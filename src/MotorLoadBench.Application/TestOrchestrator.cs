using System.Diagnostics;
using MotorLoadBench.Domain;

namespace MotorLoadBench.Application;

public enum AutoTestStage { IDLE, PRECHECK, TORQUE_ZERO, DIRECTION_CHECK, NO_LOAD, LOAD_SWEEP, WAIT_STABLE, SAMPLE, STALL_APPROACH, RAMP_DOWN, DUT_STOP, ANALYSIS, FINISHED, FAULT }
internal sealed class EndOfSweepException(string reason) : Exception(reason);

public sealed class TestOrchestrator(BenchRuntime runtime, BenchConfig config, ISessionRecorder recorder)
{
    public AutoTestStage Stage { get; private set; } = AutoTestStage.IDLE;
    public string Phase { get; private set; } = "IDLE";
    public string EndReason { get; private set; } = "NOT_STARTED";
    public string? StopDetail { get; private set; }
    public BenchSnapshot? StopSnapshot { get; private set; }
    public string CurrentPoint { get; private set; } = "—";
    public double? TargetTorqueNm { get; private set; }
    public bool Running { get; private set; }
    public event Action<PointResult>? PointCompleted;
    public event Action? StatusChanged;

    public void ReturnToIdleAfterConfirmedStop()
    {
        if (Running) throw new InvalidOperationException("测试仍在运行，不能恢复待机状态");
        Stage = AutoTestStage.IDLE;
        Phase = "IDLE";
        CurrentPoint = "—";
        TargetTorqueNm = null;
        Changed();
    }

    public async Task RunAsync(Recipe recipe, CancellationToken ct,
        Func<CancellationToken, Task>? precheck = null,
        Func<TestPoint, CancellationToken, Task>? preparePoint = null,
        Func<double?, CancellationToken, Task>? stopDut = null,
        Func<CancellationToken, Task>? analyze = null,
        Action? ensureExternalSafety = null)
    {
        if (Running) throw new InvalidOperationException("配方正在运行");
        BenchConfig.ValidateRecipe(recipe, config.Limits);
        Running = true; EndReason = "RUNNING";
        Notify(AutoTestStage.PRECHECK, "PRECHECK · 重新检查全部设备与参数");
        try
        {
            runtime.Log("recipe_start", recipe.Name);
            if (precheck != null) await precheck(ct);
            Notify(AutoTestStage.TORQUE_ZERO, "TORQUE_ZERO · 伺服目标扭矩回零");
            await runtime.StopAsync(false, ct);
            var first = recipe.Points[0]; CurrentPoint = first.Id;
            Notify(AutoTestStage.DIRECTION_CHECK, $"DIRECTION_CHECK · DUT Duty {config.AutoTest.DutDriveValue:P0} 固定驱动，自然加速");
            if (preparePoint != null) await preparePoint(first, ct);
            Notify(AutoTestStage.NO_LOAD, "NO_LOAD · 记录空载基准");
            var noLoadResult = await WaitNoLoadAsync(first, ct, ensureExternalSafety);
            await recorder.SaveResultAsync(noLoadResult, ct); PointCompleted?.Invoke(noLoadResult);
            var noLoadRpm = Math.Abs(Current().DutSpeedRpm!.Value);
            try
            {
                var adaptive = recipe.Points.Count > 1;
                var sweepPoints = new LinkedList<TestPoint>(recipe.Points);
                for (var node = sweepPoints.First; node != null; node = node.Next)
                {
                    var point = node.Value;
                    CurrentPoint = point.Id; TargetTorqueNm = point.TorqueNm; Changed();
                    var attempts = point.OnFail == OnFail.RetryOnce ? 2 : 1;
                    for (var attempt = 1; attempt <= attempts; attempt++)
                    {
                        runtime.PointId = point.Id;
                        try { await RunPointAsync(point, attempt, noLoadRpm, ct, ensureExternalSafety); break; }
                        catch (EndOfSweepException) { throw; }
                        catch (Exception ex)
                        {
                            await recorder.SaveResultAsync(new(point.Id, attempt, ex is OperationCanceledException ? "ABORTED" : ex is PointFailedException ? "UNSTABLE" : "FAIL", ex.Message,
                                null, null, null, Path.Combine(recorder.DirectoryPath, "raw.csv")), CancellationToken.None);
                            runtime.RequestStop();
                            if (ex is not PointFailedException) throw;
                            await runtime.StopAsync(false, ct);
                            if (point.OnFail == OnFail.Skip) break;
                            if (attempt == attempts) throw;
                            runtime.Log("retry", $"{point.Id} 仅重试一次");
                        }
                    }
                    if (adaptive && node.Next is { } next)
                    {
                        var rpm = Math.Abs(Current().DutSpeedRpm!.Value);
                        var step = rpm < 500 ? .02 : rpm < 1000 ? .05 : rpm > 2000 ? .15 : config.AutoTest.TorqueStepNm;
                        var desired = Math.Round(point.TorqueNm + step, 3, MidpointRounding.AwayFromZero);
                        if (desired < next.Value.TorqueNm - 1e-6 && desired <= config.AutoTest.TorqueRangeMaxNm)
                        {
                            var inserted = point with
                            {
                                Id = $"T{desired:0.00}".Replace('.', '_'),
                                TorqueNm = desired,
                                TorqueToleranceNm = Math.Max(.02, desired * .02)
                            };
                            sweepPoints.AddAfter(node, inserted);
                            runtime.Log("adaptive_torque_step", $"RPM={rpm:F1}; next torque={desired:F3} N·m; step={step:F3} N·m");
                        }
                    }
                    if (point.TorqueNm >= config.AutoTest.TorqueRangeMaxNm - 1e-9) throw new EndOfSweepException("MAX_TORQUE_REACHED");
                }
            }
            catch (EndOfSweepException ex)
            {
                EndReason = ex.Message switch { "LOW_SPEED_STOP" => "RPM_LOW_LIMIT", "STALL_APPROACH_COMPLETE" => "COMPLETED_STALL_200_RPM",
                    "STALL_CROSSING_COMPLETE" => "COMPLETED_STALL_CROSSING", "END_TORQUE_COMPLETE" => "COMPLETED_END_TORQUE", _ => "COMPLETED" };
                CaptureStop(EndReason, ex.Message); runtime.Log("sweep_end", EndReason);
            }
            if (EndReason == "RUNNING") { EndReason = "COMPLETED"; CaptureStop(EndReason, null); }
            await SafeStopAsync(stopDut);
            Notify(AutoTestStage.ANALYSIS, "ANALYSIS · 计算关键结果并生成文件");
            if (analyze != null) await analyze(CancellationToken.None);
            Notify(AutoTestStage.FINISHED, $"FINISHED · {EndReason}");
            runtime.Log("recipe_complete", recipe.Name);
        }
        catch (OperationCanceledException)
        {
            EndReason = "USER_ABORT"; CaptureStop(EndReason, "用户点击正常停止"); await SafeStopAsync(stopDut);
            Notify(AutoTestStage.FINISHED, "FINISHED · 正常停止");
        }
        catch (Exception ex)
        {
            EndReason = ClassifyStopReason(ex, runtime.Latest); CaptureStop(EndReason, ex.Message);
            try { await SafeStopAsync(stopDut); } catch { }
            Notify(AutoTestStage.FAULT, $"FAULT · {ex.Message}"); runtime.Log("auto_test_fault", ex.Message); throw;
        }
        finally { runtime.RequestStop(); Running = false; runtime.PointId = "manual"; TargetTorqueNm = null; Changed(); }
    }

