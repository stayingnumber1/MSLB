using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using MotorLoadBench.Application;
using MotorLoadBench.Domain;

namespace MotorLoadBench.Infrastructure;

public sealed class EtherCatDirectBridge(BenchConfig config, string root) : IRealtimeBridge, IDirectVelocityBridge
{
    private readonly object _gate = new();
    private readonly SemaphoreSlim _writer = new(1, 1);
    private Process? _process;
    private Task? _stdoutTask;
    private Task? _stderrTask;
    private BenchSnapshot _latest = new();
    private TaskCompletionSource<bool> _connected = NewSignal();
    private string? _fatal;
    private double _velocityRampRpmPerSec = 100;
    private bool _velocityControlSelected;
    private DateTimeOffset? _peakTorqueStartedAt;
    private DateTimeOffset _peakTorqueCooldownUntil;
    private readonly LoadedStartGate _loadedStartGate = new(config.Limits.LoadedStartTriggerRpm,
        config.Limits.LoadedStartTriggerConfirmMs, config.Limits.LoadedStartReverseTripRpm,
        config.Limits.LoadedStartReverseConfirmMs);

    public bool IsSimulation => false;
    public bool CanWrite => config.Drive.AllowHardwareWrites && config.Drive.RatedTorqueNm is > 0;
    public bool CanControlVelocity => _process is { HasExited: false } && _fatal == null;

