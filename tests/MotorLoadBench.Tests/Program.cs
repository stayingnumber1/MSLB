using System.Diagnostics;
using System.Buffers.Binary;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json;
using MotorLoadBench.Application;
using MotorLoadBench.Domain;
using MotorLoadBench.Infrastructure;

// Safe USB/CDC soak: production VescBridge traffic with duty fixed at zero.
// Usage: --soak-vesc COM9 [seconds].
if (args.Contains("--soak-vesc", StringComparer.OrdinalIgnoreCase))
{
    var optionIndex = Array.FindIndex(args, a => string.Equals(a, "--soak-vesc", StringComparison.OrdinalIgnoreCase));
    var portName = optionIndex >= 0 && optionIndex + 1 < args.Length ? args[optionIndex + 1] : "COM9";
    var seconds = optionIndex >= 0 && optionIndex + 2 < args.Length && int.TryParse(args[optionIndex + 2], out var parsed)
        ? Math.Clamp(parsed, 5, 3600) : 60;
    var soakConfig = new BenchConfig
    {
        Vesc = new VescConfig { BaudRate = 115200, TelemetryRateHz = 10, StaleAfterMs = 500 }
    };
    var soakBridge = new VescBridge(new MockRealtimeBridge(soakConfig), soakConfig.Vesc);
    var diagnostics = new List<string>();
    soakBridge.Diagnostic += (code, message) =>
    {
        lock (diagnostics) diagnostics.Add($"{DateTimeOffset.UtcNow:O} {code} {message}");
    };
    var watch = Stopwatch.StartNew();
    var updates = 0;
    var disconnectedSamples = 0;
    try
    {
        await soakBridge.ConnectAsync(CancellationToken.None);
        using var connectTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(9));
        await soakBridge.ConnectDutAsync(portName, connectTimeout.Token);
        while (watch.Elapsed < TimeSpan.FromSeconds(seconds))
        {
            await soakBridge.SetDutAsync(DutControlMode.DutyCycle, 0, CancellationToken.None);
            updates++;
            var snapshot = await soakBridge.ReadSnapshotAsync(CancellationToken.None);
            if (!snapshot.Connected || !soakBridge.CanControlDut ||
                !WindowsSerialPortDiscovery.GetAllPorts().Contains(portName, StringComparer.OrdinalIgnoreCase))
                disconnectedSamples++;
            if (updates % 500 == 0)
                Console.WriteLine($"SOAK t={watch.Elapsed.TotalSeconds:F1}s updates={updates} disconnected={disconnectedSamples}");
            await Task.Delay(20);
        }
        await soakBridge.StopDutAsync(CancellationToken.None);
    }
    finally
    {
        await soakBridge.DisconnectAsync();
    }
    string[] captured;
    lock (diagnostics) captured = diagnostics.ToArray();
    var writeFaults = captured.Count(line =>
        line.Contains("write", StringComparison.OrdinalIgnoreCase) &&
        (line.Contains("abort", StringComparison.OrdinalIgnoreCase) ||
         line.Contains("fail", StringComparison.OrdinalIgnoreCase) ||
         line.Contains("deferred", StringComparison.OrdinalIgnoreCase)));
    var delayedTelemetry = captured.Count(line => line.Contains("vesc_telemetry_delayed", StringComparison.OrdinalIgnoreCase) ||
                                                  line.Contains("vesc_idle_telemetry_stale", StringComparison.OrdinalIgnoreCase));
    var pass = disconnectedSamples == 0 && writeFaults == 0 && delayedTelemetry == 0;
    Console.WriteLine($"SOAK RESULT: {(pass ? "PASS" : "FAIL")} seconds={watch.Elapsed.TotalSeconds:F1} updates={updates} disconnected={disconnectedSamples} write_faults={writeFaults} telemetry_delays={delayedTelemetry}");
    foreach (var line in captured.Where(line => line.Contains("abort", StringComparison.OrdinalIgnoreCase) ||
                                                line.Contains("fail", StringComparison.OrdinalIgnoreCase) ||
                                                line.Contains("deferred", StringComparison.OrdinalIgnoreCase) ||
                                                line.Contains("telemetry_delayed", StringComparison.OrdinalIgnoreCase) ||
                                                line.Contains("telemetry_stale", StringComparison.OrdinalIgnoreCase)))
        Console.WriteLine(line);
    return pass ? 0 : 3;
}

// 用法：--probe-vesc [COMx]   省略端口时默认探测 COM3。
if (args.Contains("--probe-vesc", StringComparer.OrdinalIgnoreCase))
{
    var requestedPort = "COM3";
    var probeIndex = Array.FindIndex(args, a => string.Equals(a, "--probe-vesc", StringComparison.OrdinalIgnoreCase));
    if (probeIndex >= 0 && probeIndex + 1 < args.Length && args[probeIndex + 1].StartsWith("COM", StringComparison.OrdinalIgnoreCase))
        requestedPort = args[probeIndex + 1];
    var found = false;
    var ports = WindowsSerialPortDiscovery.GetAllPortInfos()
        .Where(p => string.Equals(p.PortName, requestedPort, StringComparison.OrdinalIgnoreCase))
        .OrderByDescending(p => string.Equals(p.PortName, requestedPort, StringComparison.OrdinalIgnoreCase));
    foreach (var port in ports)
    {
        foreach (var baud in new[] { 115200, 921600, 460800, 230400, 57600, 38400, 19200 })
        {
            var probeConfig = new BenchConfig { Vesc = new VescConfig { BaudRate = baud } };
            var bridge = new VescBridge(new MockRealtimeBridge(probeConfig), probeConfig.Vesc);
            var watch = Stopwatch.StartNew();
            try
            {
                await bridge.ConnectAsync(CancellationToken.None);
                // VescBridge 内部按 DTR 状态与波特率做多轮探测（约 2s/轮），外层预算需覆盖全部轮次。
                using var timeout = new CancellationTokenSource(TimeSpan.FromMilliseconds(9000));
                await bridge.ConnectDutAsync(port.PortName, timeout.Token);
                Console.WriteLine($"VESC FOUND {port.DisplayName} @ {baud} baud in {watch.ElapsedMilliseconds} ms");
                found = true;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"NO VESC {port.DisplayName} @ {baud} in {watch.ElapsedMilliseconds} ms: {ex.GetBaseException().Message}");
            }
            finally { await bridge.DisconnectAsync(); }
            if (found) break;
        }
        if (found) break;
    }
    Console.WriteLine(found ? "PROBE RESULT: PASS" : "PROBE RESULT: NO VESC FOUND");
    return found ? 0 : 2;
}

var tests = new List<(string Name, Func<Task> Run)>();
void Test(string name, Action action) => tests.Add((name, () => { action(); return Task.CompletedTask; }));
void Check(bool ok, string message = "assertion failed") { if (!ok) throw new Exception(message); }
void Throws(Action action) { try { action(); } catch { return; } throw new Exception("expected rejection"); }
var config = new BenchConfig();
Test("750 W CST allows 1 Nm at 1000 rpm and retains power limiting", () =>
{
    var c = config with
    {
        Drive = config.Drive with { ExpectedRotationSign = -1, InstallationSign = 1 },
        Limits = config.Limits with { MaxPowerW = 750, MaxTorqueNm = 8.36,
            TorqueEnvelope = [new(0, 8.36), new(6000, 8.36)] }
    };
    var request = new BenchCommand(Mode: LoadMode.LoadedStart, TargetTorqueNm: 1);
    Check(Math.Abs(LoadMath.Demand(request, c, -1000, 25) - 1) < 1e-9);
    var limited = LoadMath.Demand(request with { TargetTorqueNm = 8.36 }, c, -6000, 25);
    Check(Math.Abs(limited * 6000 * Math.PI / 30 - 750) < 1e-6);
    Check(LoadMath.Demand(request, c, 1000, 25) == 0, "reverse protection must remain active");
});
Test("invalid config / NaN fail closed", () => Throws(() => (config with { Limits = config.Limits with { MaxTorqueNm = double.NaN } }).Validate()));
Test("motor characteristic auto test rejects speed closed-loop drive mode", () =>
    Throws(() => (config with { AutoTest = config.AutoTest with { DutDriveMode = "SpeedRpm" } }).Validate()));
Test("unverified hardware cannot write", () => Throws(config.ValidateHardware));
Test("direction coefficients validated", () => Throws(() => (config with { Drive = config.Drive with { InstallationSign = 0 } }).Validate()));
Test("ordered torque map required", () => Throws(() => BenchConfig.ValidateMap([new(100, 1), new(0, 1)])));
Test("constant power standstill is zero", () => Check(LoadMath.Demand(new(Mode: LoadMode.ConstantPower, TargetPowerW: 100), config, 0, 25) == 0));
Test("constant power low speed no division explosion", () => Check(LoadMath.Demand(new(Mode: LoadMode.ConstantPower, TargetPowerW: 100), config, .001, 25) == 0));
Test("loaded start applies opposing torque at standstill", () =>
    Check(LoadMath.Demand(new(Mode: LoadMode.LoadedStart, TargetTorqueNm: .1), config, 0, 25) == -.1));
Test("loaded start rejects reverse rotation", () =>
    Check(LoadMath.Demand(new(Mode: LoadMode.LoadedStart, TargetTorqueNm: .1), config, -5, 25) == 0));