    private void CaptureStop(string reason, string? detail)
    {
        StopSnapshot ??= runtime.Latest;
        StopDetail ??= detail;
        runtime.Log("test_stop", $"{reason}: {detail}");
    }

    private string ClassifyStopReason(Exception ex, BenchSnapshot s)
    {
        var message = ex.Message;
        if (message.Contains("Telemetry", StringComparison.OrdinalIgnoreCase) || message.Contains("通信") || message.Contains("缺少")) return "COMMUNICATION_LOST";
        if (Math.Abs(s.DutCurrentA ?? 0) >= config.Limits.MaxDutBusCurrentA || message.Contains("DC Bus Current 达到", StringComparison.OrdinalIgnoreCase)) return "DC_CURRENT_LIMIT";
        if (Math.Abs(s.DutPhaseCurrentA ?? 0) >= config.Vesc.MaxMotorCurrentA || message.Contains("Phase Current 达到", StringComparison.OrdinalIgnoreCase) || message.Contains("相电流", StringComparison.OrdinalIgnoreCase)) return "PHASE_CURRENT_LIMIT";
        if (Math.Abs((s.DutBusV ?? 0) * (s.DutCurrentA ?? 0)) >= config.Limits.MaxPowerW || message.Contains("输入功率") || message.Contains("功率达到")) return "POWER_LIMIT";
        if (Math.Abs(s.DutSpeedRpm ?? 0) >= config.Limits.MaxSpeedRpm || message.Contains("超速")) return "OVERSPEED_LIMIT";
        if (s.DutMotorTempC >= config.Limits.DutMotorTripC || s.MotorTempC >= config.Limits.MotorTripC || message.Contains("温度")) return "TEMPERATURE_LIMIT";
        if (!s.Connected || message.Contains("VESC", StringComparison.OrdinalIgnoreCase)) return "DUT_FAULT";
        if (!s.EtherCatOnline || !s.DriveReady || s.ErrorCode != 0 || message.Contains("Servo", StringComparison.OrdinalIgnoreCase)) return "SERVO_FAULT";
        if (!s.ExternalHealthy || message.Contains("DYN", StringComparison.OrdinalIgnoreCase)) return "DYN_FAULT";
        if (ex is PointFailedException) return "UNSTABLE";
        return ex is SafetyTripException ? "SAFETY_TRIP" : "FAULT";
    }

