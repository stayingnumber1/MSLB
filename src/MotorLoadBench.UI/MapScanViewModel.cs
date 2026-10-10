using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Windows.Input;
using Microsoft.Win32;
using MotorLoadBench.Application;
using MotorLoadBench.Domain;
using MotorLoadBench.Infrastructure;

namespace MotorLoadBench.UI;

// "Map 扫描" tab: speed x torque fixed-point scan. Shares the runtimes, recorder, precheck and event
// log of MainViewModel; the scan itself is MapScanOrchestrator.
public sealed class MapScanViewModel : INotifyPropertyChanged
{
    private readonly MainViewModel _host;
    private MapScanOrchestrator? _orchestrator;
    private CancellationTokenSource? _cts;
    private Task? _task;
    private string? _archiveDirectory;
    private string _fileTag = "";
    private int _renderedVersion = -1;
    private bool _interactive = true;
    private PointResult? _lastResult;
    private readonly List<PointResult> _noLoadResults = [];
    private readonly SemaphoreSlim _saveGate = new(1, 1);
    private MapLoadMode _loadMode = MapLoadMode.Staircase;
    private MapColumnOrder _order = MapColumnOrder.Interleaved;
    private MapTorqueReference _reference = MapTorqueReference.DynClosedLoop;

    public event PropertyChangedEventHandler? PropertyChanged;
    public event Action? GridChanged;
    public event Action<string>? ExportVisualRequested;

    public MapScanGrid? Grid { get; private set; }
    public ObservableCollection<string> RecentPoints { get; } = [];

    public string ColumnsText { get; set; } = MapScanPlanner.FormatColumns(MapScanPlanner.Ykb145().Columns);
    public string DefaultCapText { get; set; } = "3.00";
    public string StartTorqueText { get; set; } = "0.25";
    public string StepTorqueText { get; set; } = "0.25";
    public string SampleSecondsText { get; set; } = "2.0";
    public string TorqueRampText { get; set; } = "0.3";
    public string SpeedRampText { get; set; } = "500";
    public string StableTimeoutText { get; set; } = "10";
    public string CoolingRpmText { get; set; } = "800";
    public string PauseTempText { get; set; } = "90";
    public string ResumeTempText { get; set; } = "60";
    public string SpeedDropPctText { get; set; } = "3";
    public string DropConfirmText { get; set; } = "2";
    public string DutyLimitText { get; set; } = "93";
    public bool IsStaircase { get => _loadMode == MapLoadMode.Staircase; set { if (value) SetLoadMode(MapLoadMode.Staircase); } }
    public bool IsReturnToZero { get => _loadMode == MapLoadMode.ReturnToZero; set { if (value) SetLoadMode(MapLoadMode.ReturnToZero); } }
    public bool IsAscending { get => _order == MapColumnOrder.Ascending; set { if (value) SetOrder(MapColumnOrder.Ascending); } }
    public bool IsInterleaved { get => _order == MapColumnOrder.Interleaved; set { if (value) SetOrder(MapColumnOrder.Interleaved); } }
    public bool IsServoReference { get => _reference == MapTorqueReference.ServoCommand; set { if (value) SetReference(MapTorqueReference.ServoCommand); } }
    public bool IsDynClosedLoop { get => _reference == MapTorqueReference.DynClosedLoop; set { if (value) SetReference(MapTorqueReference.DynClosedLoop); } }
    public string MotorModel { get => _host.MotorModel; set { _host.MotorModel = value; Raise(nameof(MotorModel)); } }