Test("loaded start gate debounces and latches", () =>
{
    var gate = new LoadedStartGate(3, 200, 30, 300);
    var t = DateTimeOffset.UtcNow;
    Check(!gate.Update(4, 1, t).AllowTorque);
    Check(!gate.Update(-1, 1, t.AddMilliseconds(100)).AllowTorque, "startup shake must reset confirmation");
    Check(!gate.Update(4, 1, t.AddMilliseconds(200)).AllowTorque);
    Check(gate.Update(4, 1, t.AddMilliseconds(401)).AllowTorque, "confirmed forward motion must latch load");
    Check(gate.Update(-5, 1, t.AddMilliseconds(500)).AllowTorque, "minor reverse shake must retain load");
});
Test("loaded start gate trips only persistent hard reverse", () =>
{
    var gate = new LoadedStartGate(3, 200, 30, 300);
    var t = DateTimeOffset.UtcNow;
    gate.Update(4, 1, t); gate.Update(4, 1, t.AddMilliseconds(201));
    Check(gate.Update(-35, 1, t.AddMilliseconds(300)).AllowTorque);
    Check(gate.Update(-35, 1, t.AddMilliseconds(599)).AllowTorque);
    var trip = gate.Update(-35, 1, t.AddMilliseconds(601));
    Check(trip.ReverseTrip && !trip.AllowTorque);
});
Test("unexpected reverse does not become active motor", () => Check(LoadMath.Demand(new(TargetTorqueNm: .2), config, -300, 25) == 0));
Test("torque opposes verified direction", () => Check(LoadMath.Demand(new(TargetTorqueNm: .1), config, 300, 25) == -.1));
Test("power and torque caps both apply", () =>
{
    var c = config with { Limits = config.Limits with { MaxPowerW = 1 } };
    Check(Math.Abs(LoadMath.Demand(new(TargetTorqueNm: .2), c, 300, 25)) <= 1 / (10 * Math.PI) + 1e-9);
});
Test("thermal derating reduces torque", () => Check(Math.Abs(LoadMath.Demand(new(TargetTorqueNm: .2), config, 300, 65)) < .2));
Test("slew no overshoot", () => { Check(LoadMath.Slew(0, -.2, .1, .01) == -.001); Check(LoadMath.Slew(.001, 0, .2, 1) == 0); });
Test("statistics mean rms standard deviation", () => { var s = Statistics.From([1, 2, 3]); Check(s.Mean == 2 && Math.Abs(s.StdDev - Math.Sqrt(2.0/3)) < 1e-10); });
Test("missing external measurement gives no final efficiency", () => Check(new BenchSnapshot { DutBusV = 30, DutCurrentA = 1, ActualTorqueNm = 1, SpeedRpm = 300 }.EfficiencyPct == null));
Test("DUT motor efficiency uses DYN mechanical power over DC bus input", () =>
{
    var s = new BenchSnapshot
    {
        DutBusV = 30,
        DutCurrentA = 2,
        DutPhaseCurrentA = 40,
        ExternalHealthy = true,
        ExternalTorqueNm = 1,
        ExternalSpeedRpm = 280,
        DutSpeedRpm = 300
    };
    Check(Math.Abs(s.DutElectricalInputPowerW!.Value - 60) < 1e-9);
    Check(Math.Abs(s.DutMechanicalOutputPowerW!.Value - 10 * Math.PI) < 1e-9);
    Check(Math.Abs(s.DutMotorEfficiencyPct!.Value - (10 * Math.PI / 60 * 100)) < 1e-9);
});
Test("DYN-200 HEX6 decodes torque speed and CRC", () =>
{
    var cfg = new TorqueSensorConfig { Protocol = TorqueSensorProtocol.Hex6, TorqueDecimals = 3 };
    byte[] frame = [0x04, 0xD2, 0x01, 0x2C, 0, 0];
    var crc = Dyn200Protocol.Crc16(frame.AsSpan(0, 4)); frame[4] = (byte)crc; frame[5] = (byte)(crc >> 8);
    Check(Dyn200Protocol.TryDecode(frame, cfg, out var sample));
    Check(Math.Abs(sample.TorqueNm - 1.234) < 1e-12 && sample.SpeedRpm == 300);
    frame[0] ^= 1; Check(!Dyn200Protocol.TryDecode(frame, cfg, out _), "corrupt CRC must be rejected");
});
Test("DYN-200 HEX6 torque sign is carried by speed high bit", () =>
{
    var cfg = new TorqueSensorConfig { Protocol = TorqueSensorProtocol.Hex6, TorqueDecimals = 3 };
    byte[] frame = [0, 10, 0x80, 20, 0, 0];
    var crc = Dyn200Protocol.Crc16(frame.AsSpan(0, 4)); frame[4] = (byte)crc; frame[5] = (byte)(crc >> 8);
    Check(Dyn200Protocol.TryDecode(frame, cfg, out var sample) && sample.TorqueNm == -.01 && sample.SpeedRpm == 20);
});
Test("DYN-200 HEX8 decodes signed 24-bit torque", () =>
{
    var cfg = new TorqueSensorConfig { Protocol = TorqueSensorProtocol.Hex8, TorqueDecimals = 3 };
    byte[] frame = [0xff, 0xff, 0xfe, 0, 0, 100, 0, 0];
    var crc = Dyn200Protocol.Crc16(frame.AsSpan(0, 6)); frame[6] = (byte)crc; frame[7] = (byte)(crc >> 8);
    Check(Dyn200Protocol.TryDecode(frame, cfg, out var sample) && sample.TorqueNm == -.002 && sample.SpeedRpm == 100);
});
Test("invalid recipe over limit rejected", () => Throws(() => BenchConfig.ValidateRecipe(new() { Points = [new() { TorqueNm = 2 }] }, config.Limits)));
Test("torque curve recipe creates rpm and torque grid", () =>
{
    var recipe = TorqueCurveRecipeFactory.Create(new(100, 300, 100, .05, .15, .05), config);
    Check(recipe.Points.Count == 9);
    Check(recipe.Points.Select(p => p.Id).Distinct().Count() == recipe.Points.Count);
    Check(recipe.Points.All(p => p.TorqueNm * p.Rpm * Math.PI / 30 <= config.Limits.MaxPowerW));
});
Test("torque curve recipe removes points above power limit", () =>
{
    var limited = config with { Limits = config.Limits with { MaxPowerW = 2 } };
    var recipe = TorqueCurveRecipeFactory.Create(new(100, 400, 100, .05, .2, .05), limited);
    Check(recipe.Points.Count > 0 && recipe.Points.Count < 20);
    Check(recipe.Points.All(p => p.TorqueNm * p.Rpm * Math.PI / 30 <= 2 + 1e-9));
});
Test("PDO wire layout matches packed PLC", () => { Check(Marshal.SizeOf<CommandWire>() == 42); Check(Marshal.SizeOf<StatusWire>() == 126); });
Test("heartbeat watchdog latches and ramps down", () =>
{
    var m = new RealtimeModel(config);
    for (uint i = 1; i <= 150; i++) { m.Receive(new(EnableRequest: i == 1, TargetTorqueNm: .1, Heartbeat: i)); m.Tick(.01,300,m.Target); }
    Check(m.ServoOn && m.Target < -.09);
    for (int i = 0; i < 200; i++) m.Tick(.01,300,m.Target);
    Check(m.Fault.HasFlag(Interlock.Heartbeat) && !m.ServoOn && m.Target == 0);
    m.Receive(new(EnableRequest: true, TargetTorqueNm: .1, Heartbeat: 999));
    m.Tick(.01,300,0); Check(!m.ServoOn, "reconnect must not resume");
});
Test("emergency clears torque immediately and reset does not enable", () =>
{
    var m = new RealtimeModel(config);
    m.Receive(new(EnableRequest:true,TargetTorqueNm:.1,Heartbeat:1)); m.Tick(.1,300,0);
    var s = m.Tick(.01,300,0,external:Interlock.Emergency); Check(s.TargetTorqueNm == 0 && s.State == BenchState.Fault);
    m.Receive(new(ResetFault:true,Heartbeat:2)); s=m.Tick(.01,300,0);
    Check(!s.ServoOn && s.Interlocks == Interlock.None && s.TargetTorqueNm == 0);
});
Test("peak time budget cannot run indefinitely", () =>
{
    var c = config with { Limits = config.Limits with { ContinuousTorqueNm=.05,PeakSeconds=.1 } };
    var m = new RealtimeModel(c);
    for(uint i=1;i<50;i++){m.Receive(new(EnableRequest:i==1,TargetTorqueNm:.15,Heartbeat:i));m.Tick(.01,300,m.Target);}
    Check(m.Fault.HasFlag(Interlock.PeakTimeout));
});
Test("stale PLC heartbeat detected independently", () =>
{
    var mgr = new SafetyManager(config); var now=DateTimeOffset.UtcNow;
    var s = new BenchSnapshot { Connected=true,EtherCatOnline=true,DriveReady=true,Timestamp=now,PlcHeartbeat=42 };
    Check(mgr.Evaluate(s,now)==null); Check(mgr.Evaluate(s with {Timestamp=now.AddSeconds(1)},now.AddSeconds(1))!=null);
});
Test("malformed feedback trips", () =>
{
    var mgr=new SafetyManager(config);
    Check(mgr.Evaluate(new(){Connected=true,EtherCatOnline=true,SpeedRpm=double.NaN},DateTimeOffset.UtcNow)!=null);
});
Test("DUT hard protection thresholds include 130 C, 35 A bus and 100 A phase current", () =>
{
    Check(config.Limits.DutMotorTripC == 130);
    var now = DateTimeOffset.UtcNow;
    var healthy = new BenchSnapshot { Connected=true, EtherCatOnline=true, DriveReady=true, State=BenchState.DriveReady, Timestamp=now, PlcHeartbeat=1 };
    Check(SafetyManager.RequiresImmediateDutStop(new SafetyManager(config).Evaluate(healthy with { DutMotorTempC=130 }, now)!));
    Check(SafetyManager.RequiresImmediateDutStop(new SafetyManager(config).Evaluate(healthy with { DutCurrentA=35 }, now)!));
    Check(SafetyManager.RequiresImmediateDutStop(new SafetyManager(config).Evaluate(healthy with { DutPhaseCurrentA=100 }, now)!));
});
Test("DYN soft zero before precheck only at rest and only for small offsets", () =>
{
    var rest = new BenchSnapshot { ExternalHealthy = true, ExternalTorqueNm = 0.066, DutSpeedRpm = 0, ActualTorqueNm = 0 };
    Check(DynSoftZero.Decide(rest, config) == DynZeroDecision.Zero, "field drift +0.066 N.m at rest must be zeroed");
    Check(DynSoftZero.Decide(rest with { ExternalTorqueNm = -0.028 }, config) == DynZeroDecision.Zero, "small offsets inside the tolerance are zeroed too");
    Check(DynSoftZero.Decide(rest with { DutSpeedRpm = config.AutoTest.ZeroRpmTolerance + 1 }, config) == DynZeroDecision.NotAtRest, "turning DUT");
    Check(DynSoftZero.Decide(rest with { DutSpeedRpm = null }, config) == DynZeroDecision.NotAtRest, "unknown DUT speed");
    Check(DynSoftZero.Decide(rest with { ActualTorqueNm = config.Limits.ZeroTorqueNm * 5 }, config) == DynZeroDecision.NotAtRest, "servo torque present");
    Check(DynSoftZero.Decide(rest with { ExternalTorqueNm = DynSoftZero.MaxOffsetNm + 0.01 }, config) == DynZeroDecision.TooLarge, "large offset is not hidden");
    Check(DynSoftZero.Decide(rest with { ExternalHealthy = false }, config) == DynZeroDecision.NoData, "unhealthy DYN");
    Check(DynSoftZero.Decide(rest with { ExternalTorqueNm = null }, config) == DynZeroDecision.NoData, "no DYN reading");
    Check(DynSoftZero.Decide(null, config) == DynZeroDecision.NoData, "no snapshot");
});
async Task Wait(Func<bool> condition, double seconds=3)
{
    var w=Stopwatch.StartNew();
    while(!condition()){if(w.Elapsed.TotalSeconds>seconds)throw new Exception("wait timeout");await Task.Delay(10);}
}
async Task VerifyDutHardTrip(BenchSnapshot trip, bool autoTest, double servoTorqueNm = 0, int servoZeroDelayMs = 0, bool servoNeverZero = false)
{
    var bridge = new SafetyDutBridge();
    if (servoTorqueNm > 0) bridge.LoadServo(servoTorqueNm, servoZeroDelayMs, servoNeverZero);
    await using var runtime = new BenchRuntime(bridge, config, new NullSessionRecorder());
    if (autoTest) runtime.EmergencyStopDutAsync = bridge.StopDutAsync;
    await runtime.StartAsync(CancellationToken.None);
    if (autoTest) runtime.AcquireAutoTest();
    bridge.SetSnapshot(trip with { TargetTorqueNm = servoTorqueNm, ActualTorqueNm = servoTorqueNm });
    await Wait(() => bridge.Events.Contains("dut-stop-complete"));

    var events = bridge.Events;
    var frames = bridge.StopFrames;
    // Servo torque first: a DUT stop under a loaded CST servo lets the servo reverse-drive the shaft
    // (field 2026-10-05: ~6000 rpm reverse, DUT bus 38 -> 48 V).
    Check(events.IndexOf("servo-stop") >= 0 && events.IndexOf("servo-stop") < events.IndexOf("dut-stop-start"),
        "the servo stop command must go out before the DUT stop frames");
    if (servoTorqueNm > 0 && !servoNeverZero)
        Check(events.IndexOf("servo-torque-zero") >= 0 && events.IndexOf("servo-torque-zero") < events.IndexOf("dut-stop-start"),
            "the DUT stop must wait until the servo torque is back at zero");
    Check(bridge.StopRamps[0] == config.AutoTest.StallCrossingTorqueRampNmPerSec, "DUT hard trip must zero the servo torque with the emergency ramp");
    Check(frames.Count == VescEmergencyStop.FrameCount, "hard trip must send exactly three stop frames in its first latched action");
    foreach (var captured in frames)
    {
        var pending = captured.Frame.ToList();
        Check(VescProtocol.TryTakeFrame(pending, out var payload));
        Check(payload[0] == VescProtocol.CommSetCurrent && BinaryPrimitives.ReadInt32BigEndian(payload.AsSpan(1)) == 0,
            "hard trip frame must be COMM_SET_CURRENT zero");
    }
    Check((frames[^1].At - frames[0].At).TotalMilliseconds >= VescEmergencyStop.FrameIntervalMs,
        "repeated stop frames must be separated in time");
    Check(!bridge.Active && runtime.Owner == ControlOwner.Safety, "hard trip must clear active DUT output and latch safety ownership");
    await Task.Delay(80);
    Check(bridge.StopCalls == 1 && bridge.SetCalls == 0, "latched hard trip must not automatically restart or repeat the active command");
    Throws(() => runtime.SetDutAsync(DutControlMode.DutyCycle, .2, CancellationToken.None).GetAwaiter().GetResult());
}
tests.Add(("manual mode 130 C trip zeroes servo torque before stopping DUT and cannot auto-resume", () =>
    VerifyDutHardTrip(new BenchSnapshot { DutMotorTempC=130 }, autoTest:false)));
