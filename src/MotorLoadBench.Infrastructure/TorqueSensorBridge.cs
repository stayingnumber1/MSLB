using System.IO.Ports;
using MotorLoadBench.Application;
using MotorLoadBench.Domain;

namespace MotorLoadBench.Infrastructure;

public sealed class TorqueSensorBridge(IRealtimeBridge inner, TorqueSensorConfig config) : IRealtimeBridge, IDirectVelocityBridge, IExternalTorqueZeroing
{
    private readonly object _gate = new();
    private SerialPort? _port;
    private CancellationTokenSource? _cts;
    private Task? _reader;
    private Dyn200Sample _sample;
    private DateTimeOffset _sampleAt;
    private readonly Queue<(DateTimeOffset At, double TorqueNm)> _torqueSamples = new();
    private double _zeroOffsetNm;

    public bool IsSimulation => inner.IsSimulation;
    public bool CanWrite => inner.CanWrite;
    public bool CanControlVelocity => inner is IDirectVelocityBridge { CanControlVelocity: true };

    public async Task ConnectAsync(CancellationToken ct)
    {
        await inner.ConnectAsync(ct);
        if (!config.Enabled) return;
        try
        {
            var parity = Enum.Parse<Parity>(config.Parity, true);
            _port = new SerialPort(config.PortName, config.BaudRate, parity, config.DataBits,
                config.StopBits == 2 ? StopBits.Two : StopBits.One) { ReadTimeout = 100 };
            _port.Open();
            _cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            _reader = Task.Run(async () =>
            {
                try { await ReadLoopAsync(_cts.Token); }
                catch (OperationCanceledException) when (_cts.IsCancellationRequested) { }
                catch (Exception) { lock (_gate) _sampleAt = default; }
            }, CancellationToken.None);
        }
        catch
        {
            await inner.DisconnectAsync();
            throw;
        }
    }

    public async Task DisconnectAsync()
    {
        if (_cts != null) { await _cts.CancelAsync(); if (_reader != null) await _reader; _cts.Dispose(); _cts = null; }
        _port?.Dispose(); _port = null;
        await inner.DisconnectAsync();
    }

    public async Task<BenchSnapshot> ReadSnapshotAsync(CancellationToken ct)
    {
        var snapshot = await inner.ReadSnapshotAsync(ct);
        if (!config.Enabled) return snapshot;
        Dyn200Sample sample; DateTimeOffset at;
        lock (_gate) { sample = _sample; at = _sampleAt; }
        var healthy = at != default && DateTimeOffset.UtcNow - at <= TimeSpan.FromMilliseconds(config.StaleAfterMs);
        return snapshot with
        {
            ExternalHealthy = healthy,
            ExternalTorqueNm = healthy ? sample.TorqueNm - _zeroOffsetNm : null,
            ExternalSpeedRpm = healthy ? sample.SpeedRpm ?? snapshot.SpeedRpm : null
        };
    }

    public Task WriteCommandAsync(BenchCommand command, CancellationToken ct) => inner.WriteCommandAsync(command, ct);
    public Task EnableVelocityAsync(CancellationToken ct) => Direct().EnableVelocityAsync(ct);
    public Task SetVelocityAsync(double rpm, double rampRpmPerSec, CancellationToken ct) => Direct().SetVelocityAsync(rpm, rampRpmPerSec, ct);
    public Task StopVelocityAsync(bool disable, CancellationToken ct) => Direct().StopVelocityAsync(disable, ct);
    private IDirectVelocityBridge Direct() => inner as IDirectVelocityBridge ?? throw new InvalidOperationException("底层连接不支持速度控制。");

    public Task ZeroExternalTorqueAsync(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        lock (_gate)
        {
            var cutoff = DateTimeOffset.UtcNow.AddMilliseconds(-500);
            var recent = _torqueSamples.Where(x => x.At >= cutoff).Select(x => x.TorqueNm).ToArray();
            if (_sampleAt == default || DateTimeOffset.UtcNow - _sampleAt > TimeSpan.FromMilliseconds(config.StaleAfterMs) || recent.Length < 3)
                throw new InvalidOperationException("DYN-200 数据不足，无法执行软件清零");
            _zeroOffsetNm = recent.Average();
        }
        return Task.CompletedTask;
    }

    private async Task ReadLoopAsync(CancellationToken ct)
    {
        var stream = _port!.BaseStream;
        var pending = new List<byte>(256);
        var buffer = new byte[128];
        while (!ct.IsCancellationRequested)
        {
            var count = await stream.ReadAsync(buffer, ct);
            if (count == 0) continue;
            pending.AddRange(buffer.AsSpan(0, count).ToArray());
            ParsePending(pending);
        }
    }

    private void ParsePending(List<byte> pending)
    {
        if (config.Protocol == TorqueSensorProtocol.Ascii)
        {
            while (pending.IndexOf(0x0d) is var end && end >= 0)
            {
                var frame = pending.GetRange(0, end + 1).ToArray(); pending.RemoveRange(0, end + 1);
                Accept(frame);
            }
            if (pending.Count > 64) pending.Clear();
            return;
        }
        var length = config.Protocol == TorqueSensorProtocol.Hex6 ? 6 : 8;
        while (pending.Count >= length)
        {
            var frame = pending.GetRange(0, length).ToArray();
            if (Accept(frame)) pending.RemoveRange(0, length); else pending.RemoveAt(0);
        }
    }

    private bool Accept(byte[] frame)
    {
        if (!Dyn200Protocol.TryDecode(frame, config, out var decoded)) return false;
        lock (_gate)
        {
            var now = DateTimeOffset.UtcNow;
            _sample = decoded; _sampleAt = now;
            _torqueSamples.Enqueue((now, decoded.TorqueNm));
            while (_torqueSamples.Count > 100 || (_torqueSamples.Count > 0 && now - _torqueSamples.Peek().At > TimeSpan.FromSeconds(1)))
                _torqueSamples.Dequeue();
        }
        return true;
    }

    public async ValueTask DisposeAsync() { await DisconnectAsync(); await inner.DisposeAsync(); }
}