    public bool IsRunning => _task is { IsCompleted: false };
    public bool SettingsEditable => !IsRunning;
    public string StatusText => IsRunning
        ? _orchestrator?.Paused == true ? "已暂停" : _orchestrator?.Stage == MapScanStage.COOLING ? "冷却中" : "运行中"
        : _orchestrator?.Stage switch { MapScanStage.FAULT => "故障", MapScanStage.FINISHED => "结束", _ => "待机" };
    public string StatusColor => IsRunning
        ? _orchestrator?.Paused == true || _orchestrator?.Stage == MapScanStage.COOLING ? "#F4C542" : "#24D18F"
        : _orchestrator?.Stage == MapScanStage.FAULT ? "#F04B36" : "#F3C884";
    public string ProgressText
    {
        get
        {
            if (Grid is not { } g) return "未生成网格：填写转速列表后点「生成网格」";
            var pending = g.Count(MapCellState.Pending) + g.Count(MapCellState.Running);
            var text = $"{g.Plan.Name} · {g.Total - pending - g.Count(MapCellState.Skipped)}/{g.Total} 点 · 通过 {g.Count(MapCellState.Pass)} · 不可达 {g.Count(MapCellState.Unreachable)} · " +
                $"失败 {g.Count(MapCellState.Failed)} · 跳过 {g.Count(MapCellState.Skipped)} · 待测 {pending}";
            if (IsRunning && pending > 0)
                text += $" · 剩余约 {Math.Max(1, Math.Ceiling(pending * (_orchestrator?.AveragePointSeconds ?? 6) / 60)):F0} min";
            else if (_orchestrator is { Running: false, EndReason: not "NOT_STARTED" } o) text += $" · 上次结束 {o.EndReason}";
            return text;
        }
    }
    public string StageText => _orchestrator?.Phase ?? "IDLE";
    public string CurrentPointText => _orchestrator?.CurrentPoint ?? "—";
    public string SpeedText => $"{_orchestrator?.TargetRpm?.ToString("F0", CultureInfo.InvariantCulture) ?? "—"} / {Abs(_host.Snapshot?.DutSpeedRpm, "F0")}";
    public string TorqueText => $"{_orchestrator?.CellTorqueNm?.ToString("F2", CultureInfo.InvariantCulture) ?? "—"} · {_orchestrator?.TargetTorqueNm?.ToString("F2", CultureInfo.InvariantCulture) ?? "—"} / {Abs(_host.Snapshot?.ExternalTorqueNm, "F3")}";
    public string IqDutyText => $"{Abs(_host.Snapshot?.DutPhaseCurrentA, "F1")} A · {Abs(_host.Snapshot?.DutDutyCyclePct, "F0")}%";
    public string PointEfficiencyText => _lastResult is { } r
        ? $"{r.Efficiency?.Mean.ToString("F1", CultureInfo.InvariantCulture) ?? "—"} %  {r.PointId}" : "—";
    public string TemperatureText => (_host.Snapshot?.DutMotorTempC is not { } t ? "DUT 电机温度：无数据，温控门控不可用"
        : MapScanOrchestrator.ValidTemperature(t) is null ? $"DUT 电机温度读数无效（{t:F1} °C），温控门控不可用"
        : $"DUT 电机温度 {t:F1} °C") + $" · MOSFET {Abs(_host.Snapshot?.DutControllerTempC, "F1")} °C";
    public double TemperatureMax => _host.Config.Limits.DutMotorTripC;
    public double TemperatureValue => Math.Clamp(MapScanOrchestrator.ValidTemperature(_host.Snapshot?.DutMotorTempC) ?? 0, 0, TemperatureMax);
    public string TemperatureColor => MapScanOrchestrator.ValidTemperature(_host.Snapshot?.DutMotorTempC) is not { } t ? "#60798A"
        : t >= Parse(PauseTempText, 90) ? "#F04B36" : t > Parse(ResumeTempText, 60) ? "#F4C542" : "#24D18F";
    public string GateText => $"恢复 ≤ {Parse(ResumeTempText, 60):F0} · 暂停 ≥ {Parse(PauseTempText, 90):F0} · 硬跳闸 {TemperatureMax:F0} °C";
    public string ProtectionText
    {
        get
        {
            var s = _host.Snapshot;
            var vesc = _host.SimulationBridge != null ? "仿真" : _host.DutRuntime?.CanControlDut == true ? "VESC 在线" : "VESC 断开";
            var dyn = s?.ExternalHealthy == true ? "DYN 在线" : "DYN 无数据";
            var servo = s is { Connected: true } ? s.ServoOn ? "伺服 ON" : "伺服 OFF" : "伺服未连接";
            return $"{vesc} · {dyn} · {servo}\n遥测超时 / Fault / 转速 < 50% 目标 → 伺服扭矩先回零，再停 DUT；斜坡后转速跌落且不回升、或 Duty 饱和 → 该点及以上记不可达";
        }
    }
    public string ExportTitle => TitleFor(_host.MotorModel, Grid?.Plan.Name, DateTimeOffset.Now);
    // The preset name already carries the motor ("YKB-145 定点 Map"): do not repeat it.
    public static string TitleFor(string? motorModel, string? planName, DateTimeOffset when)
    {
        var model = motorModel?.Trim() ?? ""; var name = string.IsNullOrWhiteSpace(planName) ? "Map 扫描" : planName;
        var head = model.Length == 0 || name.Contains(model, StringComparison.OrdinalIgnoreCase) ? name : $"{model} · {name}";
        return $"{head} · 系统效率 %（第二行 Iq） · {when.LocalDateTime:yyyy-MM-dd HH:mm}";
    }
    public string PauseButtonText => _orchestrator?.PauseRequested == true ? "恢复" : "暂停";

