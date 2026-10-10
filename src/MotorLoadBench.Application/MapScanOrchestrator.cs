using System.Diagnostics;
using System.Runtime.ExceptionServices;
using MotorLoadBench.Domain;

namespace MotorLoadBench.Application;

public enum MapScanStage { IDLE, PRECHECK, TORQUE_ZERO, SPEED_CHANGE, SPEED_SETTLE, NO_LOAD, LOADING, WAIT_STABLE, DYN_TRIM, SAMPLE, UNLOAD, COOLING, PAUSED, STOPPING, FINISHED, FAULT }

// Map scan: the DUT speed loop holds each column speed (setDutSpeed), the CST load servo steps the
// torque through the column's pending cells. Every exit path zeroes the servo torque before the DUT
// is stopped (field 2026-10-05: stopping the DUT under CST load reverse-drove the shaft).
public sealed class MapScanOrchestrator(BenchRuntime runtime, BenchConfig config, ISessionRecorder recorder)
{
    private enum Outcome { Pass, Unreachable, Failed, Deferred }
    private sealed record PointOutcome(Outcome Kind, string? Reason, PointResult? Result);
    private sealed class ColumnAbortedException(string reason) : Exception(reason);

    private MapScanGrid _grid = null!;
    private MapScanPlan _plan = new();
    private Func<double, CancellationToken, Task> _setDutSpeed = (_, _) => Task.CompletedTask;
    private Func<CancellationToken, Task> _stopDut = _ => Task.CompletedTask;
    private Action? _externalSafety;
    private double _servoCommandNm;
    private DateTimeOffset _rampDoneAt;
    private readonly HashSet<double> _noLoadDone = [];
    // DYN closed loop feed-forward: DYN ~= gain x servo command + no-load DYN of the column.
    private readonly Dictionary<double, double> _noLoadDyn = [];
    private double _dynGain = 1.0;
    private readonly List<double> _pointSeconds = [];
    private volatile bool _pauseRequested;

    public MapScanStage Stage { get; private set; } = MapScanStage.IDLE;
    public string Phase { get; private set; } = "IDLE";
    public string EndReason { get; private set; } = "NOT_STARTED";
    public string? StopDetail { get; private set; }
    public string CurrentPoint { get; private set; } = "—";
    public double? TargetRpm { get; private set; }
    // Servo command (TargetTorqueNm) differs from the cell torque (CellTorqueNm) under the DYN closed loop.
    public double? TargetTorqueNm { get; private set; }
    public double? CellTorqueNm { get; private set; }
    public double DutSpeedCommandRpm { get; private set; }
    public bool Running { get; private set; }
    public bool Paused { get; private set; }
    public bool PauseRequested { get => _pauseRequested; set { _pauseRequested = value; Changed(); } }
    public double? AveragePointSeconds { get { lock (_pointSeconds) return _pointSeconds.Count == 0 ? null : _pointSeconds.TakeLast(20).Average(); } }
    public event Action<PointResult>? PointCompleted;
    public event Action? StatusChanged;

    public async Task RunAsync(MapScanGrid grid, CancellationToken ct,
        Func<double, CancellationToken, Task> setDutSpeed, Func<CancellationToken, Task> stopDut,
        Func<CancellationToken, Task>? precheck = null, Action? ensureExternalSafety = null)
    {
        if (Running) throw new InvalidOperationException("Map 扫描正在运行");
        MapScanPlanner.Validate(grid.Plan, config);
        _grid = grid; _plan = grid.Plan; _setDutSpeed = setDutSpeed; _stopDut = stopDut; _externalSafety = ensureExternalSafety;
        _servoCommandNm = 0; DutSpeedCommandRpm = 0; _noLoadDone.Clear(); _noLoadDyn.Clear(); _dynGain = 1.0;
        Running = true; EndReason = "RUNNING"; StopDetail = null; Paused = false;
        ExceptionDispatchInfo? failure = null;
        var emergency = false;
        try
        {
            Notify(MapScanStage.PRECHECK, "PRECHECK · 检查设备、零点与温度");
            runtime.Log("map_scan_start", $"{_plan.Name}; pending={grid.Count(MapCellState.Pending)}/{grid.Total}; columns={string.Join(",", MapScanPlanner.OrderedSpeeds(_plan))}; " +
                $"mode={_plan.LoadMode}; order={_plan.Order}; reference={_plan.TorqueReference}; pause={_plan.PauseTempC}C; resume={_plan.ResumeTempC}C");
            if (precheck != null) await precheck(ct);
            Notify(MapScanStage.TORQUE_ZERO, "TORQUE_ZERO · 伺服 CST 零扭矩使能");
            await runtime.StopAsync(false, ct);
            await EnsureServoOnAsync(ct);
            while (_grid.NextColumn() is { } rpm)
            {
                ct.ThrowIfCancellationRequested();
                await RunColumnAsync(rpm, ct);
            }
            EndReason = "COMPLETED";
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            EndReason = "USER_ABORT"; StopDetail = "用户停止";
        }
        catch (Exception ex)
        {
            emergency = true; EndReason = Classify(ex); StopDetail = ex.Message;
            runtime.Log("map_scan_fault", $"{EndReason}: {ex.Message}");
            failure = ExceptionDispatchInfo.Capture(ex);
        }
        _grid.ClearRunning();
        try { await SafeStopAsync(emergency); }
        catch (Exception stopEx)
        {
            runtime.Log("map_scan_stop_failed", stopEx.Message);
            StopDetail = StopDetail == null ? stopEx.Message : StopDetail + "；" + stopEx.Message;
            failure ??= ExceptionDispatchInfo.Capture(stopEx);
        }
        finally
        {
            Running = false; Paused = false; TargetTorqueNm = null; CellTorqueNm = null; TargetRpm = null; runtime.PointId = "manual";
        }
        runtime.Log("map_scan_end", $"{EndReason}; pass={grid.Count(MapCellState.Pass)}; unreachable={grid.Count(MapCellState.Unreachable)}; " +
            $"failed={grid.Count(MapCellState.Failed)}; pending={grid.Count(MapCellState.Pending)}; detail={StopDetail}");
        Notify(failure == null ? MapScanStage.FINISHED : MapScanStage.FAULT, (failure == null ? "FINISHED · " : "FAULT · ") + EndReason);
        failure?.Throw();
    }