tests.Add(("automatic mode 35 A bus-current trip zeroes servo torque before stopping DUT", () =>
    VerifyDutHardTrip(new BenchSnapshot { DutCurrentA=35 }, autoTest:true)));
tests.Add(("automatic mode 100 A phase-current trip zeroes servo torque before stopping DUT", () =>
    VerifyDutHardTrip(new BenchSnapshot { DutPhaseCurrentA=100 }, autoTest:true)));
tests.Add(("loaded 130 C trip stops DUT only after the 2.97 Nm servo torque is back at zero", () =>
    VerifyDutHardTrip(new BenchSnapshot { DutMotorTempC=130 }, autoTest:true, servoTorqueNm:2.97, servoZeroDelayMs:150)));
tests.Add(("hard trip still stops DUT when the servo torque never confirms zero", () =>
    VerifyDutHardTrip(new BenchSnapshot { DutMotorTempC=130 }, autoTest:true, servoTorqueNm:2.97, servoNeverZero:true)));
tests.Add(("SERVO_REVERSE after a latched trip escalates to the emergency torque ramp", async () =>
{
    var bridge = new SafetyDutBridge();
    await using var runtime = new BenchRuntime(bridge, config, new NullSessionRecorder());
    await runtime.StartAsync(CancellationToken.None);
    var hot = new BenchSnapshot { MotorTempC = config.Limits.MotorTripC };
    bridge.SetSnapshot(hot);
    await Wait(() => bridge.StopRamps.Count > 0);
    Check(bridge.StopRamps[0] == config.Limits.StopRampNmPerSec, "a servo-side trip keeps the normal stop ramp");
    bridge.SetSnapshot(hot with { SpeedRpm = -config.Drive.ExpectedRotationSign * (config.Limits.LoadedStartReverseTripRpm + 100) });
    await Wait(() => bridge.StopRamps.Contains(config.AutoTest.StallCrossingTorqueRampNmPerSec));
}));
async Task VerifyOrchestratorServoFirst(string endReason, double? expectedDutArg, bool expectDisable)
{
    var bridge = new SafetyDutBridge();
    bridge.LoadServo(2.97, zeroDelayMs: 120, neverZero: false);
    bridge.SetSnapshot(new BenchSnapshot { TargetTorqueNm = 2.97, ActualTorqueNm = 2.97 });
    await using var runtime = new BenchRuntime(bridge, config, new NullSessionRecorder());
    await runtime.StartAsync(CancellationToken.None);
    var orchestrator = new TestOrchestrator(runtime, config, new NullSessionRecorder());
    typeof(TestOrchestrator).GetProperty(nameof(TestOrchestrator.EndReason))!.SetValue(orchestrator, endReason);
    var calls = new List<(double? Arg, bool TorqueWasZero)>();
    Func<double?, CancellationToken, Task> stopDut = (arg, _) =>
    {
        calls.Add((arg, bridge.Events.Contains("servo-torque-zero")));
        return Task.CompletedTask;
    };
    var safeStop = typeof(TestOrchestrator).GetMethod("SafeStopAsync", BindingFlags.NonPublic | BindingFlags.Instance)!;
    await (Task)safeStop.Invoke(orchestrator, [stopDut])!;
    Check(calls.Count == 1 && calls[0].Arg == expectedDutArg, $"{endReason}: DUT stop must be requested once with {expectedDutArg?.ToString() ?? "null"}");
    Check(calls[0].TorqueWasZero, $"{endReason}: DUT stop must come after the servo torque is back at zero");
    Check(bridge.StopRamps[0] == config.AutoTest.StallCrossingTorqueRampNmPerSec, $"{endReason}: servo torque must use the emergency ramp");
    Check(bridge.DisableRequested == expectDisable, $"{endReason}: servo disable expected={expectDisable}");
}
tests.Add(("TEMPERATURE_LIMIT safe stop zeroes servo torque, then stops DUT, then disables", () =>
    VerifyOrchestratorServoFirst("TEMPERATURE_LIMIT", 0, expectDisable: true)));