    public ICommand LoadPresetCommand { get; }
    public ICommand ApplyGridCommand { get; }
    public ICommand PrecheckCommand { get; }
    public ICommand StartNewCommand { get; }
    public ICommand ContinueCommand { get; }
    public ICommand PauseCommand { get; }
    public ICommand StopCommand { get; }
    public ICommand ExportCommand { get; }
    public ICommand LoadStateCommand { get; }

    public MapScanViewModel(MainViewModel host)
    {
        _host = host;
        LoadPresetCommand = Command(async () =>
        {
            if (IsRunning) throw new InvalidOperationException("扫描运行中不能修改设置");
            var preset = MapScanPlanner.Ykb145();
            ApplyPlanToFields(preset with { LoadMode = _loadMode, Order = _order, TorqueReference = _reference });
            await ApplyGridAsync();
        });
        ApplyGridCommand = Command(ApplyGridAsync);
        PrecheckCommand = Command(async () => await _host.MapPrecheckAsync());
        StartNewCommand = Command(() => StartAsync(false));
        ContinueCommand = Command(() => StartAsync(true));
        PauseCommand = Command(() =>
        {
            if (_orchestrator is not { Running: true } o) throw new InvalidOperationException("Map 扫描没有在运行");
            o.PauseRequested = !o.PauseRequested;
            _host.MapEvent(o.PauseRequested ? "Map 扫描：暂停请求，当前点完成后卸载并保持冷却转速" : "Map 扫描：恢复");
            return Task.CompletedTask;
        });
        StopCommand = Command(async () => { await CancelAsync(); });
        ExportCommand = Command(ExportAsync);
        LoadStateCommand = Command(LoadStateAsync);
    }

    public void Refresh()
    {
        foreach (var p in new[] { nameof(IsRunning), nameof(SettingsEditable), nameof(StatusText), nameof(StatusColor), nameof(ProgressText), nameof(StageText),
            nameof(CurrentPointText), nameof(SpeedText), nameof(TorqueText), nameof(IqDutyText), nameof(PointEfficiencyText), nameof(TemperatureText),
            nameof(TemperatureMax), nameof(TemperatureValue), nameof(TemperatureColor), nameof(GateText), nameof(ProtectionText), nameof(PauseButtonText) })
            Raise(p);
        if (Grid is { } g && g.Version != _renderedVersion) { _renderedVersion = g.Version; GridChanged?.Invoke(); }
    }

    public void CellClicked(string id)
    {
        if (Grid is not { } g || !g.Toggle(id)) return;
        var cell = g.Snapshot().First(c => c.Id == id);
        _host.MapEvent($"Map 网格 {id}：{(cell.State == MapCellState.Pending ? cell.Retest ? "加入重测" : "恢复待测" : cell.State == MapCellState.Skipped ? "跳过" : "撤销重测，恢复 " + cell.State)}");
        Refresh();
        _ = SaveStateAsync();
    }

    public async Task CancelAsync()
    {
        if (_cts != null) await _cts.CancelAsync();
        if (_task != null) try { await _task; } catch (Exception) { }
    }

    private async Task ApplyGridAsync()
    {
        if (IsRunning) throw new InvalidOperationException("扫描运行中不能重建网格");
        var plan = BuildPlan();
        MapScanPlanner.Validate(plan, _host.Config);
        if (Grid?.HasResults == true && !Ask("重新生成网格会清空当前网格的结果（已导出的文件不受影响），是否继续？")) return;
        Grid = new MapScanGrid(plan);
        _archiveDirectory = null; _noLoadResults.Clear(); RecentPoints.Clear(); _lastResult = null;
        _host.MapEvent($"Map 网格：{plan.Columns.Count} 列，{Grid.Total} 点");
        Refresh();
        await Task.CompletedTask;
    }