    // Always start from a fresh CST zero-torque enable: a servo left enabled in CSV (velocity hold)
    // ignores torque commands and would hold the shaft against the DUT speed loop.
    private async Task EnsureServoOnAsync(CancellationToken ct)
    {
        if (runtime.Latest.ServoOn)
        {
            runtime.Log("map_servo_reenable", "Servo 已使能（模式未知）：先回零失能，再 CST 零扭矩使能");
            await runtime.StopAsync(true, ct);
        }
        runtime.Enable(ControlOwner.AutoTest);
        var deadline = DateTimeOffset.UtcNow.AddSeconds(5);
        while (!runtime.Latest.ServoOn)
        {
            if (DateTimeOffset.UtcNow >= deadline) throw new TimeoutException("CST 零扭矩使能超时：Servo 未 ON");
            await Task.Delay(30, ct);
        }
        runtime.Log("map_servo_on", "CST 零扭矩使能已确认");
    }

    private async Task RunColumnAsync(double rpm, CancellationToken ct)
    {
        TargetRpm = rpm;
        runtime.Log("map_column", $"rpm={rpm:F0}; pending={_grid.PendingCount(rpm)}");
        try
        {
            await UnloadAsync(false, ct);
            await ReachSpeedAsync(rpm, ct);
            if (_noLoadDone.Add(rpm)) await RecordNoLoadAsync(rpm, ct);
            while (_grid.NextPendingTorque(rpm) is { } torque)
            {
                ct.ThrowIfCancellationRequested();
                if (await GateAsync(rpm, ct)) continue;
                if (_plan.LoadMode == MapLoadMode.ReturnToZero && _servoCommandNm > 0)
                {
                    await UnloadAsync(false, ct);
                    await SettleOrAbortAsync(rpm, ct);
                }
                var outcome = await RunPointAsync(rpm, torque, ct);
                switch (outcome.Kind)
                {
                    case Outcome.Pass:
                        _grid.Complete(rpm, torque, MapCellState.Pass, outcome.Reason, outcome.Result);
                        break;
                    case Outcome.Unreachable:
                        _grid.Complete(rpm, torque, MapCellState.Unreachable, outcome.Reason, outcome.Result);
                        var above = _grid.MarkPendingUnreachable(rpm, torque, $"高于不可达点 {torque:F2} N·m");
                        runtime.Log("map_unreachable", $"{MapScanPlanner.PointId(rpm, torque)}: {outcome.Reason}; abovePending={above}");
                        // Lower cells queued for retest may remain; continue only once the speed is back.
                        if (_grid.PendingCount(rpm) > 0) await SettleOrAbortAsync(rpm, ct);
                        break;
                    case Outcome.Failed:
                        _grid.Complete(rpm, torque, MapCellState.Failed, outcome.Reason, outcome.Result);
                        break;
                    case Outcome.Deferred:
                        var attempts = _grid.Defer(rpm, torque, outcome.Reason ?? "温度暂停");
                        if (attempts >= 3) _grid.Complete(rpm, torque, MapCellState.Failed, $"温度反复超过暂停阈值（{attempts} 次）", null);
                        // Retry only after cooling to the resume temperature (hysteresis), not just below pause.
                        else await CoolAsync(rpm, ValidTemperature(runtime.Latest.DutMotorTempC) ?? _plan.PauseTempC, ct);
                        break;
                }
            }
        }
        catch (ColumnAbortedException ex)
        {
            var marked = _grid.MarkPendingUnreachable(rpm, null, ex.Message);
            runtime.Log("map_column_aborted", $"rpm={rpm:F0}; reason={ex.Message}; marked={marked}");
        }
        await UnloadAsync(false, ct);
    }

