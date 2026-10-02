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
    private bool _safetyStopLatched;
    private BenchSnapshot _latest = new();
    public BenchSnapshot Latest => Volatile.Read(ref _latest);
    public ConcurrentQueue<Alarm> Alarms { get; } = new();
    public string PointId { get; set; } = "manual";
    public bool CanWrite => bridge.CanWrite;
    public bool CanControlVelocity => bridge is IDirectVelocityBridge { CanControlVelocity: true };
    public bool CanControlDut => bridge is IDutMotorBridge { CanControlDut: true };
    public bool CanZeroExternalTorque => bridge is IExternalTorqueZeroing;
    public IReadOnlyList<string> AvailableDutPorts => bridge is IDutMotorBridge dut ? dut.AvailableDutPorts : [];
    public bool IsSimulation => bridge.IsSimulation;
    public bool IsRunning => _loop is { IsCompleted: false };
    public ControlOwner Owner { get; private set; } = ControlOwner.Manual;
    public Func<BenchSnapshot, BenchSnapshot>? SnapshotOverlay { get; set; }
    public Func<CancellationToken, Task>? EmergencyStopDutAsync { get; set; }
    public void AcquireAutoTest()
    {
        if (Owner == ControlOwner.Safety) throw new SafetyTripException("安全控制权已锁定");
        _safetyStopLatched = false;
        Owner = ControlOwner.AutoTest;
    }
    public void ReleaseAutoTest() { if (Owner == ControlOwner.AutoTest) Owner = ControlOwner.Manual; }
    public void RequireManualAccess() => RequireOwner(ControlOwner.Manual);
    public Task ZeroExternalTorqueAsync(CancellationToken ct) => bridge is IExternalTorqueZeroing zeroing
        ? zeroing.ZeroExternalTorqueAsync(ct)
        : throw new NotSupportedException("当前 DYN-200 连接不支持软件清零");
    private void RequireOwner(ControlOwner requester)
    {
        if (Owner == ControlOwner.Safety || requester < Owner)
            throw new InvalidOperationException($"控制权被 {Owner} 占用，拒绝 {requester} 命令");
    }
    public void Log(string code, string text) => recorder.Event(code, text);
    public async Task StartAsync(CancellationToken ct)
    {
        if (_cts != null) throw new InvalidOperationException("已连接");
        await bridge.ConnectAsync(ct);
        // Publish the first transport status synchronously. Some bridges
        // finish opening before their acquisition task has produced a sample.
        // Do not return a false "connected" state with a zero timestamp.
        var firstSampleDeadline = Stopwatch.StartNew();
        while (true)
        {
            var first = await bridge.ReadSnapshotAsync(ct);
            if (first.Connected && first.Timestamp != default)
            {
                Volatile.Write(ref _latest, first);
                break;
            }
            if (firstSampleDeadline.Elapsed >= TimeSpan.FromSeconds(3))
                throw new TimeoutException("连接后3秒内未收到有效实时状态");
            await Task.Delay(10, ct);
        }
        _cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        _loop = Task.Run(() => LoopAsync(_cts.Token), CancellationToken.None);
        Log("connected", bridge.IsSimulation ? "仿真模式" : "ADS连接；重新连接不会恢复加载");
    }
    public void Enable(ControlOwner requester = ControlOwner.Manual)
    {
        RequireOwner(requester);
        RequireHealthy();
        lock (_gate) _command = new(EnableRequest: true, TorqueRampNmPerSec: config.Limits.StopRampNmPerSec);
        Log("enable_request", "零转矩使能请求");
    }
    public void Load(double torque, double ramp, LoadMode mode = LoadMode.ConstantTorque, double power = 0,
        ControlOwner requester = ControlOwner.Manual)
    {
        RequireOwner(requester);
        RequireHealthy();
        if (!Latest.ServoOn) throw new InvalidOperationException("先确认联锁并Servo ON");
        if (!double.IsFinite(torque) || torque < 0 || torque > config.Limits.MaxTorqueNm ||
            !double.IsFinite(ramp) || ramp <= 0 || ramp > config.Limits.MaxRampNmPerSec ||
            !double.IsFinite(power) || power < 0 || power > config.Limits.MaxPowerW) throw new ArgumentException("请求超出当前安全配置");
        lock (_gate) _command = new(TargetTorqueNm: torque, TorqueRampNmPerSec: ramp, Mode: mode, TargetPowerW: power);
        Log("load_request", $"{mode}: {torque:F3} Nm, {power:F1} W, ramp {ramp:F3}");
    }
    public void RequestStop(bool disable = false, double? rampNmPerSec = null, string source = "unspecified")
    {
        var ramp = rampNmPerSec ?? config.Limits.StopRampNmPerSec;
        var maximumStopRamp = Math.Max(config.Limits.MaxRampNmPerSec, config.AutoTest.StallCrossingTorqueRampNmPerSec);
        if (!double.IsFinite(ramp) || ramp <= 0 || ramp > maximumStopRamp)
            throw new ArgumentOutOfRangeException(nameof(rampNmPerSec));
        BenchCommand previous;
        lock (_gate) previous = _command;
        Log("stop_request", $"source={source}; disable={disable}; ramp={ramp:F3} N·m/s; previousTarget={previous.TargetTorqueNm:F3} N·m; " + SnapshotTrace(Latest));
        lock (_gate) _command = new(DisableRequest: disable, StopRequest: true, TorqueRampNmPerSec: ramp);
    }
    public void ResetFault()
    {
        RequireOwner(ControlOwner.Manual);
        if (!CanWrite || !Latest.Connected || Latest.ServoOn) throw new InvalidOperationException("复位需要连接且伺服已下使能");
        lock (_gate) _command = new(ResetFault: true, StopRequest: true, TorqueRampNmPerSec: config.Limits.StopRampNmPerSec);
        Owner = ControlOwner.Manual;
        Log("reset_request", "用户显式故障复位，不恢复加载");
    }
    public Task EnableVelocityAsync(CancellationToken ct, ControlOwner requester = ControlOwner.Manual)
    {
        RequireOwner(requester);
        return bridge is IDirectVelocityBridge direct ? direct.EnableVelocityAsync(ct)
            : throw new InvalidOperationException("当前连接不是 EtherCAT 直连速度模式");
    }
    public Task SetVelocityAsync(double rpm, double rampRpmPerSec, CancellationToken ct, ControlOwner requester = ControlOwner.Manual)
    {
        RequireOwner(requester);
        return bridge is IDirectVelocityBridge direct ? direct.SetVelocityAsync(rpm, rampRpmPerSec, ct)
            : throw new InvalidOperationException("当前连接不是 EtherCAT 直连速度模式");
    }
    public Task StopVelocityAsync(bool disable, CancellationToken ct) => bridge is IDirectVelocityBridge direct
        ? direct.StopVelocityAsync(disable, ct) : throw new InvalidOperationException("当前连接不是 EtherCAT 直连速度模式");
    public Task SetDutAsync(DutControlMode mode, double value, CancellationToken ct, ControlOwner requester = ControlOwner.Manual)
    {
        RequireOwner(requester);
        return bridge is IDutMotorBridge dut ? dut.SetDutAsync(mode, value, ct)
            : throw new InvalidOperationException("当前未连接 VESC/M1 控制通道");
    }
    public Task ConnectDutAsync(string portName, CancellationToken ct, ControlOwner requester = ControlOwner.Manual)
    { RequireOwner(requester); return bridge is IDutMotorBridge dut ? dut.ConnectDutAsync(portName, ct) : throw new InvalidOperationException("当前连接不支持 VESC/M1"); }
    public Task DisconnectDutAsync(ControlOwner requester = ControlOwner.Manual)
    { RequireOwner(requester); return bridge is IDutMotorBridge dut ? dut.DisconnectDutAsync() : Task.CompletedTask; }
    public Task StopDutAsync(CancellationToken ct) => bridge is IDutMotorBridge dut
        ? dut.StopDutAsync(ct) : Task.CompletedTask;
    public async Task StopAsync(bool disable, CancellationToken ct, double? rampNmPerSec = null)
    {
        RequestStop(disable, rampNmPerSec);
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
        var s = Latest;
        var ageMs = (DateTimeOffset.UtcNow - s.Timestamp).TotalMilliseconds;
        if (!s.Connected) throw new SafetyTripException("通信尚未连接");
        if (!s.EtherCatOnline) throw new SafetyTripException("EtherCAT不在线");
        if (!s.DriveReady) throw new SafetyTripException(s.DriveFaultDetail ?? $"驱动器未就绪：State={s.State}，Error=0x{s.ErrorCode:X}");
        if (s.State == BenchState.Fault || s.Interlocks != Interlock.None || s.ErrorCode != 0)
            throw new SafetyTripException(s.DriveFaultDetail ??
                $"联锁/驱动故障：Interlock=0x{(uint)s.Interlocks:X}，Error=0x{s.ErrorCode:X}，State={s.State}" +
                (s.AuxiliaryFaultCode is { } auxiliary ? $"，Aux=0x{auxiliary:X8}" : ""));
        if (s.Timestamp == default || ageMs < -1000 || ageMs > config.Limits.SnapshotTimeoutMs)
            throw new SafetyTripException($"实时状态已过期：{ageMs:F0} ms（允许 {config.Limits.SnapshotTimeoutMs} ms）");
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
                    s = SnapshotOverlay?.Invoke(s) ?? s;
                    Volatile.Write(ref _latest, s);
                    var fault = _safety.Evaluate(s, DateTimeOffset.UtcNow);
                    if (fault != null && !_safetyStopLatched)
                    {
                        _safetyStopLatched = true;
                        var immediateDutStop = SafetyManager.RequiresImmediateDutStop(fault);
                        if (immediateDutStop) Owner = ControlOwner.Safety;
                        var reverseEmergency = fault.StartsWith("SERVO_REVERSE", StringComparison.Ordinal);
                        var stopRamp = reverseEmergency ? config.AutoTest.StallCrossingTorqueRampNmPerSec : config.Limits.StopRampNmPerSec;
                        Log("safety_trip_snapshot", $"source=SafetyManager; reason={fault}; action={(immediateDutStop ? "DUT emergency stop first, then Servo Stop+Disable" : "Stop+Disable")}; {SnapshotTrace(s)}");
                        if (immediateDutStop)
                        {
                            try
                            {
                                if (EmergencyStopDutAsync != null)
                                    await EmergencyStopDutAsync(CancellationToken.None);
                                else if (bridge is IDutMotorBridge dut)
                                    await dut.StopDutAsync(CancellationToken.None);
                                else
                                    throw new InvalidOperationException("DUT 紧急停机通道不可用");
                                Log("dut_emergency_stop_sent", $"reason={fault}; repeatedZeroCurrentFrames=true");
                            }
                            catch (Exception stopEx)
                            {
                                Alarm("dut_emergency_stop_failed", stopEx.Message);
                            }
                        }
                        RequestStop(true, stopRamp, "SafetyManager:" + fault);
                        Alarm("safety", fault);
                    }
                    recorder.CheckHealth(); recorder.Record(s, PointId);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
                catch (SafetyTripException ex)
                {
                    Owner = ControlOwner.Safety;
                    // A controlled loaded-start rejection is not a transport
                    // failure. Ramp to zero/disable while keeping OP-SYNC and
                    // the EtherCAT owner alive; reconnecting can itself cause
                    // a drive synchronization fault.
                    RequestStop(true);
                    Alarm("safety", ex.Message);
                    try
                    {
                        if (bridge.CanWrite)
                            await bridge.WriteCommandAsync(new(DisableRequest: true, StopRequest: true,
                                TorqueRampNmPerSec: config.Limits.StopRampNmPerSec,
                                Heartbeat: ++_heartbeat), ct);
                    }
                    catch (Exception stopEx) { Alarm("stop", stopEx.Message); }
                }
                catch (Exception ex)
                {
                    RequestStop(true);
                    Alarm("acquisition", ex.Message);
                    // Attempt zero command before closing; failure is handled by PLC watchdog.
                    try { if (bridge.CanWrite) await bridge.WriteCommandAsync(new(DisableRequest: true, StopRequest: true, TorqueRampNmPerSec: config.Limits.StopRampNmPerSec, Heartbeat: ++_heartbeat), ct); } catch (Exception) { }
                    Volatile.Write(ref _latest, Latest with { Connected = false, State = BenchState.Disconnected });
                    await bridge.DisconnectAsync(); connected = false;
                }
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
    }

    private static string SnapshotTrace(BenchSnapshot s) =>
        $"servoTarget={s.TargetTorqueNm:F3}; servoActual={s.ActualTorqueNm:F3}; servoRpm={s.SpeedRpm:F1}; " +
        $"dynTorque={s.ExternalTorqueNm:F3}; dynRpm={s.ExternalSpeedRpm:F1}; dutRpm={s.DutSpeedRpm:F1}; " +
        $"vbus={s.DutBusV:F2}; ibus={s.DutCurrentA:F2}; iphase={s.DutPhaseCurrentA:F2}; duty={s.DutDutyCyclePct:F2}; " +
        $"dutTemp={s.DutMotorTempC:F1}; controllerTemp={s.DutControllerTempC:F1}; vescFault={s.DutFaultCode}; " +
        $"state={s.State}; interlock=0x{(uint)s.Interlocks:X}; error=0x{s.ErrorCode:X}; snapshotUtc={s.Timestamp:O}";
    public async ValueTask DisposeAsync()
    {
        RequestStop(true);
        if (_cts != null) { await _cts.CancelAsync(); if (_loop != null) await _loop; _cts.Dispose(); }
        await bridge.DisposeAsync(); await recorder.DisposeAsync();
    }
}
