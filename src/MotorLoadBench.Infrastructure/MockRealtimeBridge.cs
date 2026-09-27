using System.Diagnostics;
using MotorLoadBench.Application;
using MotorLoadBench.Domain;

namespace MotorLoadBench.Infrastructure;

public sealed class MockRealtimeBridge(BenchConfig config) : IRealtimeBridge
{
    private readonly object _gate = new();
    private RealtimeModel _model = new(config);
    private CancellationTokenSource? _cts;
    private Task? _task;
    private BenchSnapshot _latest = new();
    private double _rpm, _actual, _temperature = 25;
    private double? _performanceNoLoadRpm;
    private Interlock _injection;
    public bool IsSimulation => true;
    public bool CanWrite => true;
    public void SetSpeed(double rpm) { if (!double.IsFinite(rpm)) throw new ArgumentException("转速无效"); lock (_gate) { _performanceNoLoadRpm = null; _rpm = rpm; } }
    public void SetPerformanceDrive(double noLoadRpm) { if (!double.IsFinite(noLoadRpm) || noLoadRpm <= 0) throw new ArgumentException("空载转速无效"); lock (_gate) { _performanceNoLoadRpm = noLoadRpm; _rpm = noLoadRpm; } }
    public void Inject(Interlock mask) { lock (_gate) _injection = mask; }
    public void SetTemperature(double temp) { lock (_gate) _temperature = temp; }
    public Task ConnectAsync(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (_cts != null) return Task.CompletedTask;
        _model = new(config); _actual = 0;
        _cts = new();
        var token = _cts.Token;
        _task = Task.Run(async () =>
        {
            var watch = Stopwatch.StartNew(); var last = watch.Elapsed.TotalSeconds;
            using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(5));
            try
            {
                while (await timer.WaitForNextTickAsync(token))
                {
                    var now = watch.Elapsed.TotalSeconds; var dt = now - last; last = now;
                    lock (_gate)
                    {
                        _actual += (_model.Target - _actual) * (1 - Math.Exp(-dt / .035));
                        if (_performanceNoLoadRpm is { } noLoad)
                            _rpm = Math.Max(0, noLoad - Math.Abs(_actual) * noLoad / Math.Max(.1, config.AutoTest.TorqueRangeMaxNm));
                        _latest = _model.Tick(dt, _rpm, _actual, _temperature, 27, 310, _injection);
                        var p = Math.Abs(_actual * _rpm * Math.PI / 30);
                        _latest = _latest with { ExternalHealthy = true, ExternalTorqueNm = _actual, ExternalSpeedRpm = _rpm,
                            DutBusV = 30, DutCurrentA = (p / .88 + 2) / 30, DutPhaseCurrentA = (p / .88 + 2) / 30 * 1.15,
                            DutDutyCyclePct = 48, DutMotorTempC = _temperature, DutSpeedRpm = _rpm,
                            ServoCurrentA = Math.Abs(_actual) * 1.5, ServoPhaseCurrentA = Math.Abs(_actual) * 1.5,
                            ServoBusCurrentA = (p + 4) / 310, ServoDutyCyclePct = 36, ServoEfficiencyPct = 86,
                            ServoLoadPct = Math.Abs(_actual) / 2.39 * 100, ServoFeedbackPowerW = p * .86 };
                    }
                }
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        }, CancellationToken.None);
        return Task.CompletedTask;
    }
    public Task<BenchSnapshot> ReadSnapshotAsync(CancellationToken ct)
    { ct.ThrowIfCancellationRequested(); lock (_gate) return Task.FromResult(_latest); }
    public Task WriteCommandAsync(BenchCommand command, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        lock (_gate) { _model.Receive(command); if (!command.StopRequest && !command.DisableRequest) _model.StartLoad(); }
        return Task.CompletedTask;
    }
    public async Task DisconnectAsync()
    {
        if (_cts == null) return;
        await _cts.CancelAsync(); if (_task != null) await _task;
        _cts.Dispose(); _cts = null;
        lock (_gate) _latest = new();
    }
    public async ValueTask DisposeAsync() => await DisconnectAsync();
}