tests.Add(("COMPLETED_STALL_200_RPM safe stop zeroes servo torque before the DUT duty", () =>
    VerifyOrchestratorServoFirst("COMPLETED_STALL_200_RPM", null, expectDisable: false)));
tests.Add(("COMPLETED_STALL_CROSSING safe stop zeroes servo torque before the DUT duty", () =>
    VerifyOrchestratorServoFirst("COMPLETED_STALL_CROSSING", null, expectDisable: true)));
tests.Add(("COMPLETED_END_TORQUE safe stop zeroes servo torque before the DUT duty", () =>
    VerifyOrchestratorServoFirst("COMPLETED_END_TORQUE", null, expectDisable: false)));
Test("end-above torque is off by default and must be finite and >= 0", () =>
{
    Check(config.AutoTest.EndAboveTorqueNm == 0);
    (config with { AutoTest = config.AutoTest with { EndAboveTorqueNm = 2.9 } }).Validate();
    Throws(() => (config with { AutoTest = config.AutoTest with { EndAboveTorqueNm = -1 } }).Validate());
    Throws(() => (config with { AutoTest = config.AutoTest with { EndAboveTorqueNm = double.NaN } }).Validate());
});
tests.Add(("DUT-only runtime hard trip zeroes the real load servo torque before stopping the DUT", async () =>
{
    // GUI layout: the servo runtime and a separate DUT runtime (VescBridge over a mock servo). Only the
    // DUT runtime sees the trip here; it must still wait for the real servo (field 2026-10-05 22:22).
    var servoBridge = new SafetyDutBridge();
    servoBridge.LoadServo(2.2, zeroDelayMs: 150, neverZero: false);
    servoBridge.SetSnapshot(new BenchSnapshot { TargetTorqueNm = 2.2, ActualTorqueNm = 2.2 });
    await using var servoRuntime = new BenchRuntime(servoBridge, config, new NullSessionRecorder());
    await servoRuntime.StartAsync(CancellationToken.None);
    var dutBridge = new SafetyDutBridge();
    await using var dutRuntime = new BenchRuntime(dutBridge, config, new NullSessionRecorder());
    dutRuntime.LoadServoState = () => servoRuntime.Latest;
    dutRuntime.RequestLoadServoStop = (ramp, reason) => servoRuntime.RequestStop(true, ramp, "DUT runtime hard trip: " + reason);
    await dutRuntime.StartAsync(CancellationToken.None);
    dutBridge.SetSnapshot(new BenchSnapshot { DutMotorTempC = 130 });
    await Wait(() => dutBridge.Events.Contains("dut-stop-complete"));
    var zeroAt = servoBridge.FirstAt("servo-torque-zero");
    var stopAt = dutBridge.FirstAt("dut-stop-start");
    Check(servoBridge.StopRamps.Contains(config.AutoTest.StallCrossingTorqueRampNmPerSec), "the DUT runtime must ask the real servo for an emergency torque zero");
    Check(zeroAt is not null && stopAt is not null && zeroAt <= stopAt, "the DUT stop must wait for the real servo torque zero");
}));
tests.Add(("AUTO_TEST ownership rejects manual service commands", async () =>
{
    var bridge = new MockRealtimeBridge(config);
    await using var runtime = new BenchRuntime(bridge, config, new NullSessionRecorder());
    await runtime.StartAsync(CancellationToken.None);
    runtime.AcquireAutoTest();
    Throws(() => runtime.Load(.05, .1, requester: ControlOwner.Manual));
    Throws(() => runtime.Enable(ControlOwner.Manual));
    runtime.ReleaseAutoTest();
}));
tests.Add(("full mock recipe + CSV + JSON + stop",async () =>
{
    var root=Path.Combine(Environment.CurrentDirectory,"artifacts","test-sessions");
    var bridge=new MockRealtimeBridge(config);
    var recorder=new SessionRecorder(root,config,true,"test");
    await using(var runtime=new BenchRuntime(bridge,config,recorder))
    {
        await runtime.StartAsync(CancellationToken.None);
        Check(runtime.Latest.Connected && runtime.Latest.DriveReady,
            "StartAsync must publish the first healthy snapshot before returning");
        runtime.Enable(); await Wait(()=>runtime.Latest.ServoOn);
        var recipe=new Recipe{Points=[new(){Id="integration",Rpm=500,TorqueNm=.05,StableSeconds=.05,SampleSeconds=1,MaxDurationSeconds=6}]};
        var runner=new TestOrchestrator(runtime,config,recorder);
        var stages = new List<AutoTestStage>(); runner.StatusChanged += () => stages.Add(runner.Stage);
        await runner.RunAsync(recipe,CancellationToken.None, preparePoint: (_, _) => { bridge.SetSpeed(config.AutoTest.NominalMaxSpeedRpm); return Task.CompletedTask; });
        await runtime.StopAsync(true,CancellationToken.None);
        Check(!runtime.Latest.ServoOn);
        Check(Directory.GetFiles(recorder.DirectoryPath,"point_*.json").Length==2);
        var result=Directory.GetFiles(recorder.DirectoryPath,"point_*.json").Select(path => JsonSerializer.Deserialize<PointResult>(File.ReadAllText(path),BenchConfig.Json)!).Single(x => x.PointId == "integration");
        Check(result.Result=="PASS" && result.Torque!.Count>=2);
        Check(stages.Contains(AutoTestStage.PRECHECK) && stages.Contains(AutoTestStage.TORQUE_ZERO) &&
            stages.Contains(AutoTestStage.DIRECTION_CHECK) && stages.Contains(AutoTestStage.NO_LOAD) &&
            stages.Contains(AutoTestStage.LOAD_SWEEP) && stages.Contains(AutoTestStage.WAIT_STABLE) &&
            stages.Contains(AutoTestStage.SAMPLE) && stages.Contains(AutoTestStage.RAMP_DOWN) &&
            stages.Contains(AutoTestStage.DUT_STOP) && stages.Contains(AutoTestStage.ANALYSIS) &&
            stages.Contains(AutoTestStage.FINISHED), "full automatic state sequence must execute");
    }
    Check(File.ReadAllLines(Path.Combine(recorder.DirectoryPath,"samples.csv")).Length>10);
    Check(File.Exists(Path.Combine(recorder.DirectoryPath,"closed.json")));
}));
tests.Add(("EndAboveTorqueNm ends the sweep once the measured torque is above it and records the final point", async () =>
{
    // Constant mock speed (no sustained decline, so no STALL_APPROACH); mock DYN torque = servo actual torque.
    var c = config with
    {
        Limits = config.Limits with { MaxTorqueNm = .3, ContinuousTorqueNm = .3, TorqueEnvelope = [new(0, .3), new(6000, .3)] },
        AutoTest = config.AutoTest with { EndAboveTorqueNm = .2, TorqueRangeMaxNm = .3 }
    };
    c.Validate();
    var bridge = new MockRealtimeBridge(c);
    var recorder = new SessionRecorder(Path.Combine(Environment.CurrentDirectory, "artifacts", "test-sessions"), c, true, "test");
    await using var runtime = new BenchRuntime(bridge, c, recorder);
    await runtime.StartAsync(CancellationToken.None);
    runtime.Enable(); await Wait(() => runtime.Latest.ServoOn);
    TestPoint P(string id, double nm) => new() { Id = id, TorqueNm = nm, RampNmPerSec = .3, StableSeconds = .05, SampleSeconds = 1, MaxDurationSeconds = 6 };
    var recipe = new Recipe { Points = [P("T0_10", .10), P("T0_24", .24)] };
    var runner = new TestOrchestrator(runtime, c, recorder);
    var run = runner.RunAsync(recipe, CancellationToken.None, preparePoint: (_, _) => { bridge.SetSpeed(3000); return Task.CompletedTask; });
    Check(await Task.WhenAny(run, Task.Delay(TimeSpan.FromSeconds(20))) == run, "the sweep must end by itself");
    await run;
    Check(runner.EndReason == "COMPLETED_END_TORQUE", $"end reason {runner.EndReason}");
    var results = Directory.GetFiles(recorder.DirectoryPath, "point_*.json")
        .Select(path => JsonSerializer.Deserialize<PointResult>(File.ReadAllText(path), BenchConfig.Json)!).ToList();
    var ids = string.Join(",", results.Select(r => r.PointId).OrderBy(x => x, StringComparer.Ordinal));
    Check(ids == "END_TORQUE_FINAL,NO_LOAD,T0_10", $"recorded points {ids}");
    var final = results.Single(r => r.PointId == "END_TORQUE_FINAL");
    Check(final.Result == "PASS" && final.Torque!.Count >= 2 && final.Torque.Min > .2, $"final point torque min {final.Torque?.Min}");
    Check(results.Single(r => r.PointId == "T0_10").Torque!.Mean < .2, "points below the end torque are recorded normally");
    await runtime.StopAsync(true, CancellationToken.None);
}));
tests.Add(("safety trip always aborts even Skip recipe",async () =>
{
    var b=new MockRealtimeBridge(config);
    var rec=new SessionRecorder(Path.Combine(Environment.CurrentDirectory,"artifacts","test-sessions"),config,true,"test");
    await using var r=new BenchRuntime(b,config,rec);
    await r.StartAsync(CancellationToken.None); await Wait(()=>r.Latest.DriveReady);
    r.Enable(); await Wait(()=>r.Latest.ServoOn);
    var runner=new TestOrchestrator(r,config,rec);
    var task=runner.RunAsync(new(){Points=[new(){Id="fault",StableSeconds=.1,SampleSeconds=2,OnFail=OnFail.Skip}]},CancellationToken.None);
    await Task.Delay(200); b.Inject(Interlock.Emergency);
    bool failed=false; try{await task;}catch(SafetyTripException){failed=true;}
    Check(failed); await Wait(()=>!r.Latest.ServoOn);
}));
Test("passive loading rejects same torque/speed sign", () => Throws(() => (config with { Drive = config.Drive with { InstallationSign = 1 } }).Validate()));
Test("constant torque ramps from standstill and remains below 50 RPM", () =>
{
    foreach (var rpm in new[] { 0.0, .001, 3, 49.9, 50 })
        Check(LoadMath.Demand(new(TargetTorqueNm: .1), config, rpm, 25) == -.1);
    Check(LoadMath.Demand(new(TargetTorqueNm: .1), config, -3, 25) == 0);
    Check(LoadMath.Demand(new(TargetTorqueNm: 1), config, 0, 25) == -config.Limits.MaxTorqueNm);
    Check(LoadMath.Demand(new(TargetTorqueNm: .1), config, 0, config.Limits.MotorTripC) == 0);
    var m = new RealtimeModel(config);
    m.Receive(new(EnableRequest: true, TargetTorqueNm: .1, TorqueRampNmPerSec: .1, Heartbeat: 1));
    Check(Math.Abs(m.Tick(.01, 0, 0).TargetTorqueNm + .001) < 1e-9);
    for (uint i = 2; i <= 150; i++)
    {
        m.Receive(new(TargetTorqueNm: .1, TorqueRampNmPerSec: .1, Heartbeat: i));
        m.Tick(.01, 0, m.Target);
    }
    Check(m.ServoOn && Math.Abs(m.Target + .1) < 1e-9);
    m.Receive(new(StopRequest: true, DisableRequest: true, Heartbeat: 151));
    for (uint i = 152; i <= 250; i++)
    {
        m.Receive(new(StopRequest: true, DisableRequest: true, Heartbeat: i));
        m.Tick(.01, 0, m.Target);
    }
    Check(!m.ServoOn && m.Target == 0);
});
Test("P1 standard performance recipe scans torque only from 0.10 to 4.00 Nm", () =>
{
    var c = config with { Limits = config.Limits with { MaxTorqueNm = 8.36, MaxPowerW = 750,
        TorqueEnvelope = [new(0, 8.36), new(6000, 8.36)] } };
    var recipe = TorqueCurveRecipeFactory.CreateStandardPerformance(c);
    Check(recipe.Name == "P1 标准性能曲线" && recipe.Points.Count == 40);
    Check(recipe.Points.Select(p => p.Rpm).Distinct().Count() == 1, "RPM must not be swept as a command");
    Check(recipe.Points.Select((p, i) => Math.Abs(p.TorqueNm - (i + 1) * .1) < 1e-9).All(ok => ok), "servo torque must sweep by 0.10 N·m");
    Check(Math.Abs(recipe.Points[0].TorqueNm - .1) < 1e-9 && Math.Abs(recipe.Points[^1].TorqueNm - 4) < 1e-9);
    Check(recipe.Points.Select(p => p.Rpm).Distinct().Count() == 1, "DUT drive condition must not scan RPM");
});
Test("torque map falling to standstill removes target without ramp delay", () =>
{
    var m = new RealtimeModel(config);
    for (uint i = 1; i <= 150; i++) { m.Receive(new(EnableRequest: i == 1, Mode: LoadMode.TorqueMap, Heartbeat: i)); m.Tick(.01,300,m.Target); }
    Check(m.Target < -.04);
    var s = m.Tick(.001,0,m.Target);
    Check(s.TargetTorqueNm == 0, "residual ramp torque must not drive the stopped shaft");
});
Test("VESC short packet framing and CRC reject corruption", () =>
{
    var frame = VescProtocol.SetRpm(300, 7);
    var pending = frame.ToList();
    Check(VescProtocol.TryTakeFrame(pending, out var payload));
    Check(payload[0] == VescProtocol.CommSetRpm && BinaryPrimitives.ReadInt32BigEndian(payload.AsSpan(1)) == 2100);
    frame[3] ^= 1;
    pending = frame.ToList();
    Check(!VescProtocol.TryTakeFrame(pending, out _), "corrupt VESC CRC must be rejected");
});
Test("VESC mechanical RPM commands convert pole pairs and enforce both speed limits", () =>
{
    var config = new VescConfig { MotorPolePairs = 7, MaxRpm = 5000, CurrentModeMaxRpm = 4000 };
    foreach (var rpm in new[] { -5000, 0, 5000 })
    {
        var pending = VescProtocol.SetMechanicalRpm(rpm, config).ToList();
        Check(VescProtocol.TryTakeFrame(pending, out var payload));
        Check(payload[0] == VescProtocol.CommSetRpm);
        Check(BinaryPrimitives.ReadInt32BigEndian(payload.AsSpan(1)) == rpm * 7);
    }
    Throws(() => VescProtocol.SetMechanicalRpm(5001, config));
    Throws(() => VescProtocol.SetMechanicalRpm(-5001, config));
    Throws(() => VescProtocol.SetMechanicalRpm(6001, config with { MaxRpm = 6000 }));
    Throws(() => VescProtocol.SetMechanicalRpm(double.NaN, config));
});
Test("VESC long packet framing accepts 16-bit and 24-bit lengths", () =>
{
    static byte[] LongFrame(byte start, byte[] payload)
    {
        var offset = start == 3 ? 3 : 4;
        var frame = new byte[offset + payload.Length + 3];
        frame[0] = start;
        if (start == 3) { frame[1] = (byte)(payload.Length >> 8); frame[2] = (byte)payload.Length; }
        else { frame[1] = (byte)(payload.Length >> 16); frame[2] = (byte)(payload.Length >> 8); frame[3] = (byte)payload.Length; }
        payload.CopyTo(frame, offset);
        BinaryPrimitives.WriteUInt16BigEndian(frame.AsSpan(offset + payload.Length), VescProtocol.Crc16(payload));
        frame[^1] = 3;
        return frame;
    }
    var payload = Enumerable.Range(0, 300).Select(i => (byte)i).ToArray();
    foreach (var start in new byte[] { 3, 4 })
    {
        var pending = LongFrame(start, payload).ToList();
        Check(VescProtocol.TryTakeFrame(pending, out var decoded));
        Check(decoded.SequenceEqual(payload));
    }
    var fragmented = LongFrame(3, payload);
    var partial = fragmented.Take(100).ToList();
    Check(!VescProtocol.TryTakeFrame(partial, out _), "fragmented valid long frame must wait for remaining bytes");
    partial.AddRange(fragmented.Skip(100));
    Check(VescProtocol.TryTakeFrame(partial, out var completed) && completed.SequenceEqual(payload));
});
Test("VESC parser rejects oversized false long header and resynchronizes", () =>
{
    var valid = VescProtocol.Request(VescProtocol.CommGetValues);
    var pending = new byte[] { 0x03, 0x08, 0x7F }.Concat(valid).ToList();
    Check(VescProtocol.TryTakeFrame(pending, out var payload), "oversized false header must not pin parser");
    Check(payload.SequenceEqual(new byte[] { VescProtocol.CommGetValues }));
});
Test("VESC parser bypasses incomplete plausible false header when validated frame follows", () =>
{
    var valid = VescProtocol.Request(VescProtocol.CommFwVersion);
    var pending = new byte[] { 0x03, 0x00, 0x64, 0x55, 0xAA }.Concat(valid).ToList();
    Check(VescProtocol.TryTakeFrame(pending, out var payload), "validated later frame must resynchronize stream");
    Check(payload.SequenceEqual(new byte[] { VescProtocol.CommFwVersion }));
});
Test("VESC direct duty current and brake commands scale correctly", () =>
{
    foreach (var item in new[]
    {
        (Frame: VescProtocol.SetDuty(.2), Command: VescProtocol.CommSetDuty, Raw: 20000),
        (Frame: VescProtocol.SetCurrent(3), Command: VescProtocol.CommSetCurrent, Raw: 3000),
        (Frame: VescProtocol.SetBrakeCurrent(3), Command: VescProtocol.CommSetCurrentBrake, Raw: 3000)
    })
    {
        var pending = item.Frame.ToList();
        Check(VescProtocol.TryTakeFrame(pending, out var payload));
        Check(payload[0] == item.Command && BinaryPrimitives.ReadInt32BigEndian(payload.AsSpan(1)) == item.Raw);
    }
});
Test("VESC external-control claim disables onboard app output indefinitely", () =>
{
    var pending = VescProtocol.DisableAppOutput().ToList();
    Check(VescProtocol.TryTakeFrame(pending, out var payload));
    Check(payload.Length == 6);
    Check(payload[0] == VescProtocol.CommAppDisableOutput);
    Check(payload[1] == 0, "external-control claim must not be forwarded over CAN");
    Check(BinaryPrimitives.ReadInt32BigEndian(payload.AsSpan(2)) == -1);
});
Test("VESC position command uses degrees times one million", () =>
{
    var pending = VescProtocol.SetPosition(12.5).ToList();
    Check(VescProtocol.TryTakeFrame(pending, out var payload));
    Check(payload[0] == VescProtocol.CommSetPosition && BinaryPrimitives.ReadInt32BigEndian(payload.AsSpan(1)) == 12500000);
});
Test("VESC GET_VALUES telemetry scaling", () =>
{
    var payload = new byte[54]; var p = 0;
    payload[p++] = VescProtocol.CommGetValues;
    void I16(short value) { BinaryPrimitives.WriteInt16BigEndian(payload.AsSpan(p), value); p += 2; }
    void I32(int value) { BinaryPrimitives.WriteInt32BigEndian(payload.AsSpan(p), value); p += 4; }
    I16(420); I16(355); I32(1234); I32(567); I32(0); I32(0); I16(321); I32(2100); I16(502);
    Check(VescProtocol.TryDecodeValues(payload, out var v));
    Check(v.FetTemperatureC == 42 && v.MotorTemperatureC == 35.5 && v.MotorCurrentA == 12.34);
    Check(v.InputCurrentA == 5.67 && v.DutyCycle == .321 && v.ElectricalRpm == 2100 && v.InputVoltageV == 50.2);
});
Test("Windows serial discovery keeps every COM port and orders numerically", () =>
{
    var ports = WindowsSerialPortDiscovery.OrderPorts(["COM5", "com3", "COM12", "COM4", "COM3"]);
    Check(ports.SequenceEqual(["COM3", "COM4", "COM5", "COM12"]));
});
// ---- Map scan (DUT speed loop + CST torque steps) ----
void AddAsync(string name, Func<Task> run) => tests.Add((name, run));
MapScanPlan SmallMapPlan(params MapColumn[] columns) => new()
{
    Columns = [.. columns], StartTorqueNm = .05, TorqueStepNm = .05, SampleSeconds = .5, TorqueRampNmPerSec = .3,
    SpeedRampRpmPerSec = 5000, StableTimeoutSeconds = 4, SpeedSettleSeconds = 3, Order = MapColumnOrder.Ascending, DynCorrectionMaxNm = 0
};
MapCellSnapshot Cell(MapScanGrid grid, double rpm, double torque) => grid.Snapshot().Single(c => c.Id == MapScanPlanner.PointId(rpm, torque));
Test("Map YKB-145 plan has 117 cells, interleaved column order and validated caps", () =>
{
    var plan = MapScanPlanner.Ykb145();
    Check(new MapScanGrid(plan).Total == 117);
    Check(MapScanPlanner.OrderedSpeeds(plan).Take(4).SequenceEqual([650.0, 3500, 800, 3250]), "interleaved: lowest, highest, 2nd lowest ...");
    Check(MapScanPlanner.OrderedSpeeds(plan with { Order = MapColumnOrder.Ascending }).SequenceEqual(plan.Columns.Select(c => c.Rpm)));
    Check(MapScanPlanner.Torques(plan, plan.Columns[^1]).SequenceEqual([.25, .5, .75, 1.0]));
    var bench = config with { Limits = config.Limits with { MaxTorqueNm = 8.36, ContinuousTorqueNm = 4, TorqueEnvelope = [new(0, 8.36), new(6000, 8.36)] } };
    MapScanPlanner.Validate(plan, bench);
    Throws(() => MapScanPlanner.Validate(plan, bench with { Limits = bench.Limits with { ContinuousTorqueNm = 2.39 } }));
    Throws(() => MapScanPlanner.Validate(plan with { ResumeTempC = 95 }, bench));
    Throws(() => MapScanPlanner.Validate(plan with { PauseTempC = 130 }, bench));
    Throws(() => MapScanPlanner.Validate(plan with { Columns = [new(40, 1)] }, bench));
    Throws(() => MapScanPlanner.Validate(plan with { Columns = [new(3500, 3)] }, bench with { Limits = bench.Limits with { MaxPowerW = 1000 } }));
    Throws(() => MapScanPlanner.Validate(plan with { TorqueRampNmPerSec = 1 }, bench));
});
Test("Map columns parse rpm:cap with a default cap and reject duplicates", () =>
{
    var columns = MapScanPlanner.ParseColumns("800, 650:3，1000：2.75\n1250", 2.5);
    Check(columns.Select(c => c.Rpm).SequenceEqual([650.0, 800, 1000, 1250]));
    Check(columns.Select(c => c.MaxTorqueNm).SequenceEqual([3.0, 2.5, 2.75, 2.5]));
    Throws(() => MapScanPlanner.ParseColumns("650, 650:2", 1));
    Throws(() => MapScanPlanner.ParseColumns("abc", 1));
    Check(MapScanPlanner.ParseColumns(MapScanPlanner.FormatColumns(MapScanPlanner.Ykb145().Columns), 9).SequenceEqual(MapScanPlanner.Ykb145().Columns));
});
Test("Map grid clicks skip / restore and retest / undo; unreachable marks only pending cells above", () =>
{
    var grid = new MapScanGrid(MapScanPlanner.Ykb145());
    var id = MapScanPlanner.PointId(650, .5);
    Check(id == "R0650_T0_50");
    Check(grid.Toggle(id) && Cell(grid, 650, .5).State == MapCellState.Skipped);
    Check(grid.Toggle(id) && Cell(grid, 650, .5).State == MapCellState.Pending);
    grid.SetRunning(650, .5);
    Check(!grid.Toggle(id), "a running cell ignores clicks");
    grid.Complete(650, .5, MapCellState.Pass, null, null);
    Check(grid.Toggle(id) && Cell(grid, 650, .5) is { State: MapCellState.Pending, Retest: true });
    Check(grid.NextPendingTorque(650) == .25);
    Check(grid.Toggle(id) && Cell(grid, 650, .5).State == MapCellState.Pass, "a second click restores the result");
    grid.Toggle(MapScanPlanner.PointId(650, 2));
    Check(grid.MarkPendingUnreachable(650, 1, "test") == 7, "1.25-3.00 minus the skipped 2.00");
    Check(Cell(grid, 650, 2).State == MapCellState.Skipped && Cell(grid, 650, 1).State == MapCellState.Pending);
    var json = JsonSerializer.Serialize(grid.ToState("M"), BenchConfig.Json);
    var back = MapScanGrid.FromState(JsonSerializer.Deserialize<MapScanState>(json, BenchConfig.Json)!);
    Check(back.Snapshot().Select(c => c.State).SequenceEqual(grid.Snapshot().Select(c => c.State)), "state JSON round trip");
});
async Task<(MapScanOrchestrator Runner, List<PointResult> Results, List<double> Speeds, List<double> TorqueAtDutStop)> StartMapScan(
    BenchRuntime runtime, MockRealtimeBridge bridge, MapScanGrid grid, Action<double>? commanded = null)
{
    var runner = new MapScanOrchestrator(runtime, config, new NullSessionRecorder());
    var results = new List<PointResult>(); var speeds = new List<double>(); var torqueAtStop = new List<double>();
    runner.PointCompleted += r => { lock (results) results.Add(r); };
    var run = runner.RunAsync(grid, CancellationToken.None,
        (rpm, _) => { lock (speeds) speeds.Add(rpm); commanded?.Invoke(rpm); bridge.SetSpeed(rpm); return Task.CompletedTask; },
        _ => { torqueAtStop.Add(Math.Abs(runtime.Latest.ActualTorqueNm)); commanded?.Invoke(0); bridge.SetSpeed(0); return Task.CompletedTask; });
    Check(await Task.WhenAny(run, Task.Delay(TimeSpan.FromSeconds(60))) == run, "the Map scan must end by itself");
    await run;
    return (runner, results, speeds, torqueAtStop);
}
AddAsync("Map scan on mock: every pending cell passes, the skipped cell is untouched, servo zero before DUT stop", async () =>
{
    var grid = new MapScanGrid(SmallMapPlan(new MapColumn(600, .15), new(1200, .1)));
    grid.Toggle(MapScanPlanner.PointId(600, .1));
    var bridge = new MockRealtimeBridge(config);
    await using var runtime = new BenchRuntime(bridge, config, new NullSessionRecorder());
    await runtime.StartAsync(CancellationToken.None);
    var (runner, results, speeds, torqueAtStop) = await StartMapScan(runtime, bridge, grid);
    Check(runner.EndReason == "COMPLETED", runner.EndReason);
    Check(grid.Count(MapCellState.Pass) == 4 && Cell(grid, 600, .1).State == MapCellState.Skipped);
    Check(results.Count(r => r.PointId.EndsWith("_NL")) == 2, "one no-load point per column");
    foreach (var cell in grid.Snapshot().Where(c => c.State == MapCellState.Pass))
        Check(cell.Result is { Result: "PASS" } r && Math.Abs(r.Torque!.Mean - cell.TorqueNm) < .02 && Math.Abs(r.Speed!.Mean - cell.Rpm) < 5, cell.Id);
    Check(speeds.Max() == 1200 && speeds[^1] < 1200, "DUT speed ramps up per column and down at the end");
    Check(torqueAtStop.Count == 1 && torqueAtStop[0] <= config.Limits.ZeroTorqueNm, $"DUT stop must follow the servo torque zero: {string.Join(",", torqueAtStop)}");
    Check(runtime.Latest.ServoOn, "a normal end keeps the servo enabled at zero torque");
});
AddAsync("Map scan marks the speed-drop point and every pending cell above it unreachable", async () =>
{
    var grid = new MapScanGrid(SmallMapPlan(new MapColumn(1000, .2)));
    var bridge = new MockRealtimeBridge(config);
    await using var runtime = new BenchRuntime(bridge, config, new NullSessionRecorder());
    await runtime.StartAsync(CancellationToken.None);
    double target = 0;
    using var plantStop = new CancellationTokenSource();
    // Speed loop at its current limit above 0.12 N·m: the shaft settles 10 % below the command.
    var plant = Task.Run(async () =>
    {
        while (!plantStop.IsCancellationRequested)
        {
            bridge.SetSpeed(Math.Abs(runtime.Latest.ActualTorqueNm) > .12 ? Volatile.Read(ref target) * .9 : Volatile.Read(ref target));
            await Task.Delay(5);
        }
    });
    var (runner, results, _, torqueAtStop) = await StartMapScan(runtime, bridge, grid, rpm => Volatile.Write(ref target, rpm));
    plantStop.Cancel(); await plant;
    Check(runner.EndReason == "COMPLETED", runner.EndReason);
    Check(Cell(grid, 1000, .05).State == MapCellState.Pass && Cell(grid, 1000, .1).State == MapCellState.Pass);
    Check(Cell(grid, 1000, .15) is { State: MapCellState.Unreachable, Reason: { } reason } && reason.Contains("跌落"), Cell(grid, 1000, .15).Reason ?? "");
    Check(Cell(grid, 1000, .2) is { State: MapCellState.Unreachable, Reason: { } above } && above.Contains("高于不可达点"));
    Check(results.Any(r => r.PointId == "R1000_T0_15" && r.Result == "UNREACHABLE"), "the unreachable window is kept as a diagnostic result");
    Check(torqueAtStop.Single() <= config.Limits.ZeroTorqueNm);
});
AddAsync("Map scan does not flag a speed sag during the torque ramp that recovers afterwards", async () =>
{
    var grid = new MapScanGrid(SmallMapPlan(new MapColumn(1000, .15)));
    var bridge = new MockRealtimeBridge(config);
    await using var runtime = new BenchRuntime(bridge, config, new NullSessionRecorder());
    await runtime.StartAsync(CancellationToken.None);
    double target = 0;
    using var plantStop = new CancellationTokenSource();
    // Soft speed loop (JD-OLD 2026-10-10): 6 % sag while the torque changes, linear recovery over 1 s after.
    var plant = Task.Run(async () =>
    {
        var lastTorque = 0.0; var lastChange = DateTimeOffset.MinValue;
        while (!plantStop.IsCancellationRequested)
        {
            var torque = Math.Abs(runtime.Latest.ActualTorqueNm);
            if (Math.Abs(torque - lastTorque) > .0005) lastChange = DateTimeOffset.UtcNow;
            lastTorque = torque;
            var sag = .06 * Math.Clamp(1 - (DateTimeOffset.UtcNow - lastChange).TotalSeconds, 0, 1);
            bridge.SetSpeed(Volatile.Read(ref target) * (1 - sag));
            await Task.Delay(5);
        }
    });
    var (runner, _, _, _) = await StartMapScan(runtime, bridge, grid, rpm => Volatile.Write(ref target, rpm));
    plantStop.Cancel(); await plant;
    Check(runner.EndReason == "COMPLETED" && grid.Count(MapCellState.Pass) == 3,
        string.Join(",", grid.Snapshot().Select(c => $"{c.Id}={c.State}:{c.Reason}")));
});
AddAsync("Map scan cools down above the pause temperature and resumes only at the resume temperature", async () =>
{
    var grid = new MapScanGrid(SmallMapPlan(new MapColumn(800, .1)) with { CoolingRpm = 300 });
    var bridge = new MockRealtimeBridge(config);
    await using var runtime = new BenchRuntime(bridge, config, new NullSessionRecorder());
    double dutTemp = 95;
    runtime.SnapshotOverlay = s => s with { DutMotorTempC = Volatile.Read(ref dutTemp) };
    await runtime.StartAsync(CancellationToken.None);
    double commandedRpm = 0;
    var scan = StartMapScan(runtime, bridge, grid, rpm => Volatile.Write(ref commandedRpm, rpm));
    await Wait(() => Volatile.Read(ref commandedRpm) == 300, 10);
    Check(grid.Count(MapCellState.Pass) == 0, "no point may be loaded above the pause temperature");
    Volatile.Write(ref dutTemp, 70);
    await Task.Delay(600);
    Check(grid.Count(MapCellState.Pass) == 0 && Volatile.Read(ref commandedRpm) == 300, "70 °C is above the resume temperature: keep cooling");
    Volatile.Write(ref dutTemp, 50);
    var (runner, _, _, _) = await scan;
    Check(runner.EndReason == "COMPLETED" && grid.Count(MapCellState.Pass) == 2, $"{runner.EndReason} pass={grid.Count(MapCellState.Pass)}");
});
AddAsync("Map DYN correction trims the servo command until DYN reads the target", async () =>
{
    var plan = SmallMapPlan(new MapColumn(900, .1)) with { StartTorqueNm = .1, TorqueStepNm = .1, TorqueReference = MapTorqueReference.DynClosedLoop, DynCorrectionMaxNm = .05 };
    var grid = new MapScanGrid(plan);
    var bridge = new MockRealtimeBridge(config);
    await using var runtime = new BenchRuntime(bridge, config, new NullSessionRecorder());
    runtime.SnapshotOverlay = s => s with { ExternalTorqueNm = s.ExternalTorqueNm * .7 };
    await runtime.StartAsync(CancellationToken.None);
    var (runner, _, _, _) = await StartMapScan(runtime, bridge, grid);
    var result = Cell(grid, 900, .1).Result!;
    Check(runner.EndReason == "COMPLETED" && result.Result == "PASS", runner.EndReason);
    Check(Math.Abs(result.Torque!.Mean - .1) < .02, $"DYN {result.Torque?.Mean:F3}");
    Check(result.ServoTargetTorque!.Mean > .12, $"servo command {result.ServoTargetTorque?.Mean:F3} must have been raised");
});
AddAsync("Map DYN closed loop starts from the no-load drag and flags a cell below it", async () =>
{
    // Bench-like DYN: gain 1.3 plus 0.08 N·m servo-side drag that is present even at zero servo torque.
    var grid = new MapScanGrid(SmallMapPlan(new MapColumn(1000, .15)));
    var bridge = new MockRealtimeBridge(config);
    await using var runtime = new BenchRuntime(bridge, config, new NullSessionRecorder());
    // Input current scaled too, so the mock's efficiency stays below 100 % with the extra DYN torque.
    runtime.SnapshotOverlay = s => s with { ExternalTorqueNm = s.ExternalTorqueNm is { } t ? Math.Abs(t) * 1.3 + .08 : null, DutCurrentA = s.DutCurrentA * 3 + .5 };
    await runtime.StartAsync(CancellationToken.None);
    var (runner, results, _, _) = await StartMapScan(runtime, bridge, grid);
    Check(runner.EndReason == "COMPLETED", runner.EndReason);
    Check(results.Single(r => r.PointId == "R1000_NL").Torque!.Mean is > .07 and < .09, "the no-load point measures the drag");
    Check(Cell(grid, 1000, .05) is { State: MapCellState.Pass, Result: { Result: "PASS_WITH_WARNING", Reason: { } reason } } && reason.Contains("拖曳"),
        Cell(grid, 1000, .05).Result?.Reason ?? "no result");
    foreach (var torque in new[] { .1, .15 })
    {
        var r = Cell(grid, 1000, torque).Result!;
        Check(r.Result == "PASS" && Math.Abs(r.Torque!.Mean - torque) < .02, $"{torque}: {r.Result} DYN {r.Torque?.Mean:F3}");
        Check(r.ServoTargetTorque!.Mean < torque - .03, $"{torque}: servo {r.ServoTargetTorque?.Mean:F3} must sit below the target");
    }
});
AddAsync("Map scan fault zeroes the servo torque before stopping the DUT, then disables the servo", async () =>
{
    var grid = new MapScanGrid(SmallMapPlan(new MapColumn(700, .15)));
    var bridge = new MockRealtimeBridge(config);
    await using var runtime = new BenchRuntime(bridge, config, new NullSessionRecorder());
    var fault = 0;
    runtime.SnapshotOverlay = s => Volatile.Read(ref fault) == 0 ? s : s with { DutFaultCode = 5 };
    await runtime.StartAsync(CancellationToken.None);
    var runner = new MapScanOrchestrator(runtime, config, new NullSessionRecorder());
    var torqueAtStop = new List<double>();
    var run = runner.RunAsync(grid, CancellationToken.None, (rpm, _) => { bridge.SetSpeed(rpm); return Task.CompletedTask; },
        _ => { torqueAtStop.Add(Math.Abs(runtime.Latest.ActualTorqueNm)); bridge.SetSpeed(0); return Task.CompletedTask; });
    await Wait(() => runner.CurrentPoint == "R0700_T0_10" && runner.Stage == MapScanStage.SAMPLE, 15);
    Volatile.Write(ref fault, 1);
    var tripped = false;
    try { await run; } catch (SafetyTripException) { tripped = true; }
    Check(tripped && runner.EndReason == "DUT_FAULT", runner.EndReason);
    Check(torqueAtStop.Count == 1 && torqueAtStop[0] <= config.Limits.ZeroTorqueNm, $"DUT stop at servo torque {string.Join(",", torqueAtStop)}");
    await Wait(() => !runtime.Latest.ServoOn, 3);
    Check(Cell(grid, 700, .1).State == MapCellState.Pending && Cell(grid, 700, .15).State == MapCellState.Pending, "the interrupted cell is measured again on continue");
});
AddAsync("Map scan starts from a fresh CST enable when the servo is already on", async () =>
{
    var grid = new MapScanGrid(SmallMapPlan(new MapColumn(800, .05)));
    var bridge = new MockRealtimeBridge(config);
    await using var runtime = new BenchRuntime(bridge, config, new NullSessionRecorder());
    await runtime.StartAsync(CancellationToken.None);
    runtime.Enable(); await Wait(() => runtime.Latest.ServoOn);
    var sawOff = false;
    using var watchStop = new CancellationTokenSource();
    var watcher = Task.Run(async () => { while (!watchStop.IsCancellationRequested) { if (!runtime.Latest.ServoOn) sawOff = true; await Task.Delay(2); } });
    var (runner, _, _, _) = await StartMapScan(runtime, bridge, grid);
    watchStop.Cancel(); await watcher;
    Check(sawOff, "an already enabled servo must be disabled before the CST zero-torque enable");
    Check(runner.EndReason == "COMPLETED" && grid.Count(MapCellState.Pass) == 1, runner.EndReason);
});
AddAsync("Map scan fault still disables the servo when the DUT stop cannot be sent", async () =>
{
    var grid = new MapScanGrid(SmallMapPlan(new MapColumn(700, .1)));
    var bridge = new MockRealtimeBridge(config);
    await using var runtime = new BenchRuntime(bridge, config, new NullSessionRecorder());
    var fault = 0;
    runtime.SnapshotOverlay = s => Volatile.Read(ref fault) == 0 ? s : s with { DutFaultCode = 5 };
    await runtime.StartAsync(CancellationToken.None);
    var runner = new MapScanOrchestrator(runtime, config, new NullSessionRecorder());
    var run = runner.RunAsync(grid, CancellationToken.None, (rpm, _) => { bridge.SetSpeed(rpm); return Task.CompletedTask; },
        _ => throw new IOException("VESC USB gone"));
    await Wait(() => runner.Stage == MapScanStage.SAMPLE, 15);
    Volatile.Write(ref fault, 1);
    Exception? error = null;
    try { await run; } catch (Exception ex) { error = ex; }
    Check(error is SafetyTripException && runner.EndReason == "DUT_FAULT", $"{runner.EndReason}: {error?.GetType().Name}");
    await Wait(() => !runtime.Latest.ServoOn, 3);
    Check(runner.StopDetail?.Contains("DUT 停机指令发送失败") == true, runner.StopDetail ?? "");
});
int failures=0;
foreach(var t in tests)
{
    try{await t.Run();Console.WriteLine("PASS "+t.Name);}
    catch(Exception ex){failures++;Console.WriteLine("FAIL "+t.Name+" : "+ex);}
}
Console.WriteLine($"RESULT {tests.Count-failures}/{tests.Count} passed");
return failures==0?0:1;