    // Before each point: cool down above the pause temperature, hold while paused. Returns true when
    // the pending list must be read again (the loop around it re-checks the gates).
    private async Task<bool> GateAsync(double rpm, CancellationToken ct)
    {
        if (ValidTemperature(runtime.Latest.DutMotorTempC) is { } temp && temp >= _plan.PauseTempC)
        {
            await CoolAsync(rpm, temp, ct);
            return true;
        }
        if (!_pauseRequested) return false;
        Notify(MapScanStage.PAUSED, "PAUSED · 已卸载，等待恢复");
        Paused = true; runtime.Log("map_paused", $"rpm={rpm:F0}");
        await UnloadAsync(false, ct);
        await HoldSpeedAsync(_plan.CoolingRpm, ct);
        while (_pauseRequested)
        {
            ct.ThrowIfCancellationRequested(); _externalSafety?.Invoke(); Current();
            await Task.Delay(100, ct);
        }
        Paused = false; runtime.Log("map_resumed", $"rpm={rpm:F0}");
        await ReachSpeedAsync(rpm, ct);
        return true;
    }

    private async Task CoolAsync(double rpm, double temp, CancellationToken ct)
    {
        Notify(MapScanStage.COOLING, $"COOLING · 电机 {temp:F1} °C ≥ {_plan.PauseTempC:F0} °C，冷却到 {_plan.ResumeTempC:F0} °C");
        runtime.Log("map_cooling_start", $"rpm={rpm:F0}; temp={temp:F1}; resume={_plan.ResumeTempC:F1}; coolingRpm={_plan.CoolingRpm:F0}");
        await UnloadAsync(false, ct);
        await HoldSpeedAsync(_plan.CoolingRpm, ct);
        var watch = Stopwatch.StartNew();
        while (true)
        {
            ct.ThrowIfCancellationRequested(); _externalSafety?.Invoke();
            var s = Current();
            var t = ValidTemperature(s.DutMotorTempC);
            if (t is null || t <= _plan.ResumeTempC) { runtime.Log("map_cooling_end", $"temp={t:F1}; seconds={watch.Elapsed.TotalSeconds:F0}"); break; }
            if (watch.Elapsed.TotalSeconds >= _plan.CoolingTimeoutSeconds)
                throw new SafetyTripException($"COOLING_TIMEOUT：{_plan.CoolingTimeoutSeconds:F0} s 内电机温度未降到 {_plan.ResumeTempC:F0} °C（当前 {t:F1} °C）");
            await Task.Delay(250, ct);
        }
        await ReachSpeedAsync(rpm, ct);
    }

    private async Task HoldSpeedAsync(double rpm, CancellationToken ct)
    {
        if (rpm > 0) { await ChangeSpeedAsync(rpm, ct); return; }
        await ChangeSpeedAsync(0, ct);
        await _stopDut(ct);
        DutSpeedCommandRpm = 0;
    }

    private async Task ReachSpeedAsync(double rpm, CancellationToken ct)
    {
        await ChangeSpeedAsync(rpm, ct);
        await SettleOrAbortAsync(rpm, ct);
    }

    private async Task SettleOrAbortAsync(double rpm, CancellationToken ct)
    {
        if (!await SettleSpeedAsync(rpm, ct))
            throw new ColumnAbortedException($"空载转速在 {_plan.SpeedSettleSeconds:F0} s 内未稳定到 {rpm:F0} rpm");
    }

    // Ramps the DUT speed command; the servo torque must already be zero.
    private async Task ChangeSpeedAsync(double target, CancellationToken ct)
    {
        if (Math.Abs(DutSpeedCommandRpm - target) < 1e-6 && target > 0) return;
        Notify(MapScanStage.SPEED_CHANGE, $"SPEED_CHANGE · DUT {DutSpeedCommandRpm:F0} → {target:F0} rpm");
        var interval = TimeSpan.FromMilliseconds(50);
        var step = _plan.SpeedRampRpmPerSec * interval.TotalSeconds;
        var command = DutSpeedCommandRpm;
        while (true)
        {
            ct.ThrowIfCancellationRequested(); _externalSafety?.Invoke(); Current();
            command = command < target ? Math.Min(target, command + step) : Math.Max(target, command - step);
            await _setDutSpeed(command, ct);
            DutSpeedCommandRpm = command;
            if (Math.Abs(command - target) < 1e-6) break;
            await Task.Delay(interval, ct);
        }
        runtime.Log("map_speed_command", $"rpm={target:F0}");
    }

    private async Task<bool> SettleSpeedAsync(double rpm, CancellationToken ct)
    {
        Notify(MapScanStage.SPEED_SETTLE, $"SPEED_SETTLE · 等待 {rpm:F0} rpm 稳定");
        var tolerance = Math.Max(10, rpm * _plan.SpeedDropFraction / 2);
        var deadline = DateTimeOffset.UtcNow.AddSeconds(_plan.SpeedSettleSeconds);
        DateTimeOffset? since = null;
        while (true)
        {
            ct.ThrowIfCancellationRequested(); _externalSafety?.Invoke();
            var s = Current(); var now = DateTimeOffset.UtcNow;
            since = Math.Abs(Math.Abs(s.DutSpeedRpm!.Value) - rpm) <= tolerance ? since ?? now : null;
            if (since is { } start && now - start >= TimeSpan.FromSeconds(.5)) return true;
            if (now >= deadline)
            {
                runtime.Log("map_speed_not_settled", $"target={rpm:F0}; dutRpm={s.DutSpeedRpm:F1}; tolerance={tolerance:F1}; duty={s.DutDutyCyclePct:F1}");
                return false;
            }
            await Task.Delay(20, ct);
        }
    }