    private async Task SafeStopAsync(Func<double?, CancellationToken, Task>? stopDut)
    {
        var emergencyRamp = config.AutoTest.StallCrossingTorqueRampNmPerSec;
        if (EndReason == "COMPLETED_STALL_CROSSING")
        {
            Notify(AutoTestStage.RAMP_DOWN, "RAMP_DOWN · STALL_CROSSING 反转紧急停止：负载伺服扭矩先回零");
            await ServoZeroThenStopDutAsync(stopDut, null, emergencyRamp, "STALL_CROSSING_EMERGENCY");
            await runtime.StopAsync(true, CancellationToken.None, emergencyRamp);
            return;
        }
        if (EndReason is "TEMPERATURE_LIMIT" or "DC_CURRENT_LIMIT" or "PHASE_CURRENT_LIMIT")
        {
            Notify(AutoTestStage.RAMP_DOWN, $"RAMP_DOWN · {EndReason} 硬跳闸：负载伺服扭矩先快速回零，再停 DUT");
            Exception? failure = null;
            try { await ServoZeroThenStopDutAsync(stopDut, 0, emergencyRamp, EndReason); }
            catch (Exception ex) { failure = ex; }
            await runtime.StopAsync(true, CancellationToken.None, emergencyRamp);
            if (failure != null) throw failure;
            return;
        }
        if (EndReason is "COMPLETED_STALL_200_RPM" or "COMPLETED_END_TORQUE")
        {
            Notify(AutoTestStage.RAMP_DOWN, EndReason == "COMPLETED_END_TORQUE"
                ? $"RAMP_DOWN · DYN 实测扭矩超过结束扭矩 {config.AutoTest.EndAboveTorqueNm:F2} N·m：负载伺服扭矩先回零，再将 DUT Duty 回零"
                : "RAMP_DOWN · 失速接近点已记录：负载伺服扭矩先回零，再将 DUT Duty 回零");
            await ServoZeroThenStopDutAsync(stopDut, null, emergencyRamp, EndReason);
            return;
        }
        Notify(AutoTestStage.RAMP_DOWN, "RAMP_DOWN · 伺服扭矩斜坡回零");
        await runtime.StopAsync(false, CancellationToken.None);
        Notify(AutoTestStage.DUT_STOP, "DUT_STOP · DUT 指令回零并确认停转");
        if (stopDut == null) return;
        await stopDut(null, CancellationToken.None);
        var deadline = DateTimeOffset.UtcNow.AddSeconds(config.AutoTest.StopConfirmTimeoutSeconds);
        while (runtime.Latest.DutSpeedRpm is { } rpm && Math.Abs(rpm) > config.AutoTest.ZeroRpmTolerance)
        {
            if (DateTimeOffset.UtcNow >= deadline) throw new SafetyTripException("DUT 停转确认超时；请检查硬件急停与机械状态");
            await Task.Delay(30);
        }
    }

    // Stop order for hard stops: zero the load servo torque first (StopAsync confirms actual and
    // target torque at zero, servo stays enabled), then stop the DUT. Stopping the DUT first lets the
    // CST load reverse-drive the shaft to the servo speed limit (field 2026-10-05: ~6000 rpm reverse,
    // DUT bus 38 -> 48 V). The DUT is stopped even if the servo zero is not confirmed.
    private async Task ServoZeroThenStopDutAsync(Func<double?, CancellationToken, Task>? stopDut,
        double? dutRampSeconds, double servoRampNmPerSec, string source)
    {
        Exception? servoFailure = null;
        try { await runtime.StopAsync(false, CancellationToken.None, servoRampNmPerSec); }
        catch (Exception ex) { servoFailure = ex; runtime.Log("servo_zero_before_dut_stop_failed", $"{source}: {ex.Message}"); }
        Notify(AutoTestStage.DUT_STOP, servoFailure == null
            ? $"DUT_STOP · {source}：伺服扭矩已回零，停止 DUT"
            : $"DUT_STOP · {source}：伺服扭矩回零未确认，仍停止 DUT");
        try { if (stopDut != null) await stopDut(dutRampSeconds, CancellationToken.None); }
        catch (Exception ex)
        {
            runtime.Log("dut_emergency_stop_failed", ex.Message);
            throw new SafetyTripException("DUT 紧急停机帧发送未全部成功", ex);
        }
        if (servoFailure != null) throw new SafetyTripException("停 DUT 前伺服扭矩回零未确认", servoFailure);
    }

    private BenchSnapshot Current()
    {
        var s = runtime.Latest;
        // Desktop scheduling and test-runner load can pause the in-process mock
        // for more than the hardware's 200 ms watchdog. Keep the production
        // timeout unchanged; simulation has no physical communication link.
        var telemetryTimeoutMs = runtime.IsSimulation
            ? Math.Max(1000, config.AutoTest.TelemetryTimeoutMs)
            : config.AutoTest.TelemetryTimeoutMs;
        if (!s.Connected || !s.EtherCatOnline || !s.DriveReady || s.State == BenchState.Fault || s.Interlocks != Interlock.None || s.ErrorCode != 0 ||
            (DateTimeOffset.UtcNow - s.Timestamp).TotalMilliseconds > telemetryTimeoutMs)
            throw new SafetyTripException("关键 Telemetry 超时或设备故障，禁止继续增载");
        if (!s.ExternalHealthy || s.ExternalTorqueNm is not { } tq || !double.IsFinite(tq))
            throw new SafetyTripException("DYN-200 数据无效，禁止使用伺服估算扭矩代替");
        if (s.DutSpeedRpm is not { } rpm || !double.IsFinite(rpm) || s.DutBusV is not { } v || !double.IsFinite(v) || s.DutCurrentA is not { } i || !double.IsFinite(i))
            throw new SafetyTripException("VESC Telemetry 缺少 DUT RPM、母线电压或 DC Bus Current");
        if (s.DutFaultCode is > 0) throw new SafetyTripException($"VESC Fault Code={s.DutFaultCode}");
        if (Math.Abs(rpm) >= config.Limits.MaxSpeedRpm) throw new SafetyTripException("DUT 超速");
        if (Math.Abs(i) >= config.Limits.MaxDutBusCurrentA) throw new SafetyTripException("DC Bus Current 达到硬限制");
        if (s.DutPhaseCurrentA is { } phaseCurrent && Math.Abs(phaseCurrent) >= config.Vesc.MaxMotorCurrentA)
            throw new SafetyTripException($"DUT Phase Current 达到硬限制：{phaseCurrent:F1} A / {config.Vesc.MaxMotorCurrentA:F1} A");
        if (Math.Abs(v * i) >= config.Limits.MaxPowerW) throw new SafetyTripException("DUT 输入功率达到硬限制");
        if (s.DutMotorTempC is { } dutTemp && dutTemp >= config.Limits.DutMotorTripC)
            throw new SafetyTripException($"DUT 温度达到硬限制：{dutTemp:F1} °C / {config.Limits.DutMotorTripC:F1} °C");
        if (s.MotorTempC is { } servoTemp && servoTemp >= config.Limits.MotorTripC) throw new SafetyTripException("Servo 温度达到硬限制");
        recorder.CheckHealth(); return s;
    }