sealed class SafetyDutBridge : IRealtimeBridge, IDutMotorBridge
{
    private readonly object _gate = new();
    private readonly List<string> _events = [];
    private readonly List<(DateTimeOffset At, byte[] Frame)> _stopFrames = [];
    private BenchSnapshot _snapshot = new();
    private uint _heartbeat;
    private int _stopCalls;
    private int _servoStopWrites;
    private int _setCalls;
    private readonly List<double> _stopRamps = [];
    private readonly Dictionary<string, DateTimeOffset> _firstAt = [];
    // Loaded CST servo model: holds _servoTorque until _servoZeroDelay after the first stop command
    // (never, with _servoNeverZero); reports Servo OFF once a disable was requested at zero torque.
    private double _servoTorque;
    private TimeSpan _servoZeroDelay;
    private bool _servoNeverZero;
    private DateTimeOffset? _firstStopAt;
    private bool _disableRequested;
    private bool _torqueZeroLogged;

    public bool IsSimulation => true;
    public bool CanWrite => true;
    public bool CanControlDut => true;
    public IReadOnlyList<string> AvailableDutPorts => [];
    public bool Active { get; private set; } = true;
    public int StopCalls => Volatile.Read(ref _stopCalls);
    public int ServoStopWrites => Volatile.Read(ref _servoStopWrites);
    public int SetCalls => Volatile.Read(ref _setCalls);
    public List<string> Events { get { lock (_gate) return [.. _events]; } }
    public List<(DateTimeOffset At, byte[] Frame)> StopFrames { get { lock (_gate) return [.. _stopFrames]; } }
    public List<double> StopRamps { get { lock (_gate) return [.. _stopRamps]; } }
    public bool DisableRequested { get { lock (_gate) return _disableRequested; } }
    public DateTimeOffset? FirstAt(string name) { lock (_gate) return _firstAt.TryGetValue(name, out var at) ? at : null; }
    // Callers hold _gate.
    private void Record(string name) { _events.Add(name); _firstAt.TryAdd(name, DateTimeOffset.UtcNow); }