    private async Task StartAsync(bool continueExisting)
    {
        if (IsRunning) throw new InvalidOperationException("Map 扫描正在运行");
        if (_host.IsRecipeRunning) throw new InvalidOperationException("P1 电机性能测试正在运行，先停止");
        var runtime = _host.ServoRuntime ?? throw new InvalidOperationException("请先连接负载伺服（EtherCAT 直连或仿真）");
        var recorder = _host.Recorder ?? throw new InvalidOperationException("没有记录会话，请先连接");
        var config = _host.Config;
        var motorModel = _host.MotorModel.Trim();
        if (string.IsNullOrWhiteSpace(motorModel)) throw new InvalidOperationException("请先输入电机型号，再开始 Map 扫描。");
        var plan = BuildPlan();
        if (continueExisting)
        {
            if (Grid == null) throw new InvalidOperationException("没有可继续的网格：请先生成网格或载入断点");
            if (!SameCells(Grid.Plan, plan) &&
                !Ask("设置里的转速 / 扭矩与当前网格不同。继续将沿用当前网格的点，只采用新的时间、温控与判据参数，是否继续？")) return;
            Grid.ReplaceSettings(plan);
            MapScanPlanner.Validate(Grid.Plan, config);
        }
        else
        {
            MapScanPlanner.Validate(plan, config);
            // A freshly generated grid keeps the skip clicks made on it.
            if (Grid is { HasResults: false } fresh && SameCells(fresh.Plan, plan)) fresh.ReplaceSettings(plan);
            else
            {
                if (Grid?.HasResults == true && !Ask("新扫描会清空当前网格的结果（已导出的文件不受影响），是否继续？")) return;
                Grid = new MapScanGrid(plan);
            }
            _archiveDirectory = null; _noLoadResults.Clear(); RecentPoints.Clear(); _lastResult = null;
        }
        var grid = Grid;
        Refresh();
        var pending = grid.Count(MapCellState.Pending);
        if (pending == 0) throw new InvalidOperationException("网格中没有待测点（全部已测或已跳过）");
        var precheck = await _host.MapPrecheckAsync();
        if (!precheck.StartsWith("PASS", StringComparison.Ordinal)) throw new InvalidOperationException(precheck);
        var p = grid.Plan;
        var rawTemp = _host.Snapshot?.DutMotorTempC;
        var tempLine = MapScanOrchestrator.ValidTemperature(rawTemp) is { } t
            ? $"当前电机温度 {t:F1} °C；≥ {p.PauseTempC:F0} °C 卸载冷却，≤ {p.ResumeTempC:F0} °C 恢复"
            : $"警告：电机温度读数无效（{rawTemp?.ToString("F1", CultureInfo.InvariantCulture) ?? "无数据"}），温控门控不会生效，只剩台架与 VESC 硬保护";
        if (!Ask($"确认启动 Map 扫描？\n电机：{motorModel}\n待测 {pending} 点 / 共 {grid.Total} 点，{p.Columns.Count} 列（{(p.Order == MapColumnOrder.Interleaved ? "两端交错" : "升序")}）" +
                 $"，{(p.LoadMode == MapLoadMode.Staircase ? "阶梯递增" : "逐点回零")}，扭矩基准：{(p.TorqueReference == MapTorqueReference.DynClosedLoop ? "DYN 闭环" : "伺服指令（DYN 会偏高约 0.3–0.6 N·m）")}\n" +
                 $"转速 {p.Columns.Min(c => c.Rpm):F0}–{p.Columns.Max(c => c.Rpm):F0} rpm，扭矩上限 {p.Columns.Max(c => c.MaxTorqueNm):F2} N·m\n{tempLine}\n" +
                 "DUT 速度环定速、伺服 CST 加扭矩；不可达或故障时伺服扭矩先回零，再停 DUT。")) return;
        _fileTag = MainViewModel.SafeFileTag(motorModel);
        if (_archiveDirectory == null)
        {
            var root = Path.GetFullPath(config.DataDirectory, _host.RootDirectory);
            _archiveDirectory = Path.Combine(root, $"{DateTime.Now:yyyyMMdd_HHmmss_fff}_{_fileTag}_map");
            Directory.CreateDirectory(_archiveDirectory);
        }
        await File.WriteAllTextAsync(Path.Combine(_archiveDirectory, "map_plan.json"), JsonSerializer.Serialize(grid.Plan, BenchConfig.Json));
        await SaveStateAsync();
        _host.MapEvent($"MAP 扫描开始：{motorModel}；待测 {pending} 点；目录 {_archiveDirectory}");
        recorder.Event("map_scan_motor", $"motorModel={motorModel}; archive={_archiveDirectory}");

        var orchestrator = new MapScanOrchestrator(runtime, config, recorder);
        orchestrator.PointCompleted += OnPointCompleted;
        orchestrator.StatusChanged += Refresh;
        _orchestrator = orchestrator;
        _cts?.Dispose(); _cts = new();
        var dut = _host.DutRuntime; var sensor = _host.SensorRuntime; var mock = _host.SimulationBridge;
        try
        {
            runtime.AcquireAutoTest(); dut?.AcquireAutoTest(); sensor?.AcquireAutoTest();
            _task = orchestrator.RunAsync(grid, _cts.Token,
                setDutSpeed: (rpm, ct) => SetDutSpeedAsync(mock, rpm, ct),
                stopDut: ct => StopDutAsync(mock, ct),
                precheck: _host.MapAutomaticPrecheckAsync,
                ensureExternalSafety: _host.EnsureMapTelemetryHealthy);
            Refresh();
            await _task;
        }
        finally
        {
            runtime.ReleaseAutoTest(); dut?.ReleaseAutoTest(); sensor?.ReleaseAutoTest();
            _host.MapEvent($"MAP 扫描结束：{orchestrator.EndReason}；通过 {grid.Count(MapCellState.Pass)}，不可达 {grid.Count(MapCellState.Unreachable)}，" +
                $"失败 {grid.Count(MapCellState.Failed)}，待测 {grid.Count(MapCellState.Pending)}" + (orchestrator.StopDetail is { } d ? $"；{d}" : ""));
            try { await ExportAsync(); }
            catch (Exception ex) { _host.MapEvent("Map 结果导出失败：" + ex.Message); }
            Refresh();
        }
    }