    private async Task<PointResult> WaitNoLoadAsync(TestPoint p, CancellationToken ct, Action? safety)
    {
        var deadline = DateTimeOffset.UtcNow.AddSeconds(config.AutoTest.StableTimeoutSeconds); DateTimeOffset? held = null;
        var window = new Queue<(DateTimeOffset Time, BenchSnapshot Sample)>();
        while (true)
        {
            ct.ThrowIfCancellationRequested(); safety?.Invoke(); var s = Current();
            window.Enqueue((DateTimeOffset.UtcNow, s));
            while (window.Count > 0 && DateTimeOffset.UtcNow - window.Peek().Time > TimeSpan.FromSeconds(config.AutoTest.PointSampleWindowSeconds)) window.Dequeue();
            var samples = window.Select(x => x.Sample).ToArray();
            var meanTorque = samples.Length == 0 ? 0 : samples.Average(x => Math.Abs(x.ExternalTorqueNm!.Value));
            var ok = Math.Abs(s.DutSpeedRpm!.Value) >= config.AutoTest.LowSpeedStopRpm && Stable(samples, meanTorque);
            held = ok ? held ?? DateTimeOffset.UtcNow : null;
            if (held is { } start && DateTimeOffset.UtcNow - start >= TimeSpan.FromSeconds(config.AutoTest.StableHoldSeconds))
            {
                var torque = Statistics.From(samples.Select(x => Math.Abs(x.ExternalTorqueNm!.Value)));
                var speed = Statistics.From(samples.Select(x => Math.Abs(x.DutSpeedRpm!.Value)));
                var power = samples.Select(x => Math.Abs(x.ExternalTorqueNm!.Value * x.DutSpeedRpm!.Value * 2 * Math.PI / 60)).ToArray();
                var efficiency = samples.Where(x => x.DutElectricalInputPowerW is > 1e-6).Select(x => Math.Abs(x.ExternalTorqueNm!.Value * x.DutSpeedRpm!.Value * 2 * Math.PI / 60) / x.DutElectricalInputPowerW!.Value * 100).ToArray();
                return new PointResult("NO_LOAD", 1, "PASS", null, torque, speed, Statistics.From(power),
                    Path.Combine(recorder.DirectoryPath, "raw.csv"), efficiency.Length > 1 ? Statistics.From(efficiency) : null,
                    TorqueSource.ExternalSensor, Statistics.From(samples.Select(x => Math.Abs(x.DutCurrentA!.Value))),
                    OptionalStatistics(samples, x => x.DutIqCommandA), OptionalStatistics(samples, x => x.DutPhaseCurrentA),
                    OptionalStatistics(samples, x => x.DutBusV), OptionalStatistics(samples, x => x.DutElectricalInputPowerW),
                    OptionalStatistics(samples, x => x.DutDutyCyclePct), OptionalStatistics(samples, x => x.DutMotorTempC),
                    Statistics.From(samples.Select(x => x.TargetTorqueNm)), Statistics.From(samples.Select(x => x.ActualTorqueNm)),
                    OptionalStatistics(samples, x => x.DutControllerTempC), samples.Select(x => x.DutFaultCode).FirstOrDefault(x => x is > 0));
            }
            if (DateTimeOffset.UtcNow >= deadline)
                throw new PointFailedException($"空载基准不稳定：RPM={s.DutSpeedRpm:F1}，DYN={s.ExternalTorqueNm:F3} N·m，Ibus={s.DutCurrentA:F2} A；{StabilityDetail(samples, meanTorque)}");
            await Task.Delay(20, ct);
        }
    }