    private async Task RecordNoLoadAsync(double rpm, CancellationToken ct)
    {
        var id = MapScanPlanner.NoLoadId(rpm); CurrentPoint = id; runtime.PointId = id; TargetTorqueNm = 0; CellTorqueNm = 0;
        Notify(MapScanStage.NO_LOAD, $"NO_LOAD · {id} · 空载基准");
        var samples = await CollectAsync(_plan.SampleSeconds, null, ct);
        if (samples.Outcome is { } aborted)
        {
            runtime.Log("map_no_load_skipped", $"{id}: {aborted.Reason}");
            if (aborted.Kind == Outcome.Unreachable) throw new ColumnAbortedException("空载时 " + aborted.Reason);
            return;
        }
        var result = BuildResult(id, 1, samples.Samples, "PASS", "NO_LOAD");
        if (result.Torque is { } drag) _noLoadDyn[rpm] = drag.Mean;
        Publish(result);
    }

    private async Task<PointOutcome> RunPointAsync(double rpm, double torque, CancellationToken ct)
    {
        var id = MapScanPlanner.PointId(rpm, torque);
        CurrentPoint = id; runtime.PointId = id; CellTorqueNm = torque;
        _grid.SetRunning(rpm, torque);
        var attempt = _grid.Snapshot().First(c => c.Id == id).Attempts;
        var watch = Stopwatch.StartNew();
        var closedLoop = _plan.TorqueReference == MapTorqueReference.DynClosedLoop;
        var maxCommand = closedLoop ? torque + _plan.DynCorrectionMaxNm : torque;
        var command = closedLoop ? Math.Clamp((torque - _noLoadDyn.GetValueOrDefault(rpm)) / _dynGain, 0, maxCommand) : torque;
        Notify(MapScanStage.LOADING, $"LOADING · {id} · 目标 {torque:F2} N·m · 伺服 {command:F3} N·m");
        LoadServo(command);
        var tolerance = Math.Max(.02, torque * .02);
        string? limitWarning = null;
        var deadline = _rampDoneAt.AddSeconds(_plan.StableTimeoutSeconds);
        var monitor = new LoadMonitor(rpm, _plan);
        var window = new Queue<(DateTimeOffset Time, BenchSnapshot Sample)>();
        DateTimeOffset? held = null;
        var trims = 0;
        Notify(MapScanStage.WAIT_STABLE, $"WAIT_STABLE · {id}");
        while (true)
        {
            ct.ThrowIfCancellationRequested(); _externalSafety?.Invoke();
            var s = Current(); var now = DateTimeOffset.UtcNow;
            window.Enqueue((now, s));
            while (window.Count > 0 && now - window.Peek().Time > TimeSpan.FromSeconds(_plan.StabilityWindowSeconds)) window.Dequeue();
            if (monitor.Check(s, now, _rampDoneAt) is { } stop) return await AbortPointAsync(id, attempt, stop, window.Select(x => x.Sample).ToList(), ct);
            held = now >= _rampDoneAt && Stable(window.Select(x => x.Sample).ToList(), torque, rpm) ? held ?? now : null;
            if (held is { } start && now - start >= TimeSpan.FromSeconds(config.AutoTest.StableHoldSeconds))
            {
                var dyn = window.Average(x => Math.Abs(x.Sample.ExternalTorqueNm!.Value));
                if (closedLoop && Math.Abs(torque - dyn) > tolerance)
                {
                    var corrected = Math.Clamp(command + (torque - dyn) / _dynGain, 0, maxCommand);
                    if (trims < 4 && Math.Abs(corrected - command) > .005)
                    {
                        trims++;
                        runtime.Log("map_dyn_trim", $"{id}: dyn={dyn:F3}; target={torque:F3}; servo {command:F3} -> {corrected:F3}; gain={_dynGain:F3}");
                        Notify(MapScanStage.DYN_TRIM, $"DYN_TRIM · {id} · 伺服 {corrected:F3} N·m（DYN {dyn:F3} / 目标 {torque:F2}）");
                        command = corrected; LoadServo(command);
                        window.Clear(); held = null;
                        if (deadline < _rampDoneAt.AddSeconds(3)) deadline = _rampDoneAt.AddSeconds(3);
                        continue;
                    }
                    limitWarning = corrected <= 0 && dyn > torque
                        ? $"伺服指令已为 0，DYN {dyn:F3} 仍高于目标 {torque:F2} N·m（台架空载拖曳）"
                        : $"DYN {dyn:F3} 未收敛到目标 {torque:F2} N·m（伺服 {command:F3}，修正 {trims} 次）";
                    runtime.Log("map_dyn_limit", $"{id}: {limitWarning}");
                }
                break;
            }
            if (now >= deadline)
            {
                var reason = "UNSTABLE：" + StabilityDetail(window.Select(x => x.Sample).ToList(), torque, rpm);
                return await FailPointAsync(id, attempt, reason, window.Select(x => x.Sample).ToList(), ct);
            }
            await Task.Delay(20, ct);
        }

        Notify(MapScanStage.SAMPLE, $"SAMPLE · {id}");
        var sampleDeadline = DateTimeOffset.UtcNow.AddSeconds(_plan.StableTimeoutSeconds);
        while (true)
        {
            var collected = await CollectAsync(_plan.SampleSeconds, monitor, ct);
            if (collected.Outcome is { } stop) return await AbortPointAsync(id, attempt, stop, collected.Samples, ct);
            if (collected.Samples.Count >= 2 && Stable(collected.Samples, torque, rpm))
            {
                var result = BuildResult(id, attempt, collected.Samples, "PASS", limitWarning, torque);
                if (closedLoop && command > .2 && result.Torque is { } measured && _noLoadDyn.TryGetValue(rpm, out var drag))
                {
                    var gain = (measured.Mean - drag) / command;
                    if (gain is > .8 and < 1.5) _dynGain = .7 * _dynGain + .3 * gain;
                }
                Publish(result);
                lock (_pointSeconds) _pointSeconds.Add(watch.Elapsed.TotalSeconds);
                return new(Outcome.Pass, null, result);
            }
            var detail = StabilityDetail(collected.Samples, torque, rpm);
            runtime.Log("map_sample_rejected", $"{id}: {detail}");
            if (DateTimeOffset.UtcNow >= sampleDeadline)
                return await FailPointAsync(id, attempt, "UNSTABLE：采样窗口持续波动；" + detail, collected.Samples, ct);
        }
    }