    private Task SetDutSpeedAsync(MockRealtimeBridge? mock, double rpm, CancellationToken ct)
    {
        if (mock != null) { mock.SetSpeed(rpm * _host.Config.Drive.ExpectedRotationSign); return Task.CompletedTask; }
        var dut = _host.DutRuntime ?? throw new SafetyTripException("VESC/M1 已断开，无法发送转速指令");
        return dut.SetDutAsync(DutControlMode.SpeedRpm, rpm, ct, ControlOwner.AutoTest);
    }
    private Task StopDutAsync(MockRealtimeBridge? mock, CancellationToken ct)
    {
        if (mock != null) { mock.SetSpeed(0); return Task.CompletedTask; }
        return _host.DutRuntime?.StopDutAsync(ct) ?? Task.FromException(new InvalidOperationException("DUT 紧急停机通道已断开"));
    }

    private void OnPointCompleted(PointResult r)
    {
        if (r.PointId.EndsWith("_NL", StringComparison.Ordinal)) _noLoadResults.Add(r);
        else _lastResult = r;
        var result = r.Result switch { "PASS" => "OK", "PASS_WITH_WARNING" => "OK!", "UNREACHABLE" => "不可达", "UNSTABLE" => "不稳", _ => r.Result };
        var efficiency = r.PointId.EndsWith("_NL", StringComparison.Ordinal) ? "  空载" : Fmt(r.Efficiency?.Mean, "F1").PadLeft(5) + "%";
        RecentPoints.Insert(0, string.Create(CultureInfo.InvariantCulture,
            $"{DateTime.Now:HH:mm:ss} {r.PointId,-11} {result,-3} η{efficiency} DYN {Fmt(r.Torque?.Mean, "F3")} {Fmt(r.Temperature?.Mean, "F0")}°C"));
        while (RecentPoints.Count > 40) RecentPoints.RemoveAt(RecentPoints.Count - 1);
        Refresh();
        _ = SaveStateAsync();
    }

    private async Task SaveStateAsync()
    {
        if (Grid is not { } g || _archiveDirectory is not { } dir) return;
        await _saveGate.WaitAsync();
        try
        {
            var state = g.ToState(_host.MotorModel.Trim()) with { NoLoad = [.. _noLoadResults] };
            var temp = Path.Combine(dir, "map_state.json.tmp");
            await File.WriteAllTextAsync(temp, JsonSerializer.Serialize(state, BenchConfig.Json));
            File.Move(temp, Path.Combine(dir, "map_state.json"), true);
        }
        catch (Exception ex) { _host.MapEvent("Map 断点保存失败：" + ex.Message); }
        finally { _saveGate.Release(); }
    }

    private async Task LoadStateAsync()
    {
        if (IsRunning) throw new InvalidOperationException("扫描运行中不能载入断点");
        var dialog = new OpenFileDialog { Filter = "Map 断点 (map_state.json)|map_state.json;*_map_state.json|JSON (*.json)|*.json" };
        if (dialog.ShowDialog() != true) return;
        var state = JsonSerializer.Deserialize<MapScanState>(await File.ReadAllTextAsync(dialog.FileName), BenchConfig.Json)
            ?? throw new InvalidDataException("断点文件为空");
        MapScanPlanner.Validate(state.Plan, _host.Config);
        if (Grid?.HasResults == true && !Ask("载入断点会替换当前网格，是否继续？")) return;
        Grid = MapScanGrid.FromState(state);
        _archiveDirectory = Path.GetDirectoryName(dialog.FileName);
        _noLoadResults.Clear(); _noLoadResults.AddRange(state.NoLoad ?? []);
        RecentPoints.Clear(); _lastResult = null;
        ApplyPlanToFields(state.Plan);
        if (string.IsNullOrWhiteSpace(_host.MotorModel) && !string.IsNullOrWhiteSpace(state.MotorModel)) MotorModel = state.MotorModel;
        _host.MapEvent($"Map 断点已载入：{dialog.FileName}；通过 {Grid.Count(MapCellState.Pass)}，待测 {Grid.Count(MapCellState.Pending)}；继续时写入同一目录");
        Refresh();
    }