    private async Task RunPointAsync(TestPoint p, int attempt, double noLoadRpm, CancellationToken ct, Action? safety)
    {
        var watch = Stopwatch.StartNew(); Notify(AutoTestStage.LOAD_SWEEP, $"LOAD_SWEEP · {p.Id} · 目标 {p.TorqueNm:F3} N·m");
        var current = Current();
        if (Math.Abs(current.DutSpeedRpm!.Value) < config.AutoTest.LowSpeedStopRpm) throw new EndOfSweepException("LOW_SPEED_STOP");
        var requested = p.TorqueNm;
        TargetTorqueNm = requested; runtime.Load(requested, p.RampNmPerSec, requester: ControlOwner.AutoTest);
        var deadline = DateTimeOffset.UtcNow.AddSeconds(Math.Min(p.MaxDurationSeconds, config.AutoTest.StableTimeoutSeconds));
        var rpmTrend = new Queue<(DateTimeOffset Time, BenchSnapshot Sample)>();
        var crossingWindow = new Queue<(DateTimeOffset Time, BenchSnapshot Sample)>();
        DateTimeOffset? stallThresholdSince = null;
        var endWatch = new EndTorqueWatch(config.AutoTest.EndAboveTorqueNm, config.AutoTest.StallConfirmSeconds);
        var lastHighLoadTrace = DateTimeOffset.MinValue;
        List<BenchSnapshot> samples;
        while (true)
        {
            Notify(AutoTestStage.WAIT_STABLE, $"WAIT_STABLE · {p.Id}");
            var window = new Queue<(DateTimeOffset Time, BenchSnapshot Sample)>();
            DateTimeOffset? held = null;
            while (true)
            {
                ct.ThrowIfCancellationRequested(); safety?.Invoke(); var s = Current();
                window.Enqueue((DateTimeOffset.UtcNow, s));
                rpmTrend.Enqueue((DateTimeOffset.UtcNow, s));
                crossingWindow.Enqueue((DateTimeOffset.UtcNow, s));
                while (window.Count > 0 && DateTimeOffset.UtcNow - window.Peek().Time > TimeSpan.FromSeconds(config.AutoTest.PointSampleWindowSeconds)) window.Dequeue();
                while (rpmTrend.Count > 0 && DateTimeOffset.UtcNow - rpmTrend.Peek().Time > TimeSpan.FromSeconds(3)) rpmTrend.Dequeue();
                while (crossingWindow.Count > 0 && DateTimeOffset.UtcNow - crossingWindow.Peek().Time > TimeSpan.FromMilliseconds(500)) crossingWindow.Dequeue();
                var now = DateTimeOffset.UtcNow;
                if (requested >= 2.0 && now - lastHighLoadTrace >= TimeSpan.FromMilliseconds(50))
                {
                    runtime.Log("high_load_trace", $"point={p.Id}; stage=WAIT_STABLE; requested={requested:F3}; " +
                        $"servoTarget={s.TargetTorqueNm:F3}; servoActual={s.ActualTorqueNm:F3}; servoRpm={s.SpeedRpm:F1}; " +
                        $"dynTorque={s.ExternalTorqueNm:F3}; dynRpm={s.ExternalSpeedRpm:F1}; dutRpm={s.DutSpeedRpm:F1}; " +
                        $"vbus={s.DutBusV:F2}; ibus={s.DutCurrentA:F2}; iphase={s.DutPhaseCurrentA:F2}; " +
                        $"pin={s.DutElectricalInputPowerW:F1}; duty={s.DutDutyCyclePct:F2}; motorTemp={s.DutMotorTempC:F1}; " +
                        $"controllerTemp={s.DutControllerTempC:F1}; vescFault={s.DutFaultCode}");
                    lastHighLoadTrace = now;
                }
                var signedServoRpm = s.SpeedRpm * config.Drive.ExpectedRotationSign;
                var previousDynRpm = crossingWindow.Count > 1 ? Math.Abs(crossingWindow.ElementAt(crossingWindow.Count - 2).Sample.ExternalSpeedRpm ?? double.NaN) : double.NaN;
                var dynEnteredStallBandRapidly = double.IsFinite(previousDynRpm) && previousDynRpm > 500 &&
                    Math.Abs(s.ExternalSpeedRpm ?? double.MaxValue) <= config.AutoTest.StallStopRpm;
                if (signedServoRpm <= -config.Limits.LoadedStartReverseTripRpm || dynEnteredStallBandRapidly)
                {
                    runtime.Log("stall_crossing_detected", $"point={p.Id}; signedServoRpm={signedServoRpm:F1}; previousDynRpm={previousDynRpm:F1}; " +
                        $"dynRpm={s.ExternalSpeedRpm:F1}; dutRpm={s.DutSpeedRpm:F1}; dynTorque={s.ExternalTorqueNm:F3}");
                    runtime.RequestStop(false, config.AutoTest.StallCrossingTorqueRampNmPerSec, "STALL_CROSSING_DETECT");
                    await SaveStallCrossingPointAsync(p, attempt, crossingWindow.Select(x => x.Sample), ct);
                    throw new EndOfSweepException("STALL_CROSSING_COMPLETE");
                }
                EnforceLoadedSafety(s, noLoadRpm);
                if (endWatch.Reached(s, now)) await EndAboveTorqueAsync(p, attempt, endWatch.Above, ct);
                if (Math.Abs(s.DutSpeedRpm!.Value) <= config.AutoTest.StallStopRpm) stallThresholdSince ??= now;
                else stallThresholdSince = null;
                if (stallThresholdSince is { } thresholdStart && now - thresholdStart >= TimeSpan.FromSeconds(config.AutoTest.StallConfirmSeconds))
                {
                    await RunStallApproachAsync(p, attempt, ct, safety);
                    throw new EndOfSweepException("STALL_APPROACH_COMPLETE");
                }
                if (rpmTrend.Count >= 12 && IsSustainedRpmDecline(rpmTrend.Select(x => x.Sample)))
                {
                    await RunStallApproachAsync(p, attempt, ct, safety);
                    throw new EndOfSweepException("STALL_APPROACH_COMPLETE");
                }
                held = Stable(window.Select(x => x.Sample), requested) ? held ?? DateTimeOffset.UtcNow : null;
                if (held is { } start && DateTimeOffset.UtcNow - start >= TimeSpan.FromSeconds(config.AutoTest.StableHoldSeconds)) break;
                if (DateTimeOffset.UtcNow >= deadline)
                {
                    if (IsSustainedRpmDecline(rpmTrend.Select(x => x.Sample)))
                    {
                        await RunStallApproachAsync(p, attempt, ct, safety);
                        throw new EndOfSweepException("STALL_APPROACH_COMPLETE");
                    }
                    throw new PointFailedException($"UNSTABLE：RPM / Torque / Ibus 未在 Stable Timeout 内满足窗口；{StabilityDetail(window.Select(x => x.Sample), requested)}");
                }
                await Task.Delay(20, ct);
            }

            Notify(AutoTestStage.SAMPLE, $"SAMPLE · {p.Id}");
            var startSample = watch.Elapsed.TotalSeconds;
            var duration = Math.Max(config.AutoTest.PointSampleWindowSeconds, p.SampleSeconds);
            samples = []; DateTimeOffset? last = null;
            while (watch.Elapsed.TotalSeconds - startSample < duration)
            {
                ct.ThrowIfCancellationRequested(); safety?.Invoke(); var s = Current(); EnforceLoadedSafety(s, noLoadRpm);
                if (endWatch.Reached(s, DateTimeOffset.UtcNow)) await EndAboveTorqueAsync(p, attempt, endWatch.Above, ct);
                if (s.Timestamp != last) { samples.Add(s); last = s.Timestamp; }
                await Task.Delay(10, ct);
            }
            if (samples.Count >= 2 && Stable(samples, requested)) break;

            var detail = StabilityDetail(samples, requested);
            runtime.Log("sample_rejected", $"{p.Id}: discard unstable sample window; {detail}");
            if (DateTimeOffset.UtcNow >= deadline)
            {
                if (IsSustainedRpmDecline(rpmTrend.Select(x => x.Sample)))
                {
                    await RunStallApproachAsync(p, attempt, ct, safety);
                    throw new EndOfSweepException("STALL_APPROACH_COMPLETE");
                }
                throw new PointFailedException($"UNSTABLE：采样窗口持续波动且重试超时；{detail}");
            }
        }
        var torque = Statistics.From(samples.Select(s => Math.Abs(s.ExternalTorqueNm!.Value)));
        var speed = Statistics.From(samples.Select(s => Math.Abs(s.DutSpeedRpm!.Value)));
        var powers = samples.Select(s => Math.Abs(s.ExternalTorqueNm!.Value * s.DutSpeedRpm!.Value * 2 * Math.PI / 60)).ToArray();
        var efficiencies = samples.Select((s, i) => powers[i] / Math.Abs(s.DutBusV!.Value * s.DutCurrentA!.Value) * 100).ToArray();
        var warning = efficiencies.Any(x => x > 100) ? "DATA_QUALITY_WARNING: efficiency > 100%" : null;
        var result = new PointResult(p.Id, attempt, warning == null ? "PASS" : "PASS_WITH_WARNING", warning, torque, speed, Statistics.From(powers),
            Path.Combine(recorder.DirectoryPath, "raw.csv"), Statistics.From(efficiencies), TorqueSource.ExternalSensor,
            Statistics.From(samples.Select(s => Math.Abs(s.DutCurrentA!.Value))),
            OptionalStatistics(samples, s => s.DutIqCommandA), OptionalStatistics(samples, s => s.DutPhaseCurrentA),
            OptionalStatistics(samples, s => s.DutBusV), OptionalStatistics(samples, s => s.DutElectricalInputPowerW),
            OptionalStatistics(samples, s => s.DutDutyCyclePct), OptionalStatistics(samples, s => s.DutMotorTempC),
            Statistics.From(samples.Select(s => s.TargetTorqueNm)), Statistics.From(samples.Select(s => s.ActualTorqueNm)),
            OptionalStatistics(samples, s => s.DutControllerTempC), samples.Select(s => s.DutFaultCode).FirstOrDefault(x => x is > 0));
        await recorder.SaveResultAsync(result, ct); PointCompleted?.Invoke(result);
    }