    public void SetSnapshot(BenchSnapshot snapshot)
    {
        lock (_gate) _snapshot = snapshot;
    }

    public void LoadServo(double torqueNm, int zeroDelayMs, bool neverZero)
    {
        lock (_gate) { _servoTorque = torqueNm; _servoZeroDelay = TimeSpan.FromMilliseconds(zeroDelayMs); _servoNeverZero = neverZero; }
    }

    public Task ConnectAsync(CancellationToken ct) => Task.CompletedTask;
    public Task DisconnectAsync() => Task.CompletedTask;
    public Task ConnectDutAsync(string portName, CancellationToken ct) => Task.CompletedTask;
    public Task DisconnectDutAsync() => Task.CompletedTask;

    public Task<BenchSnapshot> ReadSnapshotAsync(CancellationToken ct)
    {
        BenchSnapshot snapshot;
        var now = DateTimeOffset.UtcNow;
        lock (_gate)
        {
            snapshot = _snapshot;
            if (_servoTorque > 0)
            {
                var zero = !_servoNeverZero && _firstStopAt is { } at && now - at >= _servoZeroDelay;
                if (zero && !_torqueZeroLogged) { _torqueZeroLogged = true; Record("servo-torque-zero"); }
                var torque = zero ? 0 : _servoTorque;
                snapshot = snapshot with { TargetTorqueNm = torque, ActualTorqueNm = torque, ServoOn = !(zero && _disableRequested) };
            }
        }
        return Task.FromResult(snapshot with
        {
            Connected = true,
            EtherCatOnline = true,
            DriveReady = true,
            State = BenchState.DriveReady,
            Timestamp = now,
            PlcHeartbeat = ++_heartbeat
        });
    }