    private async Task ExportAsync()
    {
        if (Grid is not { } g) throw new InvalidOperationException("没有网格可导出");
        var motorModel = _host.MotorModel.Trim();
        if (string.IsNullOrWhiteSpace(_fileTag)) _fileTag = MainViewModel.SafeFileTag(motorModel);
        if (_archiveDirectory == null)
        {
            _archiveDirectory = Path.Combine(Path.GetFullPath(_host.Config.DataDirectory, _host.RootDirectory), $"{DateTime.Now:yyyyMMdd_HHmmss_fff}_{_fileTag}_map");
            Directory.CreateDirectory(_archiveDirectory);
        }
        var dir = _archiveDirectory;
        var cells = g.Snapshot().OrderBy(c => c.Rpm).ThenBy(c => c.TorqueNm).ToList();
        static string N(double? v) => v?.ToString("G17", CultureInfo.InvariantCulture) ?? "";
        static string Q(string? s) => "\"" + (s ?? "").Replace("\"", "\"\"") + "\"";
        var csv = new StringBuilder("motor_model,point_id,target_rpm,target_torque_nm,state,result,reason,dyn_torque_nm,dut_rpm,servo_target_torque_nm,servo_actual_torque_nm," +
            "iq_actual_a,vbus_v,ibus_a,pin_w,pout_w,efficiency_pct,duty_pct,motor_temperature_c,controller_temperature_c,vesc_fault_code,samples\n");
        void Row(string id, double rpm, double torque, string state, PointResult? r, string? reason) => csv.AppendLine(string.Join(',', Q(motorModel), Q(id),
            N(rpm), N(torque), state, r?.Result ?? "", Q(reason ?? r?.Reason), N(r?.Torque?.Mean), N(r?.Speed?.Mean), N(r?.ServoTargetTorque?.Mean), N(r?.ServoActualTorque?.Mean),
            N(r?.IqActual?.Mean), N(r?.Vbus?.Mean), N(r?.DcBusCurrent?.Mean), N(r?.InputPower?.Mean), N(r?.MechanicalPower?.Mean), N(r?.Efficiency?.Mean),
            N(r?.Duty?.Mean), N(r?.Temperature?.Mean), N(r?.ControllerTemperature?.Mean), r?.DutFaultCode ?? 0, r?.Torque?.Count ?? 0));
        foreach (var r in _noLoadResults) Row(r.PointId, double.Parse(r.PointId[1..r.PointId.IndexOf('_')], CultureInfo.InvariantCulture), 0, "NO_LOAD", r, null);
        foreach (var c in cells) Row(c.Id, c.Rpm, c.TorqueNm, c.State.ToString().ToUpperInvariant(), c.Result, c.Reason);
        await File.WriteAllTextAsync(Path.Combine(dir, $"{_fileTag}_map_points.csv"), csv.ToString(), new UTF8Encoding(true));

        // Efficiency matrix for spreadsheets: rows = torque (high to low), columns = rpm; non-PASS cells carry their state.
        var rpms = cells.Select(c => c.Rpm).Distinct().Order().ToList();
        var matrix = new StringBuilder("torque_nm\\rpm," + string.Join(',', rpms.Select(r => N(r))) + "\n");
        foreach (var torque in cells.Select(c => c.TorqueNm).Distinct().OrderDescending())
            matrix.AppendLine(N(torque) + "," + string.Join(',', rpms.Select(rpm => cells.FirstOrDefault(c => Math.Abs(c.Rpm - rpm) < 1e-6 && Math.Abs(c.TorqueNm - torque) < 1e-9) is { } c
                ? c.State == MapCellState.Pass && c.Result?.Efficiency is { } e ? e.Mean.ToString("F2", CultureInfo.InvariantCulture) : c.State.ToString().ToUpperInvariant()
                : "")));
        await File.WriteAllTextAsync(Path.Combine(dir, $"{_fileTag}_map_efficiency.csv"), matrix.ToString(), new UTF8Encoding(true));
        await File.WriteAllTextAsync(Path.Combine(dir, $"{_fileTag}_map_state.json"),
            JsonSerializer.Serialize(g.ToState(motorModel) with { NoLoad = [.. _noLoadResults] }, BenchConfig.Json));
        if (_host.Recorder is { } recorder)
        {
            foreach (var (source, target) in new[] { ("raw.csv", "_raw.csv"), ("events.jsonl", "_events.jsonl") })
            {
                var path = Path.Combine(recorder.DirectoryPath, source);
                try { if (File.Exists(path)) File.Copy(path, Path.Combine(dir, _fileTag + "_map" + target), true); }
                catch (IOException ex) { _host.MapEvent($"Map 导出：复制 {source} 失败：{ex.Message}"); }
            }
        }
        ExportVisualRequested?.Invoke(Path.Combine(dir, $"{_fileTag}_map.png"));
        _host.MapEvent($"Map 结果已导出：{dir}");
    }

