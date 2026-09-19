using System.Collections.Concurrent;
using System.Diagnostics;
using MotorLoadBench.Domain;

namespace MotorLoadBench.Application;

public sealed class BenchRuntime(IRealtimeBridge bridge, BenchConfig config, ISessionRecorder recorder) : IAsyncDisposable
{
    private readonly object _gate = new();
    private readonly SafetyManager _safety = new(config);
    private BenchCommand _command = new(TorqueRampNmPerSec: config.Limits.StopRampNmPerSec);
    private CancellationTokenSource? _cts;
    private Task? _loop;
    private uint _heartbeat;
    private string? _lastAlarm;
    public BenchSnapshot Latest { get; private set; } = new();
    public ConcurrentQueue<Alarm> Alarms { get; } = new();
    public string PointId { get; set; } = "manual";
    public bool CanWrite => bridge.CanWrite;
    public bool IsSimulation => bridge.IsSimulation;
    public bool IsRunning => _loop is { IsCompleted: false };
    public void Log(string code, string text) => recorder.Event(code, text);
    public async Task StartAsync(CancellationToken ct)
    {
        if (_cts != null) throw new InvalidOperationException("已连接");
        await bridge.ConnectAsync(ct);
        _cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        _loop = Task.Run(() => LoopAsync(_cts.Token), CancellationToken.None);
        Log("connected", bridge.IsSimulation ? "仿真模式" : "ADS连接；重新连接不会恢复加载");
    }
    public void Enable()
    {
        RequireHealthy();
        lock (_gate) _command = new(EnableRequest: true, TorqueRampNmPerSec: config.Limits.StopRampNmPerSec);
        Log("enable_request", "零转矩使能请求");
    }
    public void Load(double torque, double ramp, LoadMode mode = LoadMode.ConstantTorque, double power = 0)
    {
        RequireHealthy();
        if (!Latest.ServoOn) throw new InvalidOperationException("先确认联锁并Servo ON");
        if (!double.IsFinite(torque) || torque < 0 || torque > config.Limits.MaxTorqueNm ||
            !double.IsFinite(ramp) || ramp <= 0 || ramp > config.Limits.MaxRampNmPerSec ||
            !double.IsFinite(power) || power < 0 || power > config.Limits.MaxPowerW) throw new ArgumentException("请求超出当前安全配置");
        lock (_gate) _command = new(TargetTorqueNm: torque, TorqueRampNmPerSec: ramp, Mode: mode, TargetPowerW: power);
        Log("load_request", $"{mode}: {torque:F3} Nm, {power:F1} W, ramp {ramp:F3}");
    }
    public void RequestStop(bool disable = false)
    {
        lock (_gate) _command = new(DisableRequest: disable, StopRequest: true, TorqueRampNmPerSec: config.Limits.StopRampNmPerSec);
    }
    public void ResetFault()
    {
        if (!CanWrite || !Latest.Connected || Latest.ServoOn) throw new InvalidOperationException("复位需要连接且伺服已下使能");
        lock (_gate) _command = new(ResetFault: true, StopRequest: true, TorqueRampNmPerSec: config.Limits.StopRampNmPerSec);
        Log("reset_request", "用户显式故障复位，不恢复加载");
    }
    public async Task StopAsync(bool disable, CancellationToken ct)
    {
        RequestStop(disable);
        var watch = Stopwatch.StartNew();
        int stable = 0;
        while (watch.Elapsed.TotalSeconds < config.Limits.StopTimeoutSeconds)
        {
            ct.ThrowIfCancellationRequested();
            var s = Latest;
            if (!s.Connected) throw new SafetyTripException("连接已断开；无法确认实际转矩回零，请检查PLC/硬件停机链");
            stable = Math.Abs(s.ActualTorqueNm) <= config.Limits.ZeroTorqueNm && Math.Abs(s.TargetTorqueNm) <= config.Limits.ZeroTorqueNm && (!disable || !s.ServoOn) ? stable + 1 : 0;
            if (stable >= 3) { Log("stop_confirmed", "实际/目标转矩连续回零" + (disable ? "，已下使能" : "")); return; }
            await Task.Delay(30, ct);
        }
        RequestStop(true);
        throw new SafetyTripException("回零确认超时，已请求禁能；检查硬件停机，不得自动继续");
    }
    private void RequireHealthy()
    {
        if (!CanWrite) throw new InvalidOperationException("当前为ADS只读，禁止写入");
        if (!Latest.Connected || !Latest.EtherCatOnline || !Latest.DriveReady || Latest.State == BenchState.Fault || Latest.Interlocks != 0 ||
            DateTimeOffset.UtcNow - Latest.Timestamp > TimeSpan.FromMilliseconds(config.Limits.SnapshotTimeoutMs)) throw new SafetyTripException("状态过期或联锁未满足");
        recorder.CheckHealth();
    }
    private void Alarm(string code, string message)
    {
        if (_lastAlarm == code + message) return;
        _lastAlarm = code + message;
        Alarms.Enqueue(new(DateTimeOffset.UtcNow, code, message));
        while (Alarms.Count > 200) Alarms.TryDequeue(out _);
        try { recorder.Event(code, message); } catch (Exception) { /* recorder failure already forces stop */ }
    }
    private async Task LoopAsync(CancellationToken ct)
    {
        bool connected = true;
        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(10));
        try
        {
            while (await timer.WaitForNextTickAsync(ct))
            {
                try
                {
                    if (!connected)
                    {
                        await Task.Delay(config.Ads.ReconnectMs, ct);
                        await bridge.ConnectAsync(ct); connected = true; _safety.Reset();
                        RequestStop(true);
                        Alarm("reconnected", "已重新连接，保持零转矩/禁能；需用户重新确认");
                    }
                    BenchCommand c;
                    lock (_gate)
                    {
                        c = _command with { Heartbeat = ++_heartbeat };
                        _command = _command with { EnableRequest = false, ResetFault = false };
                    }
                    if (bridge.CanWrite) await bridge.WriteCommandAsync(c, ct);
                    var s = await bridge.ReadSnapshotAsync(ct);
                    Latest = s;
                    var fault = _safety.Evaluate(s, DateTimeOffset.UtcNow);
                    if (fault != null) { RequestStop(true); Alarm("safety", fault); }
                    recorder.CheckHealth(); recorder.Record(s, PointId);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
                catch (Exception ex)
                {
                    RequestStop(true);
                    Alarm("acquisition", ex.Message);
                    // Attempt zero command before closing; failure is handled by PLC watchdog.
                    try { if (bridge.CanWrite) await bridge.WriteCommandAsync(new(DisableRequest: true, StopRequest: true, TorqueRampNmPerSec: config.Limits.StopRampNmPerSec, Heartbeat: ++_heartbeat), ct); } catch (Exception) { }
                    Latest = Latest with { Connected = false, State = BenchState.Disconnected };
                    await bridge.DisconnectAsync(); connected = false;
                }
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
    }
    public async ValueTask DisposeAsync()
    {
        RequestStop(true);
        if (_cts != null) { await _cts.CancelAsync(); if (_loop != null) await _loop; _cts.Dispose(); }
        await bridge.DisposeAsync(); await recorder.DisposeAsync();
    }
}
