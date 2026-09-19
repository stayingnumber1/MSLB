using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using MotorLoadBench.Application;
using MotorLoadBench.Domain;
using MotorLoadBench.Infrastructure;

var tests = new List<(string Name, Func<Task> Run)>();
void Test(string name, Action action) => tests.Add((name, () => { action(); return Task.CompletedTask; }));
void Check(bool ok, string message = "assertion failed") { if (!ok) throw new Exception(message); }
void Throws(Action action) { try { action(); } catch { return; } throw new Exception("expected rejection"); }
var config = new BenchConfig();
Test("invalid config / NaN fail closed", () => Throws(() => (config with { Limits = config.Limits with { MaxTorqueNm = double.NaN } }).Validate()));
Test("unverified hardware cannot write", () => Throws(config.ValidateHardware));
Test("direction coefficients validated", () => Throws(() => (config with { Drive = config.Drive with { InstallationSign = 0 } }).Validate()));
Test("ordered torque map required", () => Throws(() => BenchConfig.ValidateMap([new(100, 1), new(0, 1)])));
Test("constant power standstill is zero", () => Check(LoadMath.Demand(new(Mode: LoadMode.ConstantPower, TargetPowerW: 100), config, 0, 25) == 0));
Test("constant power low speed no division explosion", () => Check(LoadMath.Demand(new(Mode: LoadMode.ConstantPower, TargetPowerW: 100), config, .001, 25) == 0));
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
tests.Add(("full mock recipe + CSV + JSON + stop",async () =>
{
    var root=Path.Combine(Environment.CurrentDirectory,"artifacts","test-sessions");
    var bridge=new MockRealtimeBridge(config);
    var recorder=new SessionRecorder(root,config,true,"test");
    await using(var runtime=new BenchRuntime(bridge,config,recorder))
    {
        await runtime.StartAsync(CancellationToken.None);
        await Wait(()=>runtime.Latest.Connected && runtime.Latest.DriveReady);
        runtime.Enable(); await Wait(()=>runtime.Latest.ServoOn);
        var recipe=new Recipe{Points=[new(){Id="integration",Rpm=300,TorqueNm=.05,StableSeconds=.05,SampleSeconds=.15,MaxDurationSeconds=5}]};
        var runner=new TestOrchestrator(runtime,config,recorder);
        await runner.RunAsync(recipe,CancellationToken.None);
        await runtime.StopAsync(true,CancellationToken.None);
        Check(!runtime.Latest.ServoOn);
        Check(Directory.GetFiles(recorder.DirectoryPath,"point_*.json").Length==1);
        var result=JsonSerializer.Deserialize<PointResult>(File.ReadAllText(Directory.GetFiles(recorder.DirectoryPath,"point_*.json")[0]),BenchConfig.Json)!;
        Check(result.Result=="PASS" && result.Torque!.Count>=2);
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
Test("falling to standstill removes target without ramp delay", () =>
{
    var m = new RealtimeModel(config);
    for (uint i = 1; i <= 150; i++) { m.Receive(new(EnableRequest: i == 1, TargetTorqueNm: .1, Heartbeat: i)); m.Tick(.01,300,m.Target); }
    Check(m.Target < -.09);
    var s = m.Tick(.001,0,m.Target);
    Check(s.TargetTorqueNm == 0, "residual ramp torque must not drive the stopped shaft");
});
int failures=0;
foreach(var t in tests)
{
    try{await t.Run();Console.WriteLine("PASS "+t.Name);}
    catch(Exception ex){failures++;Console.WriteLine("FAIL "+t.Name+" : "+ex);}
}
Console.WriteLine($"RESULT {tests.Count-failures}/{tests.Count} passed");
return failures==0?0:1;