    // --map-smoke: simulation scan with no dialogs. Returns the PASS summary or throws.
    internal async Task<string> SmokeAsync()
    {
        _interactive = false;
        await _host.ConnectSimulationForMapAsync();
        MotorModel = "SIM-MAP";
        ColumnsText = "650:0.75, 2000:0.50, 3500:0.50"; SampleSecondsText = "0.5"; SpeedRampText = "2000";
        foreach (var p in new[] { nameof(ColumnsText), nameof(SampleSecondsText), nameof(SpeedRampText) }) Raise(p);
        await ApplyGridAsync();
        var skipped = MapScanPlanner.PointId(2000, .5);
        CellClicked(skipped);
        await StartAsync(false);
        var cells = Grid!.Snapshot();
        var pass = cells.Count(c => c.State == MapCellState.Pass);
        if (_orchestrator?.EndReason != "COMPLETED" || pass != cells.Count - 1 || cells.Single(c => c.Id == skipped).State != MapCellState.Skipped)
            throw new InvalidOperationException($"Map 仿真扫描结果不符：end={_orchestrator?.EndReason}; pass={pass}/{cells.Count}; " +
                string.Join(",", cells.Select(c => $"{c.Id}={c.State}")));
        var retest = MapScanPlanner.PointId(650, .25);
        CellClicked(retest);
        var queued = Grid.Snapshot().Single(c => c.Id == retest);
        CellClicked(retest);
        var restored = Grid.Snapshot().Single(c => c.Id == retest);
        if (queued is not { State: MapCellState.Pending, Retest: true } || restored.State != MapCellState.Pass)
            throw new InvalidOperationException("重测点击切换不符");
        foreach (var file in new[] { $"{_fileTag}_map_points.csv", $"{_fileTag}_map_efficiency.csv", $"{_fileTag}_map_state.json", $"{_fileTag}_map.png", "map_state.json" })
            if (!File.Exists(Path.Combine(_archiveDirectory!, file))) throw new FileNotFoundException("缺少导出文件", file);
        return $"PASS: simulated Map scan {pass}/{cells.Count} cells PASS, 1 skipped by click, no-load {_noLoadResults.Count}, retest toggle restored; archive={_archiveDirectory}";
    }

    private MapScanPlan BuildPlan()
    {
        var plan = new MapScanPlan
        {
            Name = Grid is { } g && SameColumns(g.Plan.Columns, MapScanPlanner.Ykb145().Columns) ? g.Plan.Name : "Map 扫描",
            Columns = MapScanPlanner.ParseColumns(ColumnsText, Number(DefaultCapText, "默认上限")),
            StartTorqueNm = Number(StartTorqueText, "起始扭矩"),
            TorqueStepNm = Number(StepTorqueText, "扭矩步长"),
            SampleSeconds = Number(SampleSecondsText, "采样时间"),
            TorqueRampNmPerSec = Number(TorqueRampText, "扭矩斜坡"),
            SpeedRampRpmPerSec = Number(SpeedRampText, "升速斜坡"),
            StableTimeoutSeconds = Number(StableTimeoutText, "稳定超时"),
            CoolingRpm = Number(CoolingRpmText, "冷却转速"),
            PauseTempC = Number(PauseTempText, "暂停温度"),
            ResumeTempC = Number(ResumeTempText, "恢复温度"),
            SpeedDropFraction = Number(SpeedDropPctText, "转速跌落") / 100,
            SpeedDropConfirmSeconds = Number(DropConfirmText, "跌落确认时间"),
            DutySaturationPct = Number(DutyLimitText, "Duty 判据"),
            LoadMode = _loadMode, Order = _order, TorqueReference = _reference
        };
        if (SameColumns(plan.Columns, MapScanPlanner.Ykb145().Columns)) plan = plan with { Name = MapScanPlanner.Ykb145().Name };
        return plan;
    }