    private void LoadServo(double torque)
    {
        var from = Math.Abs(runtime.Latest.TargetTorqueNm);
        runtime.Load(torque, _plan.TorqueRampNmPerSec, requester: ControlOwner.AutoTest);
        _rampDoneAt = DateTimeOffset.UtcNow.AddSeconds(Math.Abs(torque - from) / _plan.TorqueRampNmPerSec + .15);
        _servoCommandNm = torque; TargetTorqueNm = torque; Changed();
    }

    private async Task<(List<BenchSnapshot> Samples, PointOutcome? Outcome)> CollectAsync(double seconds, LoadMonitor? monitor, CancellationToken ct)
    {
        var samples = new List<BenchSnapshot>();
        var watch = Stopwatch.StartNew(); DateTimeOffset? last = null;
        while (watch.Elapsed.TotalSeconds < seconds)
        {
            ct.ThrowIfCancellationRequested(); _externalSafety?.Invoke();
            var s = Current();
            if (monitor?.Check(s, DateTimeOffset.UtcNow, _rampDoneAt) is { } stop) return (samples, stop);
            if (monitor == null && Collapsed(s)) return (samples, new(Outcome.Unreachable, $"转速塌陷 {Math.Abs(s.DutSpeedRpm!.Value):F0} rpm", null));
            if (s.Timestamp != last) { samples.Add(s); last = s.Timestamp; }
            await Task.Delay(10, ct);
        }
        return (samples, null);
    }

    private bool Collapsed(BenchSnapshot s) => TargetRpm is { } rpm && Math.Abs(s.DutSpeedRpm!.Value) < rpm * _plan.SpeedCollapseFraction;

    // Unreachable: the servo torque goes to zero on the emergency ramp before anything else.
    // Deferred (temperature): normal ramp. The partial window is kept as a diagnostic result.
    private async Task<PointOutcome> AbortPointAsync(string id, int attempt, PointOutcome stop, List<BenchSnapshot> samples, CancellationToken ct)
    {
        await UnloadAsync(stop.Kind == Outcome.Unreachable, ct);
        var label = stop.Kind == Outcome.Unreachable ? "UNREACHABLE" : "DEFERRED";
        var result = samples.Count >= 2 ? BuildResult(id, attempt, samples, label, stop.Reason) : null;
        runtime.Log("map_point_" + label.ToLowerInvariant(), $"{id}: {stop.Reason}");
        if (result != null && stop.Kind == Outcome.Unreachable) Publish(result);
        return stop with { Result = result };
    }

    private async Task<PointOutcome> FailPointAsync(string id, int attempt, string reason, List<BenchSnapshot> samples, CancellationToken ct)
    {
        await UnloadAsync(false, ct);
        var result = samples.Count >= 2 ? BuildResult(id, attempt, samples, "UNSTABLE", reason) : null;
        runtime.Log("map_point_failed", $"{id}: {reason}");
        if (result != null) Publish(result);
        return new(Outcome.Failed, reason, result);
    }

    private async Task UnloadAsync(bool emergency, CancellationToken ct)
    {
        if (_servoCommandNm <= 0 && Math.Abs(runtime.Latest.TargetTorqueNm) <= config.Limits.ZeroTorqueNm) return;
        Notify(MapScanStage.UNLOAD, emergency ? "UNLOAD · 不可达：伺服扭矩快速回零" : "UNLOAD · 伺服扭矩回零");
        await runtime.StopAsync(false, ct, emergency ? config.AutoTest.StallCrossingTorqueRampNmPerSec : null);
        _servoCommandNm = 0; TargetTorqueNm = 0; Changed();
    }

