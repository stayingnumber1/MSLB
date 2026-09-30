using System.Diagnostics;
using System.Buffers.Binary;
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
async Task Wait(Func<bool> condition, double seconds=3)
{
    var w=Stopwatch.StartNew();
    while(!condition()){if(w.Elapsed.TotalSeconds>seconds)throw new Exception("wait timeout");await Task.Delay(10);}
}
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
int failures=0;
foreach(var t in tests)
{
    try{await t.Run();Console.WriteLine("PASS "+t.Name);}
    catch(Exception ex){failures++;Console.WriteLine("FAIL "+t.Name+" : "+ex);}
}
Console.WriteLine($"RESULT {tests.Count-failures}/{tests.Count} passed");
return failures==0?0:1;