    // Measured shaft torque for EndAboveTorqueNm: the DYN-200 reading, not the servo command/estimate.
    private static double? MeasuredTorqueNm(BenchSnapshot s) =>
        s.ExternalHealthy && s.ExternalTorqueNm is { } dyn && double.IsFinite(dyn) ? Math.Abs(dyn) : null;

    private sealed class EndTorqueWatch(double endAboveTorqueNm, double confirmSeconds)
    {
        private readonly List<BenchSnapshot> _above = [];
        private DateTimeOffset? _since;
        public IReadOnlyList<BenchSnapshot> Above => _above;

        // True once the measured torque has stayed above the end torque for the confirm time.
        public bool Reached(BenchSnapshot s, DateTimeOffset now)
        {
            if (endAboveTorqueNm <= 0) return false;
            if (MeasuredTorqueNm(s) is not { } nm || nm <= endAboveTorqueNm) { _since = null; _above.Clear(); return false; }
            _since ??= now; _above.Add(s);
            return now - _since.Value >= TimeSpan.FromSeconds(confirmSeconds);
        }
    }

    private static bool IsSustainedRpmDecline(IEnumerable<BenchSnapshot> source)
    {
        var rpm = source.Where(s => s.DutSpeedRpm is { }).Select(s => Math.Abs(s.DutSpeedRpm!.Value)).ToArray();
        if (rpm.Length < 12) return false;
        var mean = rpm.Average();
        var falling = 0; var rising = 0;
        for (var i = 1; i < rpm.Length; i++)
        {
            var delta = rpm[i] - rpm[i - 1];
            if (delta < -1) falling++;
            else if (delta > 1) rising++;
        }
        var netDrop = rpm[0] - rpm[^1];
        return netDrop >= Math.Max(30, mean * .03) && falling >= rising * 2 && falling >= (rpm.Length - 1) * .55;
    }