    // Normal end: servo torque to zero (normal ramp, servo stays enabled), DUT speed ramped down, then
    // zero-current stop. Fault: servo torque to zero on the emergency ramp, DUT stopped at once
    // (even if the servo zero was not confirmed), then the servo is disabled.
    private async Task SafeStopAsync(bool emergency)
    {
        Notify(MapScanStage.STOPPING, emergency ? "STOPPING · 故障：伺服扭矩先回零，再停 DUT" : "STOPPING · 伺服扭矩回零，DUT 减速停止");
        Exception? servoFailure = null;
        try { await runtime.StopAsync(false, CancellationToken.None, emergency ? config.AutoTest.StallCrossingTorqueRampNmPerSec : null); }
        catch (Exception ex) { servoFailure = ex; runtime.Log("map_servo_zero_failed", ex.Message); }
        _servoCommandNm = 0; TargetTorqueNm = 0;
        if (!emergency && servoFailure == null && DutSpeedCommandRpm > 0)
        {
            try
            {
                var interval = TimeSpan.FromMilliseconds(50);
                var step = _plan.SpeedRampRpmPerSec * interval.TotalSeconds;
                for (var command = DutSpeedCommandRpm - step; command > 0; command -= step)
                {
                    await _setDutSpeed(command, CancellationToken.None); DutSpeedCommandRpm = command;
                    await Task.Delay(interval);
                }
            }
            catch (Exception ex) { runtime.Log("map_dut_ramp_down_failed", ex.Message); }
        }
        Exception? dutFailure = null;
        try { await _stopDut(CancellationToken.None); }
        catch (Exception ex) { dutFailure = ex; runtime.Log("map_dut_stop_failed", ex.Message); }
        DutSpeedCommandRpm = 0;
        runtime.Log("map_dut_stop_sent", $"emergency={emergency}; servoZeroConfirmed={servoFailure == null}; dutStopSent={dutFailure == null}");
        // The servo is disabled after a fault even when the DUT stop could not be sent (USB gone).
        if (emergency || dutFailure != null)
        {
            try { await runtime.StopAsync(true, CancellationToken.None, config.AutoTest.StallCrossingTorqueRampNmPerSec); }
            catch (Exception ex) { runtime.Log("map_servo_disable_failed", ex.Message); }
        }
        if (dutFailure != null) throw new SafetyTripException("DUT 停机指令发送失败；请确认 DUT 已停转", dutFailure);
        var deadline = DateTimeOffset.UtcNow.AddSeconds(config.AutoTest.StopConfirmTimeoutSeconds);
        while (runtime.Latest.DutSpeedRpm is { } rpm && Math.Abs(rpm) > config.AutoTest.ZeroRpmTolerance)
        {
            if (DateTimeOffset.UtcNow >= deadline) throw new SafetyTripException("DUT 停转确认超时；请检查硬件急停与机械状态");
            await Task.Delay(30);
        }
        if (servoFailure != null) throw new SafetyTripException("停 DUT 前伺服扭矩回零未确认", servoFailure);
    }

    private BenchSnapshot Current()
    {
        var s = runtime.Latest;
        var telemetryTimeoutMs = runtime.IsSimulation ? Math.Max(1000, config.AutoTest.TelemetryTimeoutMs) : config.AutoTest.TelemetryTimeoutMs;
        if (!s.Connected || !s.EtherCatOnline || !s.DriveReady || s.State == BenchState.Fault || s.Interlocks != Interlock.None || s.ErrorCode != 0 ||
            (DateTimeOffset.UtcNow - s.Timestamp).TotalMilliseconds > telemetryTimeoutMs)
            throw new SafetyTripException("关键 Telemetry 超时或设备故障，禁止继续加载");
        if (!s.ServoOn) throw new SafetyTripException("Servo 意外下使能");
        if (!s.ExternalHealthy || s.ExternalTorqueNm is not { } tq || !double.IsFinite(tq))
            throw new SafetyTripException("DYN-200 数据无效，禁止使用伺服估算扭矩代替");
        if (s.DutSpeedRpm is not { } rpm || !double.IsFinite(rpm) || s.DutBusV is not { } v || !double.IsFinite(v) || s.DutCurrentA is not { } i || !double.IsFinite(i))
            throw new SafetyTripException("VESC Telemetry 缺少 DUT RPM、母线电压或 DC Bus Current");
        if (s.DutFaultCode is > 0) throw new SafetyTripException($"VESC Fault Code={s.DutFaultCode}");
        if (Math.Abs(rpm) >= config.Limits.MaxSpeedRpm) throw new SafetyTripException("DUT 超速");
        if (Math.Abs(i) >= config.Limits.MaxDutBusCurrentA) throw new SafetyTripException("DC Bus Current 达到硬限制");
        if (s.DutPhaseCurrentA is { } phase && Math.Abs(phase) >= config.Vesc.MaxMotorCurrentA)
            throw new SafetyTripException($"DUT Phase Current 达到硬限制：{phase:F1} A / {config.Vesc.MaxMotorCurrentA:F1} A");
        if (Math.Abs(v * i) >= config.Limits.MaxPowerW) throw new SafetyTripException("DUT 输入功率达到硬限制");
        if (s.DutMotorTempC is { } dutTemp && dutTemp >= config.Limits.DutMotorTripC)
            throw new SafetyTripException($"DUT 温度达到硬限制：{dutTemp:F1} °C / {config.Limits.DutMotorTripC:F1} °C");
        if (s.MotorTempC is { } servoTemp && servoTemp >= config.Limits.MotorTripC) throw new SafetyTripException("Servo 温度达到硬限制");
        recorder.CheckHealth();
        return s;
    }

    // Motor temperature reads around -90 °C when the motor has no sensor (JD-X45, 2026-10-08):
    // such a reading disables the thermal gate instead of passing as "cold".
    public static double? ValidTemperature(double? value) => value is { } t && double.IsFinite(t) && t > -20 ? t : null;