    private void ApplyPlanToFields(MapScanPlan p)
    {
        static string F(double v, string format = "0.###") => v.ToString(format, CultureInfo.InvariantCulture);
        ColumnsText = MapScanPlanner.FormatColumns(p.Columns);
        DefaultCapText = F(p.Columns.Max(c => c.MaxTorqueNm), "0.00");
        StartTorqueText = F(p.StartTorqueNm); StepTorqueText = F(p.TorqueStepNm); SampleSecondsText = F(p.SampleSeconds);
        TorqueRampText = F(p.TorqueRampNmPerSec); SpeedRampText = F(p.SpeedRampRpmPerSec); StableTimeoutText = F(p.StableTimeoutSeconds);
        CoolingRpmText = F(p.CoolingRpm); PauseTempText = F(p.PauseTempC); ResumeTempText = F(p.ResumeTempC);
        SpeedDropPctText = F(p.SpeedDropFraction * 100); DropConfirmText = F(p.SpeedDropConfirmSeconds); DutyLimitText = F(p.DutySaturationPct);
        _loadMode = p.LoadMode; _order = p.Order; _reference = p.TorqueReference;
        foreach (var name in new[] { nameof(ColumnsText), nameof(DefaultCapText), nameof(StartTorqueText), nameof(StepTorqueText), nameof(SampleSecondsText),
            nameof(TorqueRampText), nameof(SpeedRampText), nameof(StableTimeoutText), nameof(CoolingRpmText), nameof(PauseTempText), nameof(ResumeTempText),
            nameof(SpeedDropPctText), nameof(DropConfirmText), nameof(DutyLimitText), nameof(IsStaircase), nameof(IsReturnToZero), nameof(IsAscending),
            nameof(IsInterleaved), nameof(IsServoReference), nameof(IsDynClosedLoop) }) Raise(name);
    }

    private static bool SameColumns(IReadOnlyList<MapColumn> a, IReadOnlyList<MapColumn> b) =>
        a.Count == b.Count && a.OrderBy(c => c.Rpm).Zip(b.OrderBy(c => c.Rpm)).All(x => Math.Abs(x.First.Rpm - x.Second.Rpm) < 1e-6 && Math.Abs(x.First.MaxTorqueNm - x.Second.MaxTorqueNm) < 1e-9);
    private static bool SameCells(MapScanPlan a, MapScanPlan b) =>
        SameColumns(a.Columns, b.Columns) && Math.Abs(a.StartTorqueNm - b.StartTorqueNm) < 1e-9 && Math.Abs(a.TorqueStepNm - b.TorqueStepNm) < 1e-9;

    private void SetLoadMode(MapLoadMode mode) { _loadMode = mode; Raise(nameof(IsStaircase)); Raise(nameof(IsReturnToZero)); }
    private void SetOrder(MapColumnOrder order) { _order = order; Raise(nameof(IsAscending)); Raise(nameof(IsInterleaved)); }
    private void SetReference(MapTorqueReference reference) { _reference = reference; Raise(nameof(IsServoReference)); Raise(nameof(IsDynClosedLoop)); }
    private bool Ask(string text) => !_interactive || MainViewModel.MapConfirm(text);
    private static double Number(string text, string label) =>
        double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) && double.IsFinite(v)
            ? v : throw new ArgumentException($"{label}：请输入有效数字（小数点使用 .）");
    private static double Parse(string text, double fallback) =>
        double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) && double.IsFinite(v) ? v : fallback;
    private static string Abs(double? value, string format) => value is { } v && double.IsFinite(v) ? Math.Abs(v).ToString(format, CultureInfo.InvariantCulture) : "—";
    private static string Fmt(double? value, string format) => value?.ToString(format, CultureInfo.InvariantCulture) ?? "—";
    private ICommand Command(Func<Task> action) => new AsyncCommand(async _ =>
    {
        try { await action(); }
        catch (OperationCanceledException) { _host.MapEvent("Map 扫描操作已取消"); }
        catch (Exception ex) { if (_interactive) _host.MapError(ex); else _host.MapEvent(ex.Message); }
        finally { Refresh(); }
    });
    private void Raise(string name) => PropertyChanged?.Invoke(this, new(name));
}