    private async Task RunStallApproachAsync(TestPoint point, int attempt, CancellationToken ct, Action? safety)
    {
        Notify(AutoTestStage.STALL_APPROACH, $"STALL_APPROACH · {point.Id} · 保持 {point.TorqueNm:F3} N·m");
        runtime.Log("stall_approach", $"Hold servo torque {point.TorqueNm:F3} N·m; wait RPM <= {config.AutoTest.StallStopRpm:F0}");
        var finalWindow = new Queue<(DateTimeOffset Time, BenchSnapshot Sample)>();
        DateTimeOffset? belowSince = null;
        var endWatch = new EndTorqueWatch(config.AutoTest.EndAboveTorqueNm, config.AutoTest.StallConfirmSeconds);
        while (true)
        {
            ct.ThrowIfCancellationRequested(); safety?.Invoke(); var s = Current();
            if (!s.ServoOn) throw new SafetyTripException("Servo 意外下使能");
            var now = DateTimeOffset.UtcNow;
            if (endWatch.Reached(s, now)) await EndAboveTorqueAsync(point, attempt, endWatch.Above, ct);
            finalWindow.Enqueue((now, s));
            while (finalWindow.Count > 0 && now - finalWindow.Peek().Time > TimeSpan.FromSeconds(Math.Max(.3, config.AutoTest.StallConfirmSeconds))) finalWindow.Dequeue();
            if (Math.Abs(s.DutSpeedRpm!.Value) <= config.AutoTest.StallStopRpm) belowSince ??= now; else belowSince = null;
            if (belowSince is { } start && now - start >= TimeSpan.FromSeconds(config.AutoTest.StallConfirmSeconds)) break;
            await Task.Delay(10, ct);
        }
        var samples = finalWindow.Select(x => x.Sample).ToArray();
        if (samples.Length < 2) throw new PointFailedException("STALL_APPROACH 最终采样不足");
        await SaveFinalPointAsync("STALL_FINAL", attempt, "STALL_APPROACH: RPM <= 200 confirmed", samples, ct);
    }

    // EndAboveTorqueNm reached: record the samples above the end torque as the last point (like STALL_FINAL), then end
    // the sweep; SafeStopAsync zeroes the servo torque before the DUT duty.
    private async Task EndAboveTorqueAsync(TestPoint point, int attempt, IReadOnlyList<BenchSnapshot> above, CancellationToken ct)
    {
        runtime.Log("end_torque_reached", $"point={point.Id}; dynTorque={MeasuredTorqueNm(above[^1]):F3}; " +
            $"endAboveTorqueNm={config.AutoTest.EndAboveTorqueNm:F3}; confirm={config.AutoTest.StallConfirmSeconds:F2} s");
        var samples = above.DistinctBy(s => s.Timestamp).ToArray();
        if (samples.Length >= 2)
            await SaveFinalPointAsync("END_TORQUE_FINAL", attempt, $"END_TORQUE: DYN torque > {config.AutoTest.EndAboveTorqueNm:F2} N·m confirmed", samples, ct);
        throw new EndOfSweepException("END_TORQUE_COMPLETE");
    }

    private async Task SaveFinalPointAsync(string id, int attempt, string note, BenchSnapshot[] samples, CancellationToken ct)
    {
        var torque = Statistics.From(samples.Select(s => Math.Abs(s.ExternalTorqueNm!.Value)));
        var speed = Statistics.From(samples.Select(s => Math.Abs(s.DutSpeedRpm!.Value)));
        var powers = samples.Select(s => Math.Abs(s.ExternalTorqueNm!.Value * s.DutSpeedRpm!.Value * 2 * Math.PI / 60)).ToArray();
        var efficiencies = samples.Where(s => s.DutElectricalInputPowerW is > 1e-6)
            .Select(s => Math.Abs(s.ExternalTorqueNm!.Value * s.DutSpeedRpm!.Value * 2 * Math.PI / 60) / s.DutElectricalInputPowerW!.Value * 100).ToArray();
        var result = new PointResult(id, attempt, "PASS", note, torque, speed, Statistics.From(powers),
            Path.Combine(recorder.DirectoryPath, "raw.csv"), efficiencies.Length > 1 ? Statistics.From(efficiencies) : null, TorqueSource.ExternalSensor,
            Statistics.From(samples.Select(s => Math.Abs(s.DutCurrentA!.Value))), OptionalStatistics(samples, s => s.DutIqCommandA),
            OptionalStatistics(samples, s => s.DutPhaseCurrentA), OptionalStatistics(samples, s => s.DutBusV), OptionalStatistics(samples, s => s.DutElectricalInputPowerW),
            OptionalStatistics(samples, s => s.DutDutyCyclePct), OptionalStatistics(samples, s => s.DutMotorTempC),
            Statistics.From(samples.Select(s => s.TargetTorqueNm)), Statistics.From(samples.Select(s => s.ActualTorqueNm)),
            OptionalStatistics(samples, s => s.DutControllerTempC), samples.Select(s => s.DutFaultCode).FirstOrDefault(x => x is > 0));
        await recorder.SaveResultAsync(result, ct); PointCompleted?.Invoke(result);
    }