    private bool Stable(IReadOnlyList<BenchSnapshot> a, double torque, double rpm)
    {
        if (a.Count < 3) return false;
        var rpmMean = a.Average(s => Math.Abs(s.DutSpeedRpm!.Value)); var iMean = a.Average(s => Math.Abs(s.DutCurrentA!.Value));
        return RobustSpan(a.Select(s => Math.Abs(s.ExternalTorqueNm!.Value))) <= Math.Max(.02, torque * config.AutoTest.TorqueStableFraction) &&
               RobustSpan(a.Select(s => Math.Abs(s.DutSpeedRpm!.Value))) <= Math.Max(1, rpmMean * config.AutoTest.RpmStableFraction) &&
               Math.Abs(rpmMean - rpm) <= rpm * _plan.SpeedDropFraction &&
               RobustStdDev(a.Select(s => Math.Abs(s.DutCurrentA!.Value))) <= Math.Max(.01, iMean * config.AutoTest.IbusStableFraction);
    }
    private string StabilityDetail(IReadOnlyList<BenchSnapshot> a, double torque, double rpm)
    {
        if (a.Count < 3) return "窗口样本不足";
        var rpmMean = a.Average(s => Math.Abs(s.DutSpeedRpm!.Value)); var iMean = a.Average(s => Math.Abs(s.DutCurrentA!.Value));
        return $"Torque(P5-P95)={RobustSpan(a.Select(s => Math.Abs(s.ExternalTorqueNm!.Value))):F3}/{Math.Max(.02, torque * config.AutoTest.TorqueStableFraction):F3} N·m，" +
               $"RPM 均值 {rpmMean:F1}/{rpm:F0}，RPM(P5-P95)={RobustSpan(a.Select(s => Math.Abs(s.DutSpeedRpm!.Value))):F1}/{Math.Max(1, rpmMean * config.AutoTest.RpmStableFraction):F1}，" +
               $"Ibus(σ)={RobustStdDev(a.Select(s => Math.Abs(s.DutCurrentA!.Value))):F3}/{Math.Max(.01, iMean * config.AutoTest.IbusStableFraction):F3} A";
    }
    private static double RobustSpan(IEnumerable<double> values)
    {
        var a = values.Order().ToArray();
        if (a.Length == 0) return double.PositiveInfinity;
        return a[(int)Math.Ceiling((a.Length - 1) * .95)] - a[(int)Math.Floor((a.Length - 1) * .05)];
    }
    private static double RobustStdDev(IEnumerable<double> values)
    {
        var ordered = values.Order().ToArray();
        if (ordered.Length == 0) return double.PositiveInfinity;
        var a = ordered[(int)Math.Floor((ordered.Length - 1) * .05)..((int)Math.Ceiling((ordered.Length - 1) * .95) + 1)];
        var mean = a.Average();
        return Math.Sqrt(a.Average(x => (x - mean) * (x - mean)));
    }

    private PointResult BuildResult(string id, int attempt, IReadOnlyList<BenchSnapshot> samples, string result, string? reason, double? targetTorqueNm = null)
    {
        var raw = Path.Combine(recorder.DirectoryPath, "raw.csv");
        if (samples.Count == 0) return new(id, attempt, result, reason, null, null, null, raw, Source: TorqueSource.ExternalSensor);
        var powers = samples.Select(s => Math.Abs(s.ExternalTorqueNm!.Value * s.DutSpeedRpm!.Value * Math.PI / 30)).ToArray();
        var efficiencies = samples.Select((s, i) => s.DutElectricalInputPowerW is > 1e-6 ? powers[i] / s.DutElectricalInputPowerW.Value * 100 : double.NaN)
            .Where(double.IsFinite).ToArray();
        if (result == "PASS" && efficiencies.Any(x => x > 100)) { result = "PASS_WITH_WARNING"; reason ??= "DATA_QUALITY_WARNING: efficiency > 100%"; }
        var dynMean = samples.Average(s => Math.Abs(s.ExternalTorqueNm!.Value));
        if (result == "PASS" && targetTorqueNm is { } target && (reason != null || Math.Abs(dynMean - target) > Math.Max(.05, target * .05)))
        {
            result = "PASS_WITH_WARNING";
            reason ??= $"DYN {dynMean:F3} 偏离目标 {target:F2} N·m";
        }
        return new PointResult(id, attempt, result, reason,
            Statistics.From(samples.Select(s => Math.Abs(s.ExternalTorqueNm!.Value))), Statistics.From(samples.Select(s => Math.Abs(s.DutSpeedRpm!.Value))),
            Statistics.From(powers), raw, efficiencies.Length > 1 ? Statistics.From(efficiencies) : null, TorqueSource.ExternalSensor,
            Statistics.From(samples.Select(s => Math.Abs(s.DutCurrentA!.Value))),
            Optional(samples, s => s.DutIqCommandA), Optional(samples, s => s.DutPhaseCurrentA), Optional(samples, s => s.DutBusV),
            Optional(samples, s => s.DutElectricalInputPowerW), Optional(samples, s => s.DutDutyCyclePct), Optional(samples, s => s.DutMotorTempC),
            Statistics.From(samples.Select(s => Math.Abs(s.TargetTorqueNm))), Statistics.From(samples.Select(s => Math.Abs(s.ActualTorqueNm))),
            Optional(samples, s => s.DutControllerTempC), samples.Select(s => s.DutFaultCode).FirstOrDefault(x => x is > 0));
    }
    private static Statistics? Optional(IEnumerable<BenchSnapshot> samples, Func<BenchSnapshot, double?> selector)
    {
        var values = samples.Select(selector).Where(x => x is { } v && double.IsFinite(v)).Select(x => x!.Value).ToArray();
        return values.Length == 0 ? null : Statistics.From(values);
    }