    public Task WriteCommandAsync(BenchCommand command, CancellationToken ct)
    {
        if (command.StopRequest || command.DisableRequest)
        {
            Interlocked.Increment(ref _servoStopWrites);
            lock (_gate)
            {
                Record("servo-stop");
                _firstStopAt ??= DateTimeOffset.UtcNow;
                if (_stopRamps.Count == 0 || _stopRamps[^1] != command.TorqueRampNmPerSec) _stopRamps.Add(command.TorqueRampNmPerSec);
                if (command.DisableRequest) _disableRequested = true;
            }
        }
        return Task.CompletedTask;
    }

    public Task SetDutAsync(DutControlMode mode, double value, CancellationToken ct)
    {
        Interlocked.Increment(ref _setCalls);
        Active = true;
        lock (_gate) Record("dut-set");
        return Task.CompletedTask;
    }

    public async Task StopDutAsync(CancellationToken ct)
    {
        Interlocked.Increment(ref _stopCalls);
        Active = false;
        lock (_gate) Record("dut-stop-start");
        await VescEmergencyStop.SendAsync((frame, _) =>
        {
            lock (_gate)
            {
                _stopFrames.Add((DateTimeOffset.UtcNow, [.. frame]));
                Record("dut-stop-frame");
            }
            return Task.CompletedTask;
        }, ct);
        lock (_gate) Record("dut-stop-complete");
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
