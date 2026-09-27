using System.IO.Ports;
using MotorLoadBench.Application;
using MotorLoadBench.Domain;

namespace MotorLoadBench.Infrastructure;

public sealed class VescBridge(IRealtimeBridge inner, VescConfig config) : IRealtimeBridge, IDirectVelocityBridge, IDutMotorBridge
{
    // USB-CDC/FTDI 驱动的应答延迟最高约 16 ms，且部分 VESC UART 出厂波特率不是 115200。
    // 部分 CDC 固件还依赖 DTR 线路状态，或打开后立即写入会与驱动线路编码竞态，
    // 因此：识别窗口给足、打开后先稳定、失败时按 DTR 状态与波特率降级重试。
    private const int FallbackBaudRate = 9600;
    private const int IdentityTimeoutMs = 1500;
    private const int TelemetryTimeoutMs = 1000;
    private const int OpenSettleMs = 100;

    private readonly object _gate = new();
    private readonly SemaphoreSlim _writeGate = new(1, 1);
    private SerialPort? _port;
    private CancellationTokenSource? _cts;
    private Task? _worker;
    private VescTelemetry _telemetry;
    private DateTimeOffset _telemetryAt;
    private bool _firmwareIdentified;
    private string? _firmwareVersion;
    private long _receivedBytes;
    private long _transmittedBytes;
    private DateTimeOffset _lastReadAt;
    private DateTimeOffset _lastWriteAt;
    private bool _staleReported;
    private bool _hardStaleReported;
    private byte[] _recentRx = [];
    private byte[]? _activeCommand;
    private DutControlMode? _activeMode;
    private double _activeValue;
    private long _commandGeneration;
    private bool _runRequested;
    private bool _externalControlClaimed;
    private Exception? _failure;
    public event Action<string, string>? Diagnostic;

    private void Trace(string code, string message)
    {
        try { Diagnostic?.Invoke(code, message); } catch { }
    }

    public bool IsSimulation => inner.IsSimulation;
    public bool CanWrite => inner.CanWrite;
    public bool CanControlVelocity => inner is IDirectVelocityBridge { CanControlVelocity: true };
    // Servo-on produces a repeatable sub-second interval of invalid/missing VESC
    // telemetry while the COM handle remains open and bytes continue to flow. Treat
    // the configured 500 ms threshold as a warning; only declare the link down after
    // the disturbance has persisted for the hard timeout.
    private TimeSpan SoftTelemetryTimeout => TimeSpan.FromMilliseconds(config.StaleAfterMs);
    private TimeSpan HardTelemetryTimeout => TimeSpan.FromMilliseconds(Math.Max(2000, config.StaleAfterMs * 4));
    public bool CanControlDut => _port?.IsOpen == true && _failure is null && _telemetryAt != default &&
        DateTimeOffset.UtcNow - _telemetryAt <= HardTelemetryTimeout;
    public IReadOnlyList<string> AvailableDutPorts => WindowsSerialPortDiscovery.GetAllPorts();

    public async Task ConnectAsync(CancellationToken ct)
    {
        await inner.ConnectAsync(ct);
        if (config.Enabled) await ConnectDutAsync(config.PortName, ct);
    }