    private void Publish(PointResult result)
    {
        runtime.Log("map_point", $"{result.PointId}: {result.Result}; dyn={result.Torque?.Mean:F3}; rpm={result.Speed?.Mean:F1}; " +
            $"pin={result.InputPower?.Mean:F1}; pout={result.MechanicalPower?.Mean:F1}; eff={result.Efficiency?.Mean:F1}; temp={result.Temperature?.Mean:F1}; reason={result.Reason}");
        PointCompleted?.Invoke(result);
    }

    private string Classify(Exception ex)
    {
        var s = runtime.Latest; var m = ex.Message;
        if (m.StartsWith("COOLING_TIMEOUT", StringComparison.Ordinal)) return "COOLING_TIMEOUT";
        if (m.Contains("Telemetry", StringComparison.OrdinalIgnoreCase) || m.Contains("通信") || m.Contains("缺少")) return "COMMUNICATION_LOST";
        if (Math.Abs(s.DutCurrentA ?? 0) >= config.Limits.MaxDutBusCurrentA || m.Contains("DC Bus Current")) return "DC_CURRENT_LIMIT";
        if (Math.Abs(s.DutPhaseCurrentA ?? 0) >= config.Vesc.MaxMotorCurrentA || m.Contains("Phase Current")) return "PHASE_CURRENT_LIMIT";
        if (m.Contains("功率")) return "POWER_LIMIT";
        if (m.Contains("超速")) return "OVERSPEED_LIMIT";
        if (m.Contains("温度")) return "TEMPERATURE_LIMIT";
        if (m.Contains("VESC", StringComparison.OrdinalIgnoreCase)) return "DUT_FAULT";
        if (m.Contains("Servo", StringComparison.OrdinalIgnoreCase) || !s.DriveReady || s.ErrorCode != 0) return "SERVO_FAULT";
        if (m.Contains("DYN", StringComparison.OrdinalIgnoreCase)) return "DYN_FAULT";
        return ex is SafetyTripException ? "SAFETY_TRIP" : "FAULT";
    }

    private void Notify(MapScanStage stage, string text) { Stage = stage; Phase = text; Changed(); runtime.Log("map_stage", stage.ToString()); }
    private void Changed() => StatusChanged?.Invoke();

    // Unreachable / deferred detection while a point is loaded.
    private sealed class LoadMonitor(double targetRpm, MapScanPlan plan)
    {
        private static readonly TimeSpan DutyConfirm = TimeSpan.FromSeconds(.3);
        private DateTimeOffset? _dropSince, _dutySince;
        private double _dropMin;
        public PointOutcome? Check(BenchSnapshot s, DateTimeOffset now, DateTimeOffset rampDoneAt)
        {
            var rpm = Math.Abs(s.DutSpeedRpm!.Value);
            if (rpm < targetRpm * plan.SpeedCollapseFraction)
                return new(Outcome.Unreachable, $"转速塌陷 {rpm:F0}/{targetRpm:F0} rpm", null);
            // The KMO speed loop sags while the torque ramps and recovers over seconds (JD-OLD 2026-10-10:
            // -5.3 % at 12 A, 18 % duty). Only a drop that persists after the ramp without recovering counts;
            // any recovery restarts the timer.
            var below = rpm < targetRpm * (1 - plan.SpeedDropFraction);
            if (!below || now < rampDoneAt) _dropSince = null;
            else if (_dropSince == null || rpm > _dropMin + Math.Max(5, targetRpm * .005)) { _dropSince = now; _dropMin = rpm; }
            else _dropMin = Math.Min(_dropMin, rpm);
            if (_dropSince is { } dropStart && now - dropStart >= TimeSpan.FromSeconds(plan.SpeedDropConfirmSeconds))
                return new(Outcome.Unreachable, $"DUT 转速跌落 {(1 - rpm / targetRpm) * 100:F1}%（{rpm:F0}/{targetRpm:F0} rpm），斜坡结束后 {plan.SpeedDropConfirmSeconds:F1} s 未回升", null);
            var duty = Math.Abs(s.DutDutyCyclePct ?? 0);
            _dutySince = duty >= plan.DutySaturationPct ? _dutySince ?? now : null;
            if (_dutySince is { } dutyStart && now - dutyStart >= DutyConfirm)
                return new(Outcome.Unreachable, $"Duty 饱和 {duty:F1}% ≥ {plan.DutySaturationPct:F0}%", null);
            if (ValidTemperature(s.DutMotorTempC) is { } temp && temp >= plan.PauseTempC)
                return new(Outcome.Deferred, $"电机温度 {temp:F1} °C ≥ 暂停阈值 {plan.PauseTempC:F0} °C", null);
            return null;
        }
    }
}