    public async Task ConnectAsync(CancellationToken ct)
    {
        if (_process is { HasExited: false }) return;
        await DisconnectAsync();
        var python = Path.Combine(root, ".tools", "ethercat-python", "Scripts", "python.exe");
        var service = Path.Combine(root, "tools", "ethercat_direct_service.py");
        if (!File.Exists(python) || !File.Exists(service))
            throw new FileNotFoundException("缺少 EtherCAT 直连运行环境");

        _connected = NewSignal();
        _fatal = null;
        _latest = new();
        var start = new ProcessStartInfo(python)
        {
            WorkingDirectory = root,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };
        start.Environment["PYTHONIOENCODING"] = "utf-8";
        start.ArgumentList.Add("-u");
        start.ArgumentList.Add(service);
        start.ArgumentList.Add("--max-rpm");
        start.ArgumentList.Add(config.Limits.MaxSpeedRpm.ToString(System.Globalization.CultureInfo.InvariantCulture));
        start.ArgumentList.Add("--max-ramp-rpm-per-s");
        start.ArgumentList.Add(config.Limits.MaxSpeedRampRpmPerSec.ToString(System.Globalization.CultureInfo.InvariantCulture));
        start.ArgumentList.Add("--external-load");
        _process = Process.Start(start) ?? throw new IOException("无法启动 EtherCAT 直连主站");
        _stdoutTask = Task.Run(() => ReadOutputAsync(_process));
        _stderrTask = Task.Run(() => ReadErrorsAsync(_process));

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(10));
        try { await _connected.Task.WaitAsync(timeout.Token); }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new TimeoutException("EtherCAT 直连进入 OP/SYNC 超时" + (_fatal is null ? "" : ": " + _fatal));
        }
        if (_fatal != null) throw new IOException(_fatal);
    }

    private async Task ReadOutputAsync(Process process)
    {
        try
        {
            while (await process.StandardOutput.ReadLineAsync() is { } line)
            {
                using var json = JsonDocument.Parse(line);
                var rootElement = json.RootElement;
                var type = rootElement.GetProperty("type").GetString();
                if (type == "fatal")
                {
                    _fatal = rootElement.GetProperty("message").GetString() ?? "EtherCAT direct fatal error";
                    _connected.TrySetResult(true);
                }
                else if (type == "status")
                {
                    var s = JsonSerializer.Deserialize<DirectStatus>(line, BenchConfig.Json)!;
                    var state = s.ErrorCode != 0 ? BenchState.Fault : s.ServoOn
                        ? (Math.Abs(s.TargetRpm) > 0.5 || Math.Abs(s.TargetTorqueRaw) > 0 ? BenchState.Running : BenchState.ServoOnIdle)
                        : BenchState.DriveReady;
                    var torqueNm = config.Drive.RatedTorqueNm is > 0
                        ? s.TorqueRaw / 1000.0 * config.Drive.RatedTorqueNm.Value
                        : config.Drive.RawTorquePerNm is > 0
                            ? s.TorqueRaw / config.Drive.RawTorquePerNm.Value : 0;
                    lock (_gate)
                    {
                        _latest = new BenchSnapshot
                        {
                            Timestamp = DateTimeOffset.UtcNow,
                            Connected = s.Connected,
                            EtherCatOnline = s.EtherCatOnline,
                            DriveReady = s.DriveReady,
                            ServoOn = s.ServoOn,
                            State = state,
                            SpeedRpm = s.SpeedRpm,
                            TargetTorqueNm = s.TargetTorqueRaw / 1000.0 * (config.Drive.RatedTorqueNm ?? 0),
                            ActualTorqueNm = torqueNm,
                            DcBusV = s.DcBusV,
                            MotorTempC = s.ModuleTempC,
                            ServoCurrentA = s.PhaseCurrentA,
                            ServoPhaseCurrentA = s.PhaseCurrentA,
                            ServoLoadPct = s.LoadPercent,
                            ErrorCode = (uint)s.ErrorCode,
                            AuxiliaryFaultCode = s.AuxiliaryFault,
                            DriveFaultDetail = s.FaultDetail,
                            Interlocks = s.ErrorCode == 0 ? Interlock.None : Interlock.DriveFault,
                            PlcHeartbeat = s.Heartbeat
                        };
                    }
                    _connected.TrySetResult(true);
                }
            }
            if (!process.HasExited) _fatal = "EtherCAT direct status stream closed";
        }
        catch (Exception ex)
        {
            _fatal = ex.Message;
            _connected.TrySetResult(true);
        }
    }

    private async Task ReadErrorsAsync(Process process)
    {
        var error = await process.StandardError.ReadToEndAsync();
        if (!string.IsNullOrWhiteSpace(error)) _fatal = error.Trim();
    }

    public Task<BenchSnapshot> ReadSnapshotAsync(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (_fatal != null) throw new IOException(_fatal);
        lock (_gate) return Task.FromResult(_latest);
    }

    public async Task WriteCommandAsync(BenchCommand command, CancellationToken ct)
    {
        if (!CanWrite) throw new InvalidOperationException("CST 硬件写入尚未解锁");
        if (command.EnableRequest)
        {
            _velocityControlSelected = false;
            ResetLoadedStart();
        }
        if (_velocityControlSelected)
        {
            if (command.StopRequest || command.DisableRequest)
                await SendAsync(new { type = command.DisableRequest ? "disable" : "stop", ramp_rpm_per_s = _velocityRampRpmPerSec }, ct);
            return;
        }
        var rated = config.Drive.RatedTorqueNm!.Value;
        if (command.ResetFault)
        {
            await SendAsync(new { type = "reset" }, ct);
            return;
        }
        if (command.EnableRequest)
            await SendAsync(new { type = "enable_torque" }, ct);

        BenchSnapshot latest;
        lock (_gate) latest = _latest;
        var targetNm = command.StopRequest || command.DisableRequest ? 0 :
            LoadMath.Demand(command, config, latest.SpeedRpm, latest.MotorTempC ?? 25);
        targetNm = ApplyLoadedStartProtection(command, latest.SpeedRpm, targetNm);
        targetNm = Math.Clamp(targetNm, -config.Limits.MaxTorqueNm, config.Limits.MaxTorqueNm);
        targetNm = ApplyPeakTorqueLimit(targetNm);
        var targetRaw = (int)Math.Round(targetNm / rated * 1000);
        var rampRaw = (int)Math.Clamp(Math.Round(command.TorqueRampNmPerSec / rated * 1000), 1, 3000);
        await SendAsync(new { type = "torque", target_raw = targetRaw, ramp_raw_per_s = rampRaw, disable = command.DisableRequest }, ct);
    }

    private double ApplyLoadedStartProtection(BenchCommand command, double speedRpm, double targetNm)
    {
        if (command.StopRequest || command.DisableRequest || command.Mode != LoadMode.LoadedStart || command.TargetTorqueNm <= 0)
        {
            ResetLoadedStart();
            return targetNm;
        }
        var decision = _loadedStartGate.Update(speedRpm, config.Drive.ExpectedRotationSign, DateTimeOffset.UtcNow);
        if (decision.ReverseTrip)
        {
            ResetLoadedStart();
            throw new SafetyTripException($"带载启动检测到持续反转：{speedRpm:F1} rpm，已请求卸载");
        }
        return decision.AllowTorque ? targetNm : 0;
    }

    private void ResetLoadedStart()
    {
        _loadedStartGate.Reset();
    }

    private double ApplyPeakTorqueLimit(double targetNm)
    {
        var limits = config.Limits;
        var now = DateTimeOffset.UtcNow;
        if (Math.Abs(targetNm) <= limits.ContinuousTorqueNm)
        {
            if (_peakTorqueStartedAt.HasValue)
                _peakTorqueCooldownUntil = now.AddSeconds(limits.PeakCooldownSeconds);
            _peakTorqueStartedAt = null;
            return targetNm;
        }
        if (now < _peakTorqueCooldownUntil)
            return Math.CopySign(limits.ContinuousTorqueNm, targetNm);
        _peakTorqueStartedAt ??= now;
        if (now - _peakTorqueStartedAt.Value < TimeSpan.FromSeconds(limits.PeakSeconds))
            return targetNm;
        _peakTorqueStartedAt = null;
        _peakTorqueCooldownUntil = now.AddSeconds(limits.PeakCooldownSeconds);
        return Math.CopySign(limits.ContinuousTorqueNm, targetNm);
    }

    public Task EnableVelocityAsync(CancellationToken ct)
    {
        _velocityControlSelected = true;
        return SendAsync(new { type = "enable" }, ct);
    }

    public Task SetVelocityAsync(double rpm, double rampRpmPerSec, CancellationToken ct)
    {
        if (!double.IsFinite(rpm) || Math.Abs(rpm) > config.Limits.MaxSpeedRpm)
            throw new ArgumentOutOfRangeException(nameof(rpm), $"目标转速必须在 ±{config.Limits.MaxSpeedRpm:F0} rpm 以内");
        if (!double.IsFinite(rampRpmPerSec) || rampRpmPerSec < 10 || rampRpmPerSec > config.Limits.MaxSpeedRampRpmPerSec)
            throw new ArgumentOutOfRangeException(nameof(rampRpmPerSec), $"速度斜坡必须在 10–{config.Limits.MaxSpeedRampRpmPerSec:F0} rpm/s 以内");
        _velocityRampRpmPerSec = rampRpmPerSec;
        _velocityControlSelected = true;
        return SendAsync(new { type = "velocity", rpm, ramp_rpm_per_s = rampRpmPerSec }, ct);
    }

    public Task StopVelocityAsync(bool disable, CancellationToken ct) =>
        SendAsync(new { type = disable ? "disable" : "stop", ramp_rpm_per_s = _velocityRampRpmPerSec }, ct);

    private async Task SendAsync(object command, CancellationToken ct)
    {
        var process = _process;
        if (process is null || process.HasExited || _fatal != null)
            throw new IOException(_fatal ?? "EtherCAT 直连未连接");
        await _writer.WaitAsync(ct);
        try
        {
            await process.StandardInput.WriteLineAsync(JsonSerializer.Serialize(command));
            await process.StandardInput.FlushAsync(ct);
        }
        finally { _writer.Release(); }
    }

    public async Task DisconnectAsync()
    {
        var process = _process;
        _process = null;
        if (process == null) return;
        if (!process.HasExited)
        {
            try
            {
                await _writer.WaitAsync();
                try
                {
                    await process.StandardInput.WriteLineAsync("{\"type\":\"shutdown\"}");
                    await process.StandardInput.FlushAsync();
                }
                finally { _writer.Release(); }
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(8));
                await process.WaitForExitAsync(timeout.Token);
            }
            catch
            {
                try { process.Kill(entireProcessTree: true); } catch { }
                await process.WaitForExitAsync();
            }
        }
        if (_stdoutTask != null) await _stdoutTask;
        if (_stderrTask != null) await _stderrTask;
        process.Dispose();
        lock (_gate) _latest = new();
    }

    public async ValueTask DisposeAsync()
    {
        await DisconnectAsync();
        _writer.Dispose();
    }

    private static TaskCompletionSource<bool> NewSignal() =>
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    private sealed record DirectStatus
    {
        [JsonPropertyName("connected")] public bool Connected { get; init; }
        [JsonPropertyName("ethercat_online")] public bool EtherCatOnline { get; init; }
        [JsonPropertyName("drive_ready")] public bool DriveReady { get; init; }
        [JsonPropertyName("servo_on")] public bool ServoOn { get; init; }
        [JsonPropertyName("speed_rpm")] public double SpeedRpm { get; init; }
        [JsonPropertyName("target_rpm")] public double TargetRpm { get; init; }
        [JsonPropertyName("torque_raw")] public int TorqueRaw { get; init; }
        [JsonPropertyName("target_torque_raw")] public int TargetTorqueRaw { get; init; }
        [JsonPropertyName("phase_current_a")] public double? PhaseCurrentA { get; init; }
        [JsonPropertyName("dc_bus_v")] public double? DcBusV { get; init; }
        [JsonPropertyName("module_temp_c")] public double? ModuleTempC { get; init; }
        [JsonPropertyName("load_percent")] public double? LoadPercent { get; init; }
        [JsonPropertyName("error_code")] public int ErrorCode { get; init; }
        [JsonPropertyName("auxiliary_fault")] public uint? AuxiliaryFault { get; init; }
        [JsonPropertyName("fault_detail")] public string? FaultDetail { get; init; }
        [JsonPropertyName("heartbeat")] public uint Heartbeat { get; init; }
    }
}