    public async Task ConnectDutAsync(string portName, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(portName)) throw new ArgumentException("请选择 VESC 串口。", nameof(portName));
        if (!AvailableDutPorts.Contains(portName, StringComparer.OrdinalIgnoreCase)) throw new IOException($"串口 {portName} 不存在，请刷新串口列表。");
        Trace("vesc_connect_start", $"port={portName}; configuredBaud={config.BaudRate}; ports={string.Join(',', AvailableDutPorts)}");
        await DisconnectDutAsync();
        var attempts = new List<(int Baud, bool Dtr, string Label)>
        {
            (config.BaudRate, false, $"{config.BaudRate} 8N1 DTR=off"),
            (config.BaudRate, true, $"{config.BaudRate} 8N1 DTR=on"),
        };
        if (config.BaudRate != FallbackBaudRate)
            attempts.Add((FallbackBaudRate, true, $"{FallbackBaudRate} 8N1 DTR=on"));
        var probeFailures = new List<string>();
        for (var i = 0; i < attempts.Count; i++)
        {
            try
            {
                await ProbeAsync(portName, attempts[i].Baud, attempts[i].Dtr, ct);
                Trace("vesc_connect_ok", $"port={portName}; baud={attempts[i].Baud}; dtr={attempts[i].Dtr}; firmware={_firmwareVersion}; receivedBytes={Interlocked.Read(ref _receivedBytes)}");
                return;
            }
            // 仅当端口已打开但识别不出 VESC（无应答/写停滞/波特率不匹配）时才换线路状态或波特率重试；
            // 端口占用、驱动错误或用户取消不在重试范围内，直接向上抛出原始诊断。
            catch (ProbeFailureException ex) when (i < attempts.Count - 1 && !ct.IsCancellationRequested)
            {
                probeFailures.Add($"[{attempts[i].Label}] {ex.Message}");
                Trace("vesc_probe_retry", $"port={portName}; attempt={attempts[i].Label}; error={ex}");
            }
        }
        throw new IOException(
            $"无法连接 VESC/M1 串口 {portName}（已尝试：{string.Join("；", attempts.Select(a => a.Label))}）。{string.Join("；", probeFailures)}");
    }

    private sealed class ProbeFailureException(string message, Exception? inner) : IOException(message, inner);

    private async Task ProbeAsync(string portName, int baudRate, bool dtrEnable, CancellationToken ct)
    {
        try
        {
            lock (_gate) { _failure = null; _telemetryAt = default; _firmwareIdentified = false; _firmwareVersion = null; _receivedBytes = 0; _transmittedBytes = 0; _lastReadAt = default; _lastWriteAt = default; _staleReported = false; _hardStaleReported = false; _recentRx = []; }
            var candidate = new SerialPort(portName, baudRate, Parity.None, 8, StopBits.One)
            {
                ReadTimeout = 100,
                // Control and telemetry frames are at most a few bytes on this path.
                // A one-second blocked write also prevents this worker from draining RX,
                // while telemetry is considered stale after 500 ms.
                WriteTimeout = Math.Min(200, config.StaleAfterMs),
                Handshake = Handshake.None,
                DtrEnable = dtrEnable,
                RtsEnable = dtrEnable
            };
            _port = candidate;
            // SerialPort.Open cannot be cancelled safely. A detached open task can retain
            // a blocked kernel IRP and keep the COM handle locked after our timeout. Since
            // only the user-selected port is opened now, keep ownership on this call path.
            try
            {
                candidate.Open();
                Trace("vesc_port_open", $"port={portName}; baud={baudRate}; dtr={dtrEnable}; baseStreamCanRead={candidate.BaseStream.CanRead}; baseStreamCanWrite={candidate.BaseStream.CanWrite}");
            }
            catch (Exception ex)
            {
                Trace("vesc_port_open_failed", $"port={portName}; baud={baudRate}; dtr={dtrEnable}; ports={string.Join(',', AvailableDutPorts)}; error={ex}");
                // Some STM32 USB CDC drivers reject SetCommState while DTR/RTS
                // are both low (ERROR_OPERATION_ABORTED). Retry only that
                // specific DTR-off failure with the existing DTR-on attempt.
                if (!dtrEnable && ex is OperationCanceledException)
                    throw new ProbeFailureException(
                        $"串口 {portName} 的 USB CDC 驱动要求 DTR/RTS 开启，改用 DTR=on 重试。", ex);
                throw new IOException($"串口 {portName} 打开失败：{ex.Message}。请确认端口未被 VESC Tool 或其他程序占用，且驱动已安装。", ex);
            }
            candidate.DiscardInBuffer();
            candidate.DiscardOutBuffer();
            // 打开后立即写入可能与驱动完成 CDC 线路编码设置竞态（表现为写指令
            // “信号灯超时时间已到”/ERROR_SEM_TIMEOUT），先等链路稳定再发首条探测。
            await Task.Delay(OpenSettleMs, ct);
            ct.ThrowIfCancellationRequested();
            // The caller token limits only this connection attempt. UI callers use an
            // 8-second CancelAfter token and disposing/cancelling that token must never
            // stop an already established serial worker several seconds later. The worker
            // lifetime is owned exclusively by DisconnectDutAsync.
            _cts = new CancellationTokenSource();
            _worker = Task.Run(() => WorkerAsync(_cts.Token), CancellationToken.None);
            // A local VESC replies within a few telemetry periods. USB-CDC/FTDI
            // drivers add up to ~16 ms latency per transfer, so keep the
            // identity window generous instead of abandoning a working port.
            var identityDeadline = DateTimeOffset.UtcNow.AddMilliseconds(IdentityTimeoutMs);
            while (!_firmwareIdentified && _telemetryAt == default && _failure == null && DateTimeOffset.UtcNow < identityDeadline)
                await Task.Delay(20, ct);
            Exception? failure;
            lock (_gate) failure = _failure;
            if (!_firmwareIdentified && _telemetryAt == default)
            {
                // 区分“写指令在 USB 层停滞”与“写正常但设备无应答”，两者指向不同硬件根因。
                var writeHint = failure == null
                    ? ""
                    : $"；且写指令失败：{failure.Message}（通常为 USB 线缆/供电/驱动问题或固件挂起，建议换线/换口/重上电）";
                throw new ProbeFailureException(_receivedBytes == 0
                    ? $"端口已打开，但 {IdentityTimeoutMs} ms 内没有收到任何字节{writeHint}。请确认 VESC Tool 已关闭，并核对 VESC UART 波特率。"
                    : $"已收到 {_receivedBytes} 字节，但没有解析出有效 VESC 帧；请检查 USB 串口链路或固件协议版本。", failure);
            }
            var telemetryDeadline = DateTimeOffset.UtcNow.AddMilliseconds(TelemetryTimeoutMs);
            while (_telemetryAt == default && _failure == null && DateTimeOffset.UtcNow < telemetryDeadline)
                await Task.Delay(20, ct);
            if (_telemetryAt == default)
                throw new IOException($"已识别 VESC 固件 {_firmwareVersion ?? "未知版本"}，但没有收到实时遥测。", _failure);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            await DisconnectDutAsync();
            throw new IOException($"连接 VESC/M1 串口 {portName} 超时（波特率 {baudRate}，{IdentityTimeoutMs} ms 内未识别出 VESC）。请确认设备上电并核对波特率。");
        }
        catch
        {
            await DisconnectDutAsync();
            throw;
        }
    }

    public async Task DisconnectDutAsync()
    {
        Trace("vesc_disconnect_start", $"port={_port?.PortName}; isOpen={_port?.IsOpen}; telemetryUtc={(_telemetryAt == default ? "none" : _telemetryAt.ToString("O"))}; failure={_failure}");
        // Never send motor commands to an arbitrary port that failed probing.
        // A zero-current stop is valid only after a VESC telemetry frame proved identity.
        if (_port?.IsOpen == true && _telemetryAt != default)
            try { await WriteAsync(VescProtocol.SetCurrent(0), CancellationToken.None); } catch { }
        if (_cts != null)
        {
            await _cts.CancelAsync();
            if (_worker != null)
            {
                var completed = await Task.WhenAny(_worker, Task.Delay(300));
                if (completed != _worker)
                {
                    // Some USB serial drivers do not cancel BaseStream reads.
                    // Closing the handle is the only reliable unblock mechanism.
                    try { _port?.Close(); } catch { }
                    await Task.WhenAny(_worker, Task.Delay(300));
                }
                if (_worker.IsCompleted) try { await _worker; } catch (OperationCanceledException) { }
            }
            _cts.Dispose(); _cts = null; _worker = null;
        }
        _port?.Dispose(); _port = null;
        lock (_gate) { _activeCommand = null; _activeMode = null; _activeValue = 0; _commandGeneration++; _runRequested = false; _externalControlClaimed = false; _failure = null; _telemetryAt = default; _firmwareIdentified = false; _firmwareVersion = null; }
        Trace("vesc_disconnect_done", $"ports={string.Join(',', AvailableDutPorts)}");
    }

    public async Task DisconnectAsync() { await DisconnectDutAsync(); await inner.DisconnectAsync(); }

    public async Task<BenchSnapshot> ReadSnapshotAsync(CancellationToken ct)
    {
        var snapshot = await inner.ReadSnapshotAsync(ct);
        VescTelemetry telemetry; DateTimeOffset at; Exception? failure; bool running;
        double activeValue; DutControlMode? activeMode;
        lock (_gate) { telemetry = _telemetry; at = _telemetryAt; failure = _failure; running = _runRequested; activeValue = _activeValue; activeMode = _activeMode; }
        if (failure != null && running) throw new IOException("VESC/M1 通信故障，已停止 M1 指令输出。", failure);
        var healthy = at != default && DateTimeOffset.UtcNow - at <= HardTelemetryTimeout;
        if (!healthy) return snapshot with { DutBusV = null, DutCurrentA = null, DutPhaseCurrentA = null, DutDutyCyclePct = null, DutMotorTempC = null, DutControllerTempC = null, DutFaultCode = null, DutSpeedRpm = null };
        return snapshot with { DutBusV = telemetry.InputVoltageV, DutCurrentA = telemetry.InputCurrentA,
            DutPhaseCurrentA = telemetry.MotorCurrentA, DutIqCommandA = activeMode == DutControlMode.MotorCurrentA ? activeValue : null, DutDutyCyclePct = telemetry.DutyCycle * 100,
            DutMotorTempC = telemetry.MotorTemperatureC, DutControllerTempC = telemetry.FetTemperatureC,
            DutFaultCode = telemetry.FaultCode, DutSpeedRpm = telemetry.ElectricalRpm / config.MotorPolePairs };
    }

    public Task WriteCommandAsync(BenchCommand command, CancellationToken ct) => inner.WriteCommandAsync(command, ct);
    public Task EnableVelocityAsync(CancellationToken ct) => Direct().EnableVelocityAsync(ct);
    public Task SetVelocityAsync(double rpm, double rampRpmPerSec, CancellationToken ct) => Direct().SetVelocityAsync(rpm, rampRpmPerSec, ct);
    public Task StopVelocityAsync(bool disable, CancellationToken ct) => Direct().StopVelocityAsync(disable, ct);

    public async Task SetDutAsync(DutControlMode mode, double value, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (!CanControlDut) throw new InvalidOperationException("VESC/M1 未连接或通信异常。");
        if (!double.IsFinite(value)) throw new ArgumentOutOfRangeException(nameof(value));
        byte[] command = mode switch
        {
            DutControlMode.SpeedRpm => VescProtocol.SetMechanicalRpm(value, config),
            DutControlMode.MotorCurrentA when Math.Abs(value) <= config.MaxMotorCurrentA => VescProtocol.SetCurrent(value),
            DutControlMode.PositionDegrees when Math.Abs(value) <= 360000 => VescProtocol.SetPosition(value),
            DutControlMode.DutyCycle when Math.Abs(value) <= config.MaxDutyCycle => VescProtocol.SetDuty(value),
            DutControlMode.MotorCurrentA => throw new ArgumentOutOfRangeException(nameof(value), $"电机电流范围为 ±{config.MaxMotorCurrentA:F1} A，最终仍受 VESC 固件电流限值约束。"),
            _ => throw new ArgumentOutOfRangeException(nameof(value), "指令超出 VESC/M1 软件限值。")
        };
        bool claimExternalControl;
        lock (_gate) claimExternalControl = !_externalControlClaimed;
        if (claimExternalControl)
        {
            var disableAppOutput = VescProtocol.DisableAppOutput();
            for (var i = 0; i < 3; i++)
            {
                await WriteAsync(disableAppOutput, ct);
                if (i < 2) await Task.Delay(5, ct);
            }
            lock (_gate) _externalControlClaimed = true;
            Trace("vesc_external_control_claimed", "appOutputDisabled=-1; source=host; restore=MCU reset");
        }
        long generation;
        lock (_gate) { _activeCommand = command; _activeMode = mode; _activeValue = value; generation = ++_commandGeneration; _runRequested = true; }
        Trace("vesc_control_target", $"mode={mode}; value={value:F5}; commandId={command[2]}");
        for (var i = 0; i < 3; i++)
        {
            await WriteActiveAsync(command, generation, ct);
            if (i < 2) await Task.Delay(5, ct);
        }
    }

    public async Task StopDutAsync(CancellationToken ct)
    {
        lock (_gate) { _activeCommand = null; _activeMode = null; _activeValue = 0; _commandGeneration++; _runRequested = false; }
        if (_port?.IsOpen == true)
            for (var i = 0; i < 10; i++)
            {
                await WriteAsync(VescProtocol.SetCurrent(0), ct);
                if (i < 9) await Task.Delay(5, ct);
            }
    }

    private async Task WorkerAsync(CancellationToken ct)
    {
        var pending = new List<byte>(512); var buffer = new byte[256];
        var commandPeriod = TimeSpan.FromSeconds(1.0 / config.CommandRateHz);
        var telemetryPeriod = TimeSpan.FromSeconds(1.0 / config.TelemetryRateHz);
        var nextTelemetry = DateTimeOffset.UtcNow;
        var nextIdentity = DateTimeOffset.MinValue;
        try
        {
            while (!ct.IsCancellationRequested)
            {
                var now = DateTimeOffset.UtcNow; byte[]? command; DutControlMode? mode; double commandValue; long generation; VescTelemetry telemetry;
                lock (_gate) { command = _activeCommand; mode = _activeMode; commandValue = _activeValue; generation = _commandGeneration; telemetry = _telemetry; }
                var energizedCommand = IsEnergizedCommand(mode, commandValue);

                // Drain data which arrived while the worker was sleeping or writing before
                // evaluating freshness. Checking age first can declare a false outage even
                // though a complete current frame is already waiting in the driver buffer.
                DrainIncoming(pending, buffer);
                now = DateTimeOffset.UtcNow;
                var telemetryAge = _telemetryAt == default ? TimeSpan.MaxValue : now - _telemetryAt;
                if (_telemetryAt != default && telemetryAge > SoftTelemetryTimeout && !_staleReported)
                {
                    _staleReported = true;
                    var ascii = new string(_recentRx.Select(b => b is >= 0x20 and <= 0x7E ? (char)b : '.').ToArray());
                    Trace("vesc_telemetry_delayed", $"port={_port?.PortName}; isOpen={_port?.IsOpen}; ageMs={telemetryAge.TotalMilliseconds:F1}; hardTimeoutMs={HardTelemetryTimeout.TotalMilliseconds:F0}; lastTelemetryUtc={_telemetryAt:O}; lastReadUtc={(_lastReadAt == default ? "none" : _lastReadAt.ToString("O"))}; lastWriteUtc={(_lastWriteAt == default ? "none" : _lastWriteAt.ToString("O"))}; rxBytes={Interlocked.Read(ref _receivedBytes)}; txBytes={Interlocked.Read(ref _transmittedBytes)}; bytesToRead={_port?.BytesToRead}; recentRxHex={Convert.ToHexString(_recentRx)}; recentRxAscii={ascii}");
                }
                if (_telemetryAt != default && telemetryAge > HardTelemetryTimeout && !_hardStaleReported)
                {
                    _hardStaleReported = true;
                    Trace("vesc_telemetry_stale", $"port={_port?.PortName}; isOpen={_port?.IsOpen}; ageMs={telemetryAge.TotalMilliseconds:F1}; hardTimeoutMs={HardTelemetryTimeout.TotalMilliseconds:F0}; lastTelemetryUtc={_telemetryAt:O}; lastReadUtc={(_lastReadAt == default ? "none" : _lastReadAt.ToString("O"))}; lastWriteUtc={(_lastWriteAt == default ? "none" : _lastWriteAt.ToString("O"))}; rxBytes={Interlocked.Read(ref _receivedBytes)}; txBytes={Interlocked.Read(ref _transmittedBytes)}; bytesToRead={_port?.BytesToRead}");
                }
                if (energizedCommand && _telemetryAt != default && telemetryAge > HardTelemetryTimeout)
                    throw new IOException($"VESC 遥测连续超过 {HardTelemetryTimeout.TotalMilliseconds:F0} ms 未更新，已执行零电流停止。");
                if (energizedCommand && mode == DutControlMode.MotorCurrentA &&
                    Math.Abs(telemetry.ElectricalRpm / config.MotorPolePairs) >= config.CurrentModeMaxRpm)
                    throw new IOException($"电流模式达到 {config.CurrentModeMaxRpm:F0} RPM 安全上限，已执行零电流停止。");
                if (command != null)
                {
                    if (energizedCommand) await WriteActiveAsync(command, generation, ct);
                    else await TryWriteIdleCommandAsync(command, generation, ct);
                }
                if (!_firmwareIdentified && now >= nextIdentity)
                {
                    await TryWriteMaintenanceAsync(VescProtocol.Request(VescProtocol.CommFwVersion), ct, "firmware");
                    nextIdentity = now.AddMilliseconds(150);
                }
                if (now >= nextTelemetry) { await TryWriteMaintenanceAsync(VescProtocol.Request(VescProtocol.CommGetValues), ct, "telemetry"); nextTelemetry = now + telemetryPeriod; }
                var readUntil = now + commandPeriod;
                // USB-CDC/FTDI 驱动应答延迟最高约 16 ms，数据可能晚于本周期窗口到达；
                // 只要驱动缓冲仍有数据就持续读出并解析，不再受单周期窗口限制，
                // 避免反复错过读取窗口导致固件识别/遥测等待超时（限制单轮次数防止饿死写线程）。
                DrainIncoming(pending, buffer);
                var delay = readUntil - DateTimeOffset.UtcNow; if (delay > TimeSpan.Zero) await Task.Delay(delay, ct);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            Trace("vesc_worker_cancelled", $"port={_port?.PortName}; reason=explicit disconnect; lastTelemetryUtc={(_telemetryAt == default ? "none" : _telemetryAt.ToString("O"))}");
        }
        catch (Exception ex)
        {
            bool recognized;
            lock (_gate) { recognized = _telemetryAt != default; _failure = ex; _activeCommand = null; _activeMode = null; _activeValue = 0; _commandGeneration++; _runRequested = false; }
            Trace("vesc_worker_failed", $"port={_port?.PortName}; isOpen={_port?.IsOpen}; recognized={recognized}; firmware={_firmwareVersion}; lastTelemetryUtc={(_telemetryAt == default ? "none" : _telemetryAt.ToString("O"))}; receivedBytes={Interlocked.Read(ref _receivedBytes)}; ports={string.Join(',', AvailableDutPorts)}; error={ex}");
            if (recognized)
                try { for (var i = 0; i < 10; i++) { await WriteAsync(VescProtocol.SetCurrent(0), CancellationToken.None); await Task.Delay(5); } } catch { }
        }
    }

    private static bool IsEnergizedCommand(DutControlMode? mode, double value) => mode switch
    {
        DutControlMode.PositionDegrees => true,
        DutControlMode.SpeedRpm => Math.Abs(value) > 0.5,
        DutControlMode.MotorCurrentA => Math.Abs(value) > 0.001,
        DutControlMode.DutyCycle => Math.Abs(value) > 0.0001,
        _ => false
    };

    private async Task TryWriteIdleCommandAsync(byte[] bytes, long generation, CancellationToken ct)
    {
        try { await WriteActiveAsync(bytes, generation, ct); }
        catch (OperationCanceledException ex) when (!ct.IsCancellationRequested && _port?.IsOpen == true)
        {
            Trace("vesc_idle_write_deferred", $"port={_port.PortName}; commandId={(bytes.Length > 2 ? bytes[2] : -1)}; hresult=0x{ex.HResult:X8}; action=keep-link-open");
        }
    }

    private async Task TryWriteMaintenanceAsync(byte[] bytes, CancellationToken ct, string purpose)
    {
        try { await WriteAsync(bytes, ct); }
        catch (OperationCanceledException ex) when (!ct.IsCancellationRequested && _port?.IsOpen == true)
        {
            Trace("vesc_maintenance_write_deferred", $"port={_port.PortName}; purpose={purpose}; commandId={(bytes.Length > 2 ? bytes[2] : -1)}; hresult=0x{ex.HResult:X8}; action=keep-link-open");
        }
    }

    private void DrainIncoming(List<byte> pending, byte[] buffer)
    {
        var port = _port;
        for (var reads = 0; port is { IsOpen: true } && port.BytesToRead > 0 && reads < 64; reads++)
        {
            // SerialPort.BaseStream.ReadAsync can ignore cancellation on several USB-UART
            // drivers. A bounded synchronous read uses ReadTimeout and cannot remain pending.
            var available = port.BytesToRead;
            int count;
            try { count = port.Read(buffer, 0, Math.Min(buffer.Length, available)); }
            catch (TimeoutException) { break; }
            Interlocked.Add(ref _receivedBytes, count);
            _lastReadAt = DateTimeOffset.UtcNow;
            _recentRx = _recentRx.Concat(buffer.AsSpan(0, count).ToArray()).TakeLast(256).ToArray();
            pending.AddRange(buffer.AsSpan(0, count).ToArray());
            while (VescProtocol.TryTakeFrame(pending, out var payload))
            {
                if (payload.Length >= 3 && payload[0] == VescProtocol.CommFwVersion)
                    lock (_gate)
                    {
                        _firmwareIdentified = true;
                        _firmwareVersion = $"{payload[1]}.{payload[2]}";
                    }
                if (VescProtocol.TryDecodeValues(payload, out var value))
                {
                    if (_staleReported) Trace("vesc_telemetry_recovered", $"port={port.PortName}; delayedOnly={!_hardStaleReported}; rxBytes={Interlocked.Read(ref _receivedBytes)}; txBytes={Interlocked.Read(ref _transmittedBytes)}");
                    lock (_gate) { _telemetry = value; _telemetryAt = DateTimeOffset.UtcNow; _staleReported = false; _hardStaleReported = false; }
                }
            }
        }
    }

    private async Task WriteAsync(byte[] bytes, CancellationToken ct)
    {
        var port = _port ?? throw new IOException("VESC/M1 串口未打开。"); await _writeGate.WaitAsync(ct);
        try { await WritePortWithRetryAsync(port, bytes, ct, active: false); }
        finally { _writeGate.Release(); }
    }
    private async Task WriteActiveAsync(byte[] bytes, long generation, CancellationToken ct)
    {
        var port = _port ?? throw new IOException("VESC/M1 串口未打开。");
        await _writeGate.WaitAsync(ct);
        try
        {
            lock (_gate) if (generation != _commandGeneration || _activeCommand == null) return;
            await WritePortWithRetryAsync(port, bytes, ct, active: true);
        }
        finally { _writeGate.Release(); }
    }

    private async Task WritePortWithRetryAsync(SerialPort port, byte[] bytes, CancellationToken ct, bool active)
    {
        // A telemetry-only link can be observed for one second without delaying
        // a motor stop. Active DUT commands keep the short fail-fast window.
        var maxAttempts = active ? 4 : 21;
        for (var attempt = 1; ; attempt++)
        {
            ct.ThrowIfCancellationRequested();
            var started = DateTimeOffset.UtcNow;
            try
            {
                port.Write(bytes, 0, bytes.Length);
                Interlocked.Add(ref _transmittedBytes, bytes.Length);
                _lastWriteAt = DateTimeOffset.UtcNow;
                if (attempt > 1)
                    Trace("vesc_write_retry_recovered", $"port={port.PortName}; commandId={(bytes.Length > 2 ? bytes[2] : -1)}; active={active}; attempt={attempt}; isOpen={port.IsOpen}");
                return;
            }
            catch (OperationCanceledException ex) when (!ct.IsCancellationRequested && port.IsOpen && attempt < maxAttempts)
            {
                // Windows usbser/System.IO.Ports can complete a synchronous overlapped
                // write with ERROR_OPERATION_ABORTED while the COM handle remains valid.
                // This is not cancellation by our worker token. Give the same handle a
                // short chance to recover instead of turning one aborted IRP into a
                // software-initiated disconnect/re-enumeration cycle.
                Trace("vesc_write_aborted_retry", $"port={port.PortName}; commandId={(bytes.Length > 2 ? bytes[2] : -1)}; active={active}; attempt={attempt}; elapsedMs={(DateTimeOffset.UtcNow - started).TotalMilliseconds:F1}; isOpen={port.IsOpen}; tokenCancelled=False; hresult=0x{ex.HResult:X8}");
                await Task.Delay(TimeSpan.FromMilliseconds(active ? 10 * attempt : 50), ct);
            }
            catch (Exception ex)
            {
                Trace("vesc_write_failed", $"port={port.PortName}; commandId={(bytes.Length > 2 ? bytes[2] : -1)}; active={active}; attempt={attempt}; elapsedMs={(DateTimeOffset.UtcNow - started).TotalMilliseconds:F1}; isOpen={port.IsOpen}; tokenCancelled={ct.IsCancellationRequested}; hresult=0x{ex.HResult:X8}; error={ex}");
                throw;
            }
        }
    }
    private IDirectVelocityBridge Direct() => inner as IDirectVelocityBridge ?? throw new InvalidOperationException("底层连接不支持速度控制。");
    public async ValueTask DisposeAsync() { await DisconnectAsync(); await inner.DisposeAsync(); _writeGate.Dispose(); }
}
