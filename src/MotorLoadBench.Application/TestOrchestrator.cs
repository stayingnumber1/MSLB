using System.Diagnostics;
using MotorLoadBench.Domain;

namespace MotorLoadBench.Application;

public sealed class TestOrchestrator(BenchRuntime runtime, BenchConfig config, ISessionRecorder recorder)
{
    public string Phase { get; private set; } = "待机";
    public bool Running { get; private set; }
    public async Task RunAsync(Recipe recipe, CancellationToken ct, Action<TestPoint>? prepareSimulation = null)
    {
        if (Running) throw new InvalidOperationException("配方已运行");
        BenchConfig.ValidateRecipe(recipe, config.Limits);
        Running = true;
        try
        {
            runtime.Log("recipe_start", recipe.Name);
            foreach (var point in recipe.Points)
            {
                prepareSimulation?.Invoke(point);
                var attempts = point.OnFail == OnFail.RetryOnce ? 2 : 1;
                for (int attempt = 1; attempt <= attempts; attempt++)
                {
                    runtime.PointId = point.Id;
                    try
                    {
                        await RunPointAsync(point, attempt, ct);
                        break;
                    }
                    catch (Exception ex)
                    {
                        await recorder.SaveResultAsync(new(point.Id, attempt, ex is OperationCanceledException ? "ABORTED" : "FAIL", ex.Message,
                            null, null, null, Path.Combine(recorder.DirectoryPath, "samples.csv")), CancellationToken.None);
                        runtime.RequestStop();
                        // Safety faults and cancellation always abort, regardless of recipe policy.
                        if (ex is not PointFailedException) throw;
                        await runtime.StopAsync(false, ct);
                        if (point.OnFail == OnFail.Skip) break;
                        if (attempt == attempts) throw;
                        runtime.Log("retry", $"{point.Id}仅重试一次");
                    }
                }
            }
            Phase = "完成";
            runtime.Log("recipe_complete", recipe.Name);
        }
        catch (Exception ex) { Phase = "已中止：" + ex.Message; throw; }
        finally
        {
            runtime.RequestStop(); Running = false; runtime.PointId = "manual";
        }
    }
    private async Task RunPointAsync(TestPoint p, int attempt, CancellationToken ct)
    {
        var watch = Stopwatch.StartNew();
        BenchSnapshot Current()
        {
            ct.ThrowIfCancellationRequested();
            var s = runtime.Latest;
            if (!s.Connected || !s.EtherCatOnline || s.State == BenchState.Fault || s.Interlocks != 0 ||
                DateTimeOffset.UtcNow - s.Timestamp > TimeSpan.FromMilliseconds(config.Limits.SnapshotTimeoutMs)) throw new SafetyTripException("采样失效/联锁触发，停止整个配方");
            if (!s.ServoOn) throw new SafetyTripException("伺服意外下使能");
            if (s.MotorTempC is not { } temp || !double.IsFinite(temp)) throw new SafetyTripException("缺少有效电机温度");
            if (temp >= p.TemperatureLimitC) throw new SafetyTripException("测试点温度超限");
            if (watch.Elapsed.TotalSeconds > p.MaxDurationSeconds) throw new PointFailedException("测试点超时");
            recorder.CheckHealth();
            return s;
        }
        bool SpeedOk(BenchSnapshot s) => Math.Abs(Math.Abs(s.SpeedRpm) - p.Rpm) <= p.SpeedWindowRpm;
        bool TorqueOk(BenchSnapshot s) => Math.Abs(Math.Abs(s.ActualTorqueNm) - p.TorqueNm) <= p.TorqueToleranceNm;
        async Task WaitStable(Func<BenchSnapshot, bool> predicate)
        {
            double? since = null;
            while (true)
            {
                if (predicate(Current())) since ??= watch.Elapsed.TotalSeconds;
                else since = null;
                if (since.HasValue && watch.Elapsed.TotalSeconds - since >= p.StableSeconds) return;
                await Task.Delay(10, ct);
            }
        }
        Phase = $"{p.Id}: 等待DUT转速 {p.Rpm:F0} ± {p.SpeedWindowRpm:F0} rpm";
        await WaitStable(SpeedOk);
        runtime.Load(p.TorqueNm, p.RampNmPerSec);
        Phase = $"{p.Id}: 斜坡及稳定";
        await WaitStable(s => SpeedOk(s) && TorqueOk(s));
        Phase = $"{p.Id}: 采样";
        var start = watch.Elapsed.TotalSeconds;
        var samples = new List<BenchSnapshot>();
        uint? last = null;
        while (watch.Elapsed.TotalSeconds - start < p.SampleSeconds)
        {
            var s = Current();
            if (!SpeedOk(s) || !TorqueOk(s)) throw new PointFailedException("采样窗口中转速或转矩离开容差，数据不判PASS");
            if (s.PlcHeartbeat != last) { samples.Add(s); last = s.PlcHeartbeat; }
            await Task.Delay(10, ct);
        }
        if (samples.Count < 2) throw new PointFailedException("有效样本不足");
        var eff = samples.Where(s => s.EfficiencyPct.HasValue).Select(s => s.EfficiencyPct!.Value).ToArray();
        await runtime.StopAsync(false, ct);
        var external = samples.All(s => s.MeasuredPowerW.HasValue);
        var result = new PointResult(p.Id, attempt, "PASS", null, Statistics.From(samples.Select(s => external ? s.ExternalTorqueNm!.Value : s.ActualTorqueNm)),
            Statistics.From(samples.Select(s => s.SpeedRpm)), Statistics.From(samples.Select(s => s.MeasuredPowerW ?? s.MechanicalPowerW)),
            Path.Combine(recorder.DirectoryPath, "samples.csv"), eff.Length == samples.Count ? Statistics.From(eff) : null, external ? TorqueSource.ExternalSensor : TorqueSource.ServoEstimated);
        await recorder.SaveResultAsync(result, ct);
    }
}