    private async Task SaveStallCrossingPointAsync(TestPoint point, int attempt, IEnumerable<BenchSnapshot> source, CancellationToken ct)
    {
        var diagnostic = source.TakeLast(8).ToArray();
        var reason = diagnostic.Length == 0
            ? $"STALL_CROSSING at {point.Id}; no direction-consistent final sample"
            : $"STALL_CROSSING at {point.Id}; transient excluded from formal curve; servoRpm={diagnostic[^1].SpeedRpm:F1}; dutRpm={diagnostic[^1].DutSpeedRpm:F1}; dynRpm={diagnostic[^1].ExternalSpeedRpm:F1}";
        var result = new PointResult("STALL_TRANSIENT", attempt, "INVALID_TRANSIENT", reason, null, null, null,
            Path.Combine(recorder.DirectoryPath, "raw.csv"), null, TorqueSource.ExternalSensor);
        await recorder.SaveResultAsync(result, ct);
    }

    private static Statistics? OptionalStatistics(IEnumerable<BenchSnapshot> samples, Func<BenchSnapshot, double?> selector)
    {
        var values = samples.Select(selector).Where(x => x is { } value && double.IsFinite(value)).Select(x => x!.Value).ToArray();
        return values.Length == 0 ? null : Statistics.From(values);
    }

    private void EnforceLoadedSafety(BenchSnapshot s, double noLoadRpm)
    {
        if (!s.ServoOn) throw new SafetyTripException("Servo 意外下使能");
    }
    private bool Stable(IEnumerable<BenchSnapshot> source, double target)
    {
        var a = source.ToArray(); if (a.Length < 3) return false;
        var rpmMean = a.Average(s => Math.Abs(s.DutSpeedRpm!.Value)); var iMean = a.Average(s => Math.Abs(s.DutCurrentA!.Value));
        return RobustSpan(a.Select(s => Math.Abs(s.ExternalTorqueNm!.Value))) <= Math.Max(.02, Math.Abs(target) * config.AutoTest.TorqueStableFraction) &&
               RobustSpan(a.Select(s => Math.Abs(s.DutSpeedRpm!.Value))) <= Math.Max(1, rpmMean * config.AutoTest.RpmStableFraction) &&
               RobustStdDev(a.Select(s => Math.Abs(s.DutCurrentA!.Value))) <= Math.Max(.01, iMean * config.AutoTest.IbusStableFraction);
    }
    private static double RobustSpan(IEnumerable<double> values)
    {
        var a = values.Order().ToArray();
        if (a.Length == 0) return double.PositiveInfinity;
        var low = (int)Math.Floor((a.Length - 1) * .05);
        var high = (int)Math.Ceiling((a.Length - 1) * .95);
        return a[high] - a[low];
    }
    private static double RobustStdDev(IEnumerable<double> values)
    {
        var ordered = values.Order().ToArray();
        if (ordered.Length == 0) return double.PositiveInfinity;
        var low = (int)Math.Floor((ordered.Length - 1) * .05);
        var high = (int)Math.Ceiling((ordered.Length - 1) * .95);
        var a = ordered[low..(high + 1)];
        var mean = a.Average();
        return Math.Sqrt(a.Average(x => (x - mean) * (x - mean)));
    }
    private string StabilityDetail(IEnumerable<BenchSnapshot> source, double target)
    {
        var a = source.ToArray(); if (a.Length < 3) return "窗口样本不足";
        var rpmMean = a.Average(s => Math.Abs(s.DutSpeedRpm!.Value)); var iMean = a.Average(s => Math.Abs(s.DutCurrentA!.Value));
        var torqueLimit = Math.Max(.02, Math.Abs(target) * config.AutoTest.TorqueStableFraction);
        var rpmLimit = Math.Max(1, rpmMean * config.AutoTest.RpmStableFraction);
        var currentLimit = Math.Max(.01, iMean * config.AutoTest.IbusStableFraction);
        return $"窗口波动 Torque(P5-P95)={RobustSpan(a.Select(s => Math.Abs(s.ExternalTorqueNm!.Value))):F3}/{torqueLimit:F3} N·m，RPM(P5-P95)={RobustSpan(a.Select(s => Math.Abs(s.DutSpeedRpm!.Value))):F1}/{rpmLimit:F1}，Ibus(σ)={RobustStdDev(a.Select(s => Math.Abs(s.DutCurrentA!.Value))):F3}/{currentLimit:F3} A";
    }
    private void Notify(AutoTestStage stage, string text) { Stage = stage; Phase = text; Changed(); runtime.Log("auto_stage", stage.ToString()); }
    private void Changed() => StatusChanged?.Invoke();
}
