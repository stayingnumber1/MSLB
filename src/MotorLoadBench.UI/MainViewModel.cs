using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Input;
using Microsoft.Win32;
using MotorLoadBench.Application;
using MotorLoadBench.Domain;
using MotorLoadBench.Infrastructure;

namespace MotorLoadBench.UI;

public sealed class MainViewModel : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;
    private BenchRuntime? _runtime;
    private MockRealtimeBridge? _mock;
    private SessionRecorder? _recorder;
    private TestOrchestrator? _orchestrator;
    private CancellationTokenSource? _recipeCts;
    private Task? _recipeTask;
    private BenchConfig _config = new();
    private string? _configError;
    private readonly string _root = File.Exists(Path.Combine(Environment.CurrentDirectory, "config/bench.json")) ? Environment.CurrentDirectory : AppContext.BaseDirectory;
    private bool _busy;
    public BenchSnapshot? Snapshot => _runtime?.Latest;
    public ObservableCollection<string> Events { get; } = [];
    public string[] ConnectionModes { get; } = ["仿真 / 无硬件", "TwinCAT ADS / 默认只读"];
    public string[] LoadModes { get; } = ["恒转矩", "恒功率", "转速-转矩表"];
    public string[] TrendWindows { get; } = ["最近 10 秒", "最近 60 秒", "全程（抽稀）"];
    public int ConnectionMode { get; set; }
    public int LoadModeIndex { get; set; }
    public int TrendWindowIndex { get; set; } = 1;
    public bool GuardConfirmed { get; set; }
    public bool DirectionConfirmed { get; set; }
    public string TorqueText { get; set; } = "0.10";
    public string RampText { get; set; } = "0.10";
    public string PowerText { get; set; } = "3";
    public string SimRpmText { get; set; } = "300";
    public string RecipeJson { get; set; } = "";
    public string Phase => _orchestrator?.Phase ?? "待机 · 未启动测试";
    public string ConnectionText => Snapshot?.Connected == true ? (_runtime!.IsSimulation ? "SIMULATION · 已连接" : "ADS · 已连接") : "DISCONNECTED";
    public string ModeBanner => _runtime?.IsSimulation != false ? "仿真模式：全部数值由模拟模型产生，不代表实机测试结果。" : "ADS 实机模式：" + (_runtime.CanWrite ? "配置核验通过，仍需现场联锁确认。" : "只读观测，所有加载写入被禁止。");
    public string LimitsText => $"当前上限 {_config.Limits.MaxTorqueNm:F2} N·m / {_config.Limits.MaxSpeedRpm:F0} rpm / {_config.Limits.MaxPowerW:F0} W";
    public string RpmDisplay => Snapshot?.SpeedRpm.ToString("F1") ?? "—";
    public string TorqueDisplay => Snapshot is { } s ? $"{s.TargetTorqueNm:F3} / {s.ActualTorqueNm:F3}" : "—";
    public string PowerDisplay => Snapshot?.MechanicalPowerW.ToString("F2") ?? "—";
    public string BusDisplay => Snapshot?.DcBusV?.ToString("F1") ?? "不可用";
    public string TemperatureDisplay => Snapshot is { } s ? $"{s.MotorTempC?.ToString("F1") ?? "—"} / {s.BrakeTempC?.ToString("F1") ?? "—"}" : "—";
    public string EfficiencyDisplay => Snapshot is { } s ? $"{s.ExternalTorqueNm?.ToString("F3") ?? "—"} Nm / {s.EfficiencyPct?.ToString("F1") ?? "—"} %" : "—";
    public string StateText => Snapshot is { Connected: true } s ? $"{s.State} | Servo {(s.ServoOn ? "ON" : "OFF")} | Interlock 0x{(uint)s.Interlocks:X} | Error 0x{s.ErrorCode:X}" : "离线 / Servo状态未知，不能据此确认停机";
    public string SessionText => _recorder?.DirectoryPath ?? "连接后创建独立记录会话；每个会话保留配置与原始数据";
    public string ConfigurationInfo => _configError ?? $"配置：{Path.Combine(_root, "config/bench.json")}\n驱动：{_config.Drive.Name}\nADS：{_config.Ads.AmsNetId}:{_config.Ads.Port}\n实机写入：{_config.Drive.AllowHardwareWrites}\nPDO缩放：{_config.Drive.RawTorquePerNm?.ToString() ?? "TODO_VERIFY"} / { _config.Drive.RpmPerRawVelocity?.ToString() ?? "TODO_VERIFY"}\n型号码核实：{_config.Drive.ModelVerified}  方向核实：{_config.Drive.DirectionVerified}\n回生核实：{_config.Drive.RegenerationVerified}  硬件安全：{_config.Drive.HardwareSafetyVerified}";
    public ICommand EtherCatAdaptersCommand { get; }
    public ICommand EtherCatScanCommand { get; }
    public ICommand ConnectCommand { get; }
    public ICommand DisconnectCommand { get; }
    public ICommand EnableCommand { get; }
    public ICommand DisableCommand { get; }
    public ICommand ResetCommand { get; }
    public ICommand LoadCommand { get; }
    public ICommand StopCommand { get; }
    public ICommand InjectCommand { get; }
    public ICommand OpenRecipeCommand { get; }
    public ICommand SaveRecipeCommand { get; }
    public ICommand ValidateRecipeCommand { get; }
    public ICommand RunRecipeCommand { get; }
    public ICommand ExportCommand { get; }
    public MainViewModel()
    {
        try { ReadConfig(); RecipeJson = File.ReadAllText(Path.Combine(_root, "config/test-recipes/demo.json")); }
        catch (Exception ex) { _configError = ex.Message; AddEvent("配置错误：" + ex.Message); }
        EtherCatAdaptersCommand = Command(() => ProbeEtherCatAsync("adapters"));
        EtherCatScanCommand = Command(() => ProbeEtherCatAsync("scan"));
        ConnectCommand = Command(ConnectAsync);
        DisconnectCommand = Command(DisconnectAsync);
        EnableCommand = Command(async () =>
        {
            var r = Runtime();
            if (!GuardConfirmed || !DirectionConfirmed) throw new InvalidOperationException("先确认两项联锁检查");
            if (Confirm("确认当前连接、零转矩、机械护罩及硬件急停，申请 Servo ON？")) { r.Enable(); await Task.Delay(100); }
        });
        DisableCommand = Command(async () => { await CancelRecipeAsync(); await Runtime().StopAsync(true, CancellationToken.None); });
        ResetCommand = Command(() => { if (Confirm("确认故障原因已消除，复位后不恢复加载？")) Runtime().ResetFault(); return Task.CompletedTask; });
        LoadCommand = Command(() =>
        {
            if (_orchestrator?.Running == true) throw new InvalidOperationException("自动配方运行中禁止手动加载");
            if (_mock != null) _mock.SetSpeed(Number(SimRpmText));
            if (Confirm("确认按当前限值开始加载？")) Runtime().Load(Number(TorqueText), Number(RampText), (LoadMode)LoadModeIndex, Number(PowerText));
            return Task.CompletedTask;
        });
        StopCommand = Command(async () => { Runtime().RequestStop(); await CancelRecipeAsync(); await Runtime().StopAsync(false, CancellationToken.None); });
        InjectCommand = new AsyncCommand(async p =>
        {
            try
            {
                if (_mock == null) throw new InvalidOperationException("仅仿真模式可注入");
                var value = Enum.Parse<Interlock>(p?.ToString() ?? "None");
                _mock.Inject(value); AddEvent("仿真注入：" + value);
                await Task.CompletedTask;
            }
            catch (Exception ex) { ShowError(ex); }
        });
        OpenRecipeCommand = Command(async () =>
        {
            if (_orchestrator?.Running == true) throw new InvalidOperationException("先停止当前配方");
            var dlg = new OpenFileDialog { Filter = "测试配方 (*.json)|*.json" };
            if (dlg.ShowDialog() == true) { RecipeJson = await File.ReadAllTextAsync(dlg.FileName); PropertyChanged?.Invoke(this, new(nameof(RecipeJson))); Refresh(); }
        });
        SaveRecipeCommand = Command(async () =>
        {
            var recipe = ParseRecipe();
            var dlg = new SaveFileDialog { Filter = "测试配方 (*.json)|*.json", FileName = "recipe.json" };
            if (dlg.ShowDialog() == true) await File.WriteAllTextAsync(dlg.FileName, JsonSerializer.Serialize(recipe, BenchConfig.Json));
        });
        ValidateRecipeCommand = Command(() => { var r = ParseRecipe(); AddEvent($"配方有效：{r.Points.Count} 点"); return Task.CompletedTask; });
        RunRecipeCommand = Command(async () =>
        {
            var runtime = Runtime(); var recipe = ParseRecipe();
            if (_recipeTask is { IsCompleted: false }) throw new InvalidOperationException("配方正在运行");
            if (!runtime.Latest.ServoOn) throw new InvalidOperationException("先Servo ON");
            if (!Confirm("确认开始自动配方？实机模式仅等待 DUT 转速，不控制 DUT。")) return;
            _recipeCts?.Dispose(); _recipeCts = new();
            await File.WriteAllTextAsync(Path.Combine(_recorder!.DirectoryPath, "recipe_" + Guid.NewGuid().ToString("N") + ".json"), JsonSerializer.Serialize(recipe, BenchConfig.Json));
            _orchestrator = new(runtime, _config, _recorder);
            _recipeTask = _orchestrator.RunAsync(recipe, _recipeCts.Token, _mock == null ? null : p => _mock.SetSpeed(p.Rpm));
            try { await _recipeTask; AddEvent("配方完成"); }
            finally { runtime.RequestStop(); }
        });
        ExportCommand = Command(ExportAsync);
    }
    private static double Number(string s)
    {
        if (!double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) || !double.IsFinite(v))
            throw new ArgumentException("请输入有效数字（小数点使用 .）");
        return v;
    }
    private void ReadConfig()
    {
        _config = JsonSerializer.Deserialize<BenchConfig>(File.ReadAllText(Path.Combine(_root, "config/bench.json")), BenchConfig.Json)
            ?? throw new InvalidDataException("配置为空");
        _config.Validate(); _configError = null;
    }
    private Recipe ParseRecipe()
    {
        var r = JsonSerializer.Deserialize<Recipe>(RecipeJson, BenchConfig.Json) ?? throw new InvalidDataException("配方为空");
        BenchConfig.ValidateRecipe(r, _config.Limits); return r;
    }
    private BenchRuntime Runtime() => _runtime ?? throw new InvalidOperationException("请先连接");
    private static bool Confirm(string text) => MessageBox.Show(text, "操作确认", MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes;
    private ICommand Command(Func<Task> action) => new AsyncCommand(async _ =>
    {
        try { await action(); }
        catch (OperationCanceledException) { AddEvent("操作已取消，已请求回零"); }
        catch (Exception ex) { ShowError(ex); }
        finally { Refresh(); }
    });
    private void ShowError(Exception ex)
    {
        AddEvent(ex.Message);
        try { _recorder?.Event("ui_error", ex.ToString()); } catch (Exception) { _runtime?.RequestStop(true); }
        MessageBox.Show(ex.Message, "Motor Load Bench", MessageBoxButton.OK, MessageBoxImage.Warning);
    }
    private void AddEvent(string message)
    {
        Events.Insert(0, $"{DateTime.Now:HH:mm:ss.fff}  {message}");
        while (Events.Count > 200) Events.RemoveAt(Events.Count - 1);
    }
    private async Task ProbeEtherCatAsync(string action)
    {
        if (_busy || _runtime != null) throw new InvalidOperationException("请先断开当前会话，再进行 EtherCAT 诊断");
        _busy = true;
        try
        {
            var python = Path.Combine(_root, ".tools/ethercat-python/Scripts/python.exe");
            if (!File.Exists(python)) throw new FileNotFoundException("缺少 EtherCAT 环境，请按 docs/ETHERCAT_DIRECT.md 安装", python);
            var info = new System.Diagnostics.ProcessStartInfo(python)
            {
                WorkingDirectory = _root, UseShellExecute = false, CreateNoWindow = true,
                RedirectStandardOutput = true, RedirectStandardError = true,
                StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8
            };
            info.Environment["PYTHONIOENCODING"] = "utf-8";
            info.ArgumentList.Add("tools/ethercat_probe.py");
            info.ArgumentList.Add(action);
            info.ArgumentList.Add("--output");
            info.ArgumentList.Add($"artifacts/ethercat/{action}.json");
            using var process = System.Diagnostics.Process.Start(info) ?? throw new IOException("无法启动 EtherCAT 工具");
            var stdout = process.StandardOutput.ReadToEndAsync();
            var stderr = process.StandardError.ReadToEndAsync();
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
            try { await process.WaitForExitAsync(timeout.Token); }
            catch (OperationCanceledException)
            {
                try { process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
                await process.WaitForExitAsync();
                throw new TimeoutException("EtherCAT 诊断超时，进程已终止；未建立加载会话");
            }
            var output = await stdout;
            var error = await stderr;
            AddEvent("EtherCAT 诊断报告：" + Path.Combine(_root, $"artifacts/ethercat/{action}.json"));
            if (process.ExitCode != 0) throw new IOException(output + error);
            AddEvent(output);
            AddEvent("诊断完成；尚未建立周期转矩控制连接，Servo ON 和加载不可用");
        }
        finally { _busy = false; }
    }

    private async Task ConnectAsync()
    {
        if (_busy || _runtime != null) throw new InvalidOperationException("正在连接或已经连接，请先断开");
        _busy = true;
        try
        {
            ReadConfig();
            var text = File.ReadAllText(Path.Combine(_root, "config/bench.json"));
            var hash = SessionRecorder.Hash(text);
            IRealtimeBridge bridge;
            if (ConnectionMode == 0) { _mock = new(_config); bridge = _mock; }
            else { _mock = null; bridge = new AdsRealtimeBridge(_config, Convert.ToUInt32(hash[..8], 16)); }
            var recorder = new SessionRecorder(Path.GetFullPath(_config.DataDirectory, _root), _config, bridge.IsSimulation, hash);
            var runtime = new BenchRuntime(bridge, _config, recorder);
            try { await runtime.StartAsync(CancellationToken.None); }
            catch { await runtime.DisposeAsync(); throw; }
            _recorder = recorder; _runtime = runtime;
            AddEvent(ConnectionMode == 0 ? "仿真连接成功" : "ADS连接成功");
        }
        finally { _busy = false; }
    }
    private async Task CancelRecipeAsync()
    {
        if (_recipeCts != null) await _recipeCts.CancelAsync();
        if (_recipeTask != null) try { await _recipeTask; } catch (Exception) { }
    }
    private async Task DisconnectAsync()
    {
        if (_busy) throw new InvalidOperationException("连接操作尚未完成");
        if (_runtime == null) return;
        await CancelRecipeAsync();
        if (_runtime.CanWrite && _runtime.Latest.Connected) await _runtime.StopAsync(true, CancellationToken.None);
        else if (!_runtime.Latest.Connected && !_runtime.IsSimulation && !Confirm("通信断开，软件无法确认停机。请先现场确认硬件已安全停机，是否关闭连接？")) return;
        await _runtime.DisposeAsync(); _runtime = null; _mock = null;
        AddEvent("已断开，数据已flush"); Refresh();
    }
    public async Task<bool> CloseAsync()
    {
        try { await DisconnectAsync(); return _runtime == null; }
        catch (Exception ex)
        {
            AddEvent(ex.Message);
            if (!Confirm(ex.Message + "\n请确认硬件安全停机。仍退出软件？")) return false;
            if (_runtime != null) await _runtime.DisposeAsync();
            return true;
        }
    }
    private async Task ExportAsync()
    {
        if (_recorder == null) throw new InvalidOperationException("没有可导出的会话");
        var results = new List<PointResult>();
        foreach (var p in Directory.GetFiles(_recorder.DirectoryPath, "point_*.json"))
        {
            var r = JsonSerializer.Deserialize<PointResult>(await File.ReadAllTextAsync(p), BenchConfig.Json);
            if (r != null) results.Add(r);
        }
        var summary = new { generatedUtc = DateTimeOffset.UtcNow, simulation = _runtime?.IsSimulation ?? ConnectionMode == 0, results };
        await File.WriteAllTextAsync(Path.Combine(_recorder.DirectoryPath, "summary.json"), JsonSerializer.Serialize(summary, BenchConfig.Json));
        var csv = new StringBuilder("point_id,attempt,result,rpm_mean,torque_mean_nm,power_mean_w,efficiency_pct,torque_source,raw_path\n");
        foreach (var r in results)
        {
            static string N(double? v) => v?.ToString("G17", CultureInfo.InvariantCulture) ?? "";
            static string Q(string s) => "\"" + s.Replace("\"", "\"\"") + "\"";
            csv.AppendLine(string.Join(',', Q(r.PointId), r.Attempt, r.Result, N(r.Speed?.Mean), N(r.Torque?.Mean), N(r.MechanicalPower?.Mean), N(r.Efficiency?.Mean), r.Source, Q(r.RawDataPath)));
        }
        await File.WriteAllTextAsync(Path.Combine(_recorder.DirectoryPath, "map_results.csv"), csv.ToString(), new UTF8Encoding(true));
        AddEvent($"已导出 {results.Count} 个结果：summary.json / map_results.csv");
    }
    public async Task SmokeAsync()
    {
        ConnectionMode = 0;
        await ConnectAsync();
        var watch = System.Diagnostics.Stopwatch.StartNew();
        while (Snapshot?.DriveReady != true)
        {
            if (watch.Elapsed.TotalSeconds > 4) throw new TimeoutException("UI smoke connection");
            await Task.Delay(20);
        }
        Runtime().Enable();
        await Task.Delay(150);
        Runtime().Load(.1, .1);
        await Task.Delay(1400);
        Refresh();
        if (Snapshot is not { ServoOn: true } || Math.Abs(Snapshot.ActualTorqueNm) < .08)
            throw new InvalidOperationException("UI simulation did not reach load");
    }
    public void Refresh()
    {
        if (_runtime != null) while (_runtime.Alarms.TryDequeue(out var alarm)) AddEvent($"{alarm.Code}: {alarm.Message}");
        foreach (var p in new[] { nameof(ConnectionText), nameof(ModeBanner), nameof(LimitsText), nameof(RpmDisplay), nameof(TorqueDisplay),
            nameof(PowerDisplay), nameof(BusDisplay), nameof(TemperatureDisplay), nameof(EfficiencyDisplay), nameof(StateText), nameof(SessionText),
            nameof(Phase), nameof(ConfigurationInfo) }) PropertyChanged?.Invoke(this, new(p));
    }
}
public sealed class AsyncCommand(Func<object?, Task> execute) : ICommand
{
    private bool _running;
    public bool CanExecute(object? parameter) => !_running;
    public event EventHandler? CanExecuteChanged;
    public async void Execute(object? parameter)
    {
        if (_running) return;
        _running = true; CanExecuteChanged?.Invoke(this, EventArgs.Empty);
        try { await execute(parameter); }
        finally { _running = false; CanExecuteChanged?.Invoke(this, EventArgs.Empty); }
    }
}
