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
    public event Action<string, string>? ExportVisualsRequested;
    private BenchRuntime? _runtime;
    private BenchRuntime? _dutRuntime;
    private BenchRuntime? _sensorRuntime;
    private MockRealtimeBridge? _mock;
    private SessionRecorder? _recorder;
    private TestOrchestrator? _orchestrator;
    private CancellationTokenSource? _recipeCts;
    private Task? _recipeTask;
    private double _autoTestIqCommand;
    private double _autoTestDutyCommand;
    private BenchConfig _config = new();
    private string? _configError;
    private readonly string _root = File.Exists(Path.Combine(Environment.CurrentDirectory, "config/bench.json")) ? Environment.CurrentDirectory : AppContext.BaseDirectory;
    private bool _busy;
    private DutControlMode? _dutCommandMode;
    private double _dutCommandValue;
    private string? _dutScanStatus;
    private string? _connectedDutPortName;
    private string? _etherCatReadOnlyStatus;
    private string _motorModel = "";
    private string _testMotorModel = "";
    private string? _testArchiveDirectory;
    private string? _testFileTag;
    private DateTimeOffset _lastLinkTraceAt;
    private DateTimeOffset _nextDutPortRefreshAt;
    private int _dutRecoveryRunning;
    private int _connectionMode = 1;
    public BenchSnapshot? Snapshot
    {
        get
        {
            if (_runtime == null && _dutRuntime == null && _sensorRuntime == null) return null;
            var value = _runtime?.Latest ?? new BenchSnapshot();
            if (_dutRuntime?.Latest is { } dut) value = value with { DutBusV = dut.DutBusV, DutCurrentA = dut.DutCurrentA,
                DutPhaseCurrentA = dut.DutPhaseCurrentA, DutIqCommandA = dut.DutIqCommandA, DutDutyCyclePct = dut.DutDutyCyclePct,
                DutMotorTempC = dut.DutMotorTempC, DutControllerTempC = dut.DutControllerTempC, DutFaultCode = dut.DutFaultCode, DutSpeedRpm = dut.DutSpeedRpm };
            if (_sensorRuntime?.Latest is { } sensor) value = value with { ExternalHealthy = sensor.ExternalHealthy,
                ExternalTorqueNm = sensor.ExternalTorqueNm, ExternalSpeedRpm = sensor.ExternalSpeedRpm };
            return value;
        }
    }
    public ObservableCollection<string> Events { get; } = [];
    public ObservableCollection<PointResult> TestResults { get; } = [];
    public ObservableCollection<WindowsSerialPortDiscovery.PortInfo> DutPorts { get; } = [];
    public WindowsSerialPortDiscovery.PortInfo? SelectedDutPort { get; set; }
    public ObservableCollection<WindowsSerialPortDiscovery.PortInfo> SensorPorts { get; } = [];
    public WindowsSerialPortDiscovery.PortInfo? SelectedSensorPort { get; set; }
    public string[] ConnectionModes { get; } = ["仿真 / 无硬件", "EtherCAT 直连 / OP-SYNC", "TwinCAT ADS / 兼容只读"];
    public string[] LoadModes { get; } = ["恒转矩", "恒功率", "转速-转矩表", "带载启动（转速触发）"];
    public string[] TrendWindows { get; } = ["最近 10 秒", "最近 60 秒", "全程（抽稀）"];
    public string[] ControlModes { get; } = ["CSV 速度模式", "CST 电子负载模式"];
    public int ConnectionMode
    {
        get => _connectionMode;
        set
        {
            if (_connectionMode == value) return;
            _connectionMode = value;
            PropertyChanged?.Invoke(this, new(nameof(ConnectionMode)));
            PropertyChanged?.Invoke(this, new(nameof(ModeBanner)));
        }
    }
    private int _loadModeIndex;
    public int LoadModeIndex
    {
        get => _loadModeIndex;
        set
        {
            if (_loadModeIndex == value) return;
            _loadModeIndex = value;
            PropertyChanged?.Invoke(this, new(nameof(LoadModeIndex)));
            PropertyChanged?.Invoke(this, new(nameof(CstLoadStatusDisplay)));
        }
    }
    private int _controlModeIndex = 1;
    public int ControlModeIndex
    {
        get => _controlModeIndex;
        set
        {
            if (value is < 0 or > 1 || _controlModeIndex == value) return;
            _controlModeIndex = value;
            PropertyChanged?.Invoke(this, new(nameof(ControlModeIndex)));
            PropertyChanged?.Invoke(this, new(nameof(CurrentControlModeDisplay)));
            PropertyChanged?.Invoke(this, new(nameof(ServoHeaderSummary)));
        }
    }
    private int _servoControlTabIndex;
    public int ServoControlTabIndex
    {
        get => _servoControlTabIndex;
        set
        {
            if (value is < 0 or > 2 || _servoControlTabIndex == value) return;
            _servoControlTabIndex = value;
            // Navigation selects the next command's mode; it sends no drive commands.
            if (value > 0) ControlModeIndex = value - 1;
            PropertyChanged?.Invoke(this, new(nameof(ServoControlTabIndex)));
        }
    }
    public string CurrentControlModeDisplay => ControlModes[ControlModeIndex];
    public string ServoEnableStatusDisplay => Snapshot is not { Connected: true } s
        ? "未连接 · 状态未知"
        : EtherCatHasFault ? "故障" : s.ServoOn ? "已使能" : "未使能";
    public int TrendWindowIndex { get; set; } = 1;
    public string TorqueText { get; set; } = "0.10";
    public string RampText { get; set; } = "0.10";
    public string PowerText { get; set; } = "3";
    public string SimRpmText { get; set; } = "300";
    public string DirectRpmText { get; set; } = "500";
    public string DirectRampRpmPerSecText { get; set; } = "100";
    public string[] DutControlModes { get; } = ["转速 / rpm", "电机电流 / A", "制动电流 / A", "占空比 / -"];
    public int DutControlModeIndex { get; set; }
    public string DutCommandValueText { get; set; } = "300";
    public string DutDutyText { get; set; } = "0.20";
    public string DutSpeedCommandText { get; set; } = "1000";
    public string DutCurrentText { get; set; } = "3.00";
    public string DutPositionText { get; set; } = "0.00";
    public string CurveStartRpmText { get; set; } = "100";
    public string CurveEndRpmText { get; set; } = "400";
    public string CurveRpmStepText { get; set; } = "100";
    public string CurveStartTorqueText { get; set; } = "0.05";
    public string CurveEndTorqueText { get; set; } = "0.20";
    public string CurveTorqueStepText { get; set; } = "0.05";
    public string RecipeJson { get; set; } = "";
    public string RecipeNameDisplay => TryRecipe()?.Name ?? "未选择配方";
    public string MotorModel
    {
        get => _motorModel;
        set
        {
            if (_orchestrator?.Running == true || _motorModel == value) return;
            _motorModel = value?.Trim() ?? "";
            PropertyChanged?.Invoke(this, new(nameof(MotorModel)));
        }
    }
    public bool AutoTestConfigurationEnabled => _orchestrator?.Running != true;
    public bool IsAutoTestRunning => _orchestrator?.Running == true;
    public string AutoTestStatus => _orchestrator?.Stage switch { AutoTestStage.FAULT => "故障", AutoTestStage.FINISHED => "完成", _ when _orchestrator?.Running == true => "运行中", _ => "待机" };
    public string AutoTestStatusColor => _orchestrator?.Stage == AutoTestStage.FAULT ? "#F04B36" : _orchestrator?.Running == true ? "#24D18F" : "#F3C884";
    public string AutoProgressText => _orchestrator?.Running == true ? _orchestrator.Phase : $"已采样 {TestResults.Count} 点";
    public string PrecheckSummary { get; private set; } = "尚未执行预检查";
    public string Phase => _orchestrator?.Stage.ToString() ?? "IDLE";
    public string AutoCurrentPoint => _orchestrator?.CurrentPoint ?? "—";
    public string AutoTargetTorque => _orchestrator?.TargetTorqueNm is { } value ? $"{value:F2} N·m" : "— N·m";
    public string AutoSampleCount => $"已采样 {TestResults.Count} 点";
    public string AutoLiveMetrics => $"目标 {(_orchestrator?.TargetTorqueNm?.ToString("F2") ?? "—")}  实测 {(Snapshot?.ExternalTorqueNm?.ToString("F2") ?? "—")} N·m  RPM {(Snapshot?.DutSpeedRpm?.ToString("F0") ?? "—")}  {TestResults.Count} 点";
    public string VescPrecheckColor => PrecheckColor("VESC");
    public string ServoPrecheckColor => PrecheckColor("Servo");
    public string DynPrecheckColor => PrecheckColor("DYN");
    public string ZeroPrecheckColor => PrecheckColor("Zero");
    public string ConfigPrecheckColor => PrecheckColor("Config");
    private readonly Dictionary<string, string> _precheckStates = new(StringComparer.OrdinalIgnoreCase)
    { ["VESC"] = "WAITING", ["Servo"] = "WAITING", ["DYN"] = "WAITING", ["Zero"] = "WAITING", ["Config"] = "WAITING" };
    public string ConnectionText => Snapshot?.Connected == true
        ? _runtime!.IsSimulation ? "SIMULATION · 已连接" : _runtime.CanControlVelocity ? "EtherCAT DIRECT · OP/SYNC" : "ADS · 已连接"
        : _etherCatReadOnlyStatus ?? "DISCONNECTED";
    private bool EtherCatHasFault => Snapshot is { Connected: true } s &&
        (s.State == BenchState.Fault || s.Interlocks != Interlock.None || s.ErrorCode != 0 || !s.DriveReady);
    public string EtherCatStatusColor => EtherCatHasFault ? "#FF453A" : Snapshot?.Connected == true ? "#24D18F" : "#F4C542";
    public string EtherCatStatusText => EtherCatHasFault ? "故障" : Snapshot?.Connected == true ? "已连接" : "断链";
    public string ServoHeaderSummary => Snapshot is { Connected: true } s
        ? $"● 已连接    ● Servo {(s.ServoOn ? "ON" : "OFF")}    当前模式：{ControlModes[Math.Clamp(ControlModeIndex, 0, ControlModes.Length - 1)].Split(' ')[0]}"
        : $"● 未连接    ● Servo 状态未知    当前模式：{ControlModes[Math.Clamp(ControlModeIndex, 0, ControlModes.Length - 1)].Split(' ')[0]}";
    public string ModeBanner => _runtime?.CanControlVelocity == true
        ? $"EtherCAT 直连：OP/SYNC；CST 上限 {_config.Limits.MaxTorqueNm:F2} N·m / {_config.Limits.MaxPowerW:F0} W。"
        : _etherCatReadOnlyStatus != null
            ? "EtherCAT PRE-OP 最近扫描成功：主站已释放网卡，仅完成只读身份/PDO核验。"
            : _runtime == null
                ? ConnectionMode switch
                {
                    1 => "EtherCAT 直连待连接：连接后主站将独占专用网卡并持续保持 OP/SYNC。",
                    2 => "TwinCAT ADS 兼容模式待连接：需要本机 ADS Router。",
                    _ => "仿真模式：全部数值由模拟模型产生，不代表实机测试结果。"
                }
                : _runtime.IsSimulation
                    ? "仿真模式：全部数值由模拟模型产生，不代表实机测试结果。"
                    : "ADS 实机模式：" + (_runtime.CanWrite ? "配置核验通过，仍需现场联锁确认。" : "只读观测，所有加载写入被禁止。");
    public string LimitsText => $"当前上限 {_config.Limits.MaxTorqueNm:F2} N·m / {_config.Limits.MaxSpeedRpm:F0} rpm / {_config.Limits.MaxPowerW:F0} W";
    private string LoadedStartSpeedCondition => _config.Drive.ExpectedRotationSign < 0
        ? $"≤ -{_config.Limits.LoadedStartTriggerRpm:F0} rpm"
        : $"≥ {_config.Limits.LoadedStartTriggerRpm:F0} rpm";
    public string CstLimitsText => $"扭矩范围：0～{_config.Limits.MaxTorqueNm:F2} N·m（连续 {_config.Limits.ContinuousTorqueNm:F2} N·m；峰值最长 {_config.Limits.PeakSeconds:F0} s）\n总功率上限：{_config.Limits.MaxPowerW:F0} W（所有加载模式生效）"
        + (Snapshot is { SpeedRpm: var rpm } && Math.Abs(rpm) >= 1
            ? $"；当前转速下功率允许的扭矩上限 {_config.Limits.MaxPowerW / (Math.Abs(rpm) * Math.PI / 30):F3} N·m" : "")
        + $"\n带载启动：伺服反馈 {LoadedStartSpeedCondition} 持续 {_config.Limits.LoadedStartTriggerConfirmMs} ms；相反方向 ≥{_config.Limits.LoadedStartReverseTripRpm:F0} rpm 持续 {_config.Limits.LoadedStartReverseConfirmMs} ms 触发保护";
    public string DirectSpeedRangeText => $"-{_config.Limits.MaxSpeedRpm:F0} ～ {_config.Limits.MaxSpeedRpm:F0} rpm";
    public string DirectRampRangeText => $"10 ～ {_config.Limits.MaxSpeedRampRpmPerSec:F0} rpm/s";
    public string DirectRampLabel => "加减速率 / rpm/s";
    public string RpmDisplay => Snapshot?.SpeedRpm.ToString("F1") ?? "—";
    public string TorqueDisplay => Snapshot is { } s ? $"{s.TargetTorqueNm:F3} / {s.ActualTorqueNm:F3}" : "—";
    public string PowerDisplay => Value(Snapshot?.DutElectricalInputPowerW, "F2", "W");
    public string BusDisplay => Snapshot?.DcBusV?.ToString("F1") ?? "不可用";
    public string TemperatureDisplay => Snapshot is { } s ? $"{s.MotorTempC?.ToString("F1") ?? "—"} / {s.BrakeTempC?.ToString("F1") ?? "—"}" : "—";
    public string SensorTorqueDisplay => Snapshot is { ExternalHealthy: true, ExternalTorqueNm: { } torque } ? $"{torque:F3} N·m" : "—";
    public string SensorSpeedDisplay => Snapshot is { ExternalHealthy: true, ExternalSpeedRpm: { } rpm } ? $"{rpm:F0} rpm" : "—";
    public string SensorPowerDisplay => Value(Snapshot?.MeasuredPowerW, "F2", "W");
    public string SensorStatusDisplay => _sensorRuntime == null ? "未连接" : Snapshot?.ExternalHealthy == true ? $"在线 · {SelectedSensorPort?.PortName} · CRC 正常" : $"已打开 {SelectedSensorPort?.PortName} · 等待数据";
    public string SensorConnectionColor => _sensorRuntime?.Latest.ExternalHealthy == true ? "#24D18F" : "#F4C542";
    public string SensorConnectionText => _sensorRuntime?.Latest.ExternalHealthy == true ? "已连接" : _sensorRuntime == null ? "断链" : "待数据";
    public string EfficiencyDisplay => Snapshot switch
    {
        { EfficiencyPct: { } efficiency } => $"{efficiency:F1} %",
        { ExternalHealthy: true, InputPowerW: null } => "待接入 DUT 电压/电流",
        { ExternalHealthy: false } => "等待扭矩传感器",
        _ => "—"
    };
    public string ServoSpeedDisplay => Value(Snapshot?.SpeedRpm, "F1", "RPM");
    public string ServoBusVoltageDisplay => Value(Snapshot?.DcBusV, "F1", "V");
    public string ServoBusCurrentDisplay => Value(Snapshot?.ServoBusCurrentA, "F2", "A");
    public string ServoPhaseCurrentDisplay => Value(Snapshot?.ServoPhaseCurrentA ?? Snapshot?.ServoCurrentA, "F2", "A");
    public string ServoDutyDisplay => Value(Snapshot?.ServoDutyCyclePct, "F1", "%");
    public string ServoTorqueDisplay => Value(Snapshot is null || (_config.Drive.RatedTorqueNm is null && _config.Drive.RawTorquePerNm is null) ? null : Snapshot.ActualTorqueNm, "F3", "N·m");
    public string ServoInputPowerDisplay => Value(Snapshot?.ServoInputPowerW, "F2", "W");
    public string ServoOutputPowerDisplay => Value(Snapshot?.ServoOutputPowerW, "F2", "W");
    public string ServoFeedbackPowerDisplay => Value(Snapshot?.ServoFeedbackPowerW, "F2", "W");
    public string ServoEfficiencyDisplay => Value(Snapshot?.ServoEfficiencyPct, "F1", "%");
    public string ServoLoadDisplay => Value(Snapshot?.ServoLoadPct, "F1", "%");
    public string ServoMotorTemperatureDisplay => Value(Snapshot?.MotorTempC, "F1", "°C");
    public string DutBusVoltageDisplay => Value(Snapshot?.DutBusV, "F1", "V");
    public string DutBusCurrentDisplay => Value(Snapshot?.DutCurrentA, "F2", "A");
    public string DutPhaseCurrentDisplay => Value(Snapshot?.DutPhaseCurrentA, "F2", "A");
    public string DutDutyDisplay => Value(Snapshot?.DutDutyCyclePct, "F1", "%");
    public string DutMotorTemperatureDisplay => Value(Snapshot?.DutMotorTempC, "F1", "°C");
    public string DutEfficiencyDisplay => Snapshot switch
    {
        { DutMotorEfficiencyPct: { } efficiency } => $"{efficiency:F1} %",
        { ExternalHealthy: false } => "等待 DYN-200 数据",
        { DutBusV: null } or { DutCurrentA: null } => "等待 DUT 母线数据",
        { DutCurrentA: <= 0 } => "0.0 %",
        _ => "等待有效功率"
    };
    public string DutSpeedDisplay => Value(Snapshot?.DutSpeedRpm, "F1", "RPM");
    public string DutControllerStatusDisplay => _dutScanStatus ??
        (_dutRuntime?.CanControlDut == true ? $"已连接 · {SelectedDutPort?.DisplayName}" : $"未连接 · {SelectedDutPort?.DisplayName ?? "未选择串口"}");
    public string DutConnectionColor => _dutRuntime?.CanControlDut == true ? "#24D18F" : "#F4C542";
    public string DutConnectionText => _dutRuntime?.CanControlDut == true ? "已连接" : "断链";
    public string DutCommandStatusDisplay => _dutCommandMode switch
    {
        DutControlMode.MotorCurrentA => $"Iq 控制：指令 {_dutCommandValue:F2} A / 实际 {Snapshot?.DutPhaseCurrentA?.ToString("F2") ?? "—"} A",
        DutControlMode.SpeedRpm => $"转速控制：指令 {_dutCommandValue:F0} RPM",
        _ => "待命"
    };
    public string CstLoadStatusDisplay => Snapshot switch
    {
        null => "待连接",
        { Connected: false } => "通信断开 · 无法确认加载状态",
        { ErrorCode: not 0 } s => s.DriveFaultDetail ?? $"驱动器故障 0x{s.ErrorCode:X} · 禁止加载",
        { Interlocks: not Interlock.None } s => $"联锁保护 {s.Interlocks} · 禁止加载",
        { ServoOn: false } => "等待 CST 零转矩使能",
        { TargetTorqueNm: var torque } when LoadModeIndex == (int)LoadMode.LoadedStart && Math.Abs(torque) > 0.001 => $"带载启动已锁存 · 指令 {Math.Abs(torque):F3} N·m",
        { SpeedRpm: var rpm } when LoadModeIndex == (int)LoadMode.LoadedStart => $"尚未输出转矩 · 当前伺服反馈 {rpm:F1} rpm · 启动要求 {LoadedStartSpeedCondition} 持续 {_config.Limits.LoadedStartTriggerConfirmMs} ms；请确认已发送加载指令",
        { SpeedRpm: var rpm } when Math.Abs(rpm) > 1e-6 && Math.Sign(rpm) != _config.Drive.ExpectedRotationSign => $"转向不符 · 当前伺服反馈 {rpm:F1} rpm · 配置要求{(_config.Drive.ExpectedRotationSign < 0 ? "负" : "正")}转速，转矩指令被限制为零",
        { SpeedRpm: var rpm } when LoadModeIndex != (int)LoadMode.ConstantTorque && Math.Abs(rpm) < _config.Limits.MinLoadSpeedRpm => $"等待 DUT 转速 ≥ {_config.Limits.MinLoadSpeedRpm:F0} rpm（当前 {rpm:F1} rpm，低速自动卸载）",
        { TargetTorqueNm: var torque } when Math.Abs(torque) > 0.001 => $"正在加载 · 指令 {Math.Abs(torque):F3} N·m",
        _ when LoadModeIndex == (int)LoadMode.ConstantTorque => "恒扭矩已就绪 · 可从 0 RPM 按斜坡加载",
        _ => "CST 已使能 · 等待加载指令"
    };
    private static string Value(double? value, string format, string unit) => value is { } v && double.IsFinite(v) ? $"{v.ToString(format, CultureInfo.InvariantCulture)} {unit}" : "N/A";
    public string StateText => Snapshot is { Connected: true } s
        ? $"{s.State} | Servo {(s.ServoOn ? "ON" : "OFF")} | Interlock 0x{(uint)s.Interlocks:X} | Error 0x{s.ErrorCode:X}"
          + (s.AuxiliaryFaultCode is { } auxiliary ? $" | Aux 0x{auxiliary:X8}" : "")
          + (string.IsNullOrWhiteSpace(s.DriveFaultDetail) ? "" : $" | {s.DriveFaultDetail}")
        : "离线 / Servo状态未知，不能据此确认停机";
    public string SessionText => _testArchiveDirectory ?? _recorder?.DirectoryPath ?? "连接后创建独立记录会话；每个会话保留配置与原始数据";
    public string? SessionDirectory => _testArchiveDirectory ?? _recorder?.DirectoryPath;
    public string ConfigurationInfo => _configError ?? $"配置：{Path.Combine(_root, "config/bench.json")}\n驱动：{_config.Drive.Name}\nADS：{_config.Ads.AmsNetId}:{_config.Ads.Port}\n扭矩传感器：{(_config.TorqueSensor.Enabled ? $"{_config.TorqueSensor.PortName} / {_config.TorqueSensor.BaudRate} / {_config.TorqueSensor.Protocol}" : "未启用")}\nVESC/M1：{(_config.Vesc.Enabled ? $"{_config.Vesc.PortName} / {_config.Vesc.BaudRate} / 电机极对数 {_config.Vesc.MotorPolePairs:G}" : "未启用")}\n实机写入：{_config.Drive.AllowHardwareWrites}\nPDO缩放：{_config.Drive.RawTorquePerNm?.ToString() ?? "TODO_VERIFY"} / { _config.Drive.RpmPerRawVelocity?.ToString() ?? "TODO_VERIFY"}\n型号码核实：{_config.Drive.ModelVerified}  方向核实：{_config.Drive.DirectionVerified}\n回生核实：{_config.Drive.RegenerationVerified}  硬件安全：{_config.Drive.HardwareSafetyVerified}";
    public ICommand EtherCatAdaptersCommand { get; }
    public ICommand EtherCatScanCommand { get; }
    public ICommand ConnectCommand { get; }
    public ICommand DisconnectCommand { get; }
    public ICommand EnableCommand { get; }
    public ICommand DisableCommand { get; }
    public ICommand ResetCommand { get; }
    public ICommand SelectedModeEnableCommand { get; }
    public ICommand SelectedModeDisableCommand { get; }
    public ICommand DirectEnableCommand { get; }
    public ICommand DirectRunCommand { get; }
    public ICommand DirectHaltCommand { get; }
    public ICommand DirectStopCommand { get; }
    public ICommand LoadCommand { get; }
    public ICommand StopCommand { get; }
    public ICommand StartDutCommand { get; }
    public ICommand StopDutCommand { get; }
    public ICommand RefreshDutPortsCommand { get; }
    public ICommand ConnectDutCommand { get; }
    public ICommand DisconnectDutCommand { get; }
    public ICommand DutDutyCommand { get; }
    public ICommand DutSpeedCommand { get; }
    public ICommand DutCurrentCommand { get; }
    public ICommand DutPositionCommand { get; }
    public ICommand AdjustDutValueCommand { get; }
    public ICommand RefreshSensorPortsCommand { get; }
    public ICommand ConnectSensorCommand { get; }
    public ICommand DisconnectSensorCommand { get; }
    public ICommand InjectCommand { get; }
    public ICommand OpenRecipeCommand { get; }
    public ICommand GenerateCurveCommand { get; }
    public ICommand SaveRecipeCommand { get; }
    public ICommand ValidateRecipeCommand { get; }
    public ICommand RunRecipeCommand { get; }
    public ICommand ExportCommand { get; }
    public ICommand PrecheckCommand { get; }
    public MainViewModel()
    {
        try { ReadConfig(); RecipeJson = JsonSerializer.Serialize(TorqueCurveRecipeFactory.CreateStandardPerformance(_config), BenchConfig.Json); RefreshDutPorts(); RefreshSensorPorts(); }
        catch (Exception ex) { _configError = ex.Message; AddEvent("配置错误：" + ex.Message); }
        EtherCatAdaptersCommand = Command(() => ProbeEtherCatAsync("adapters"));
        EtherCatScanCommand = Command(() => ProbeEtherCatAsync("scan"));
        ConnectCommand = Command(ConnectAsync);
        DisconnectCommand = Command(async () => { Runtime().RequireManualAccess(); await DisconnectAsync(); });
        EnableCommand = Command(async () =>
        {
            var r = Runtime();
            if (Confirm("确认当前连接、零转矩、机械护罩及硬件急停，申请 Servo ON？")) { r.Enable(); await Task.Delay(100); }
        });
        DisableCommand = Command(async () => { await CancelRecipeAsync(); await Runtime().StopAsync(true, CancellationToken.None); });
        ResetCommand = Command(() => { if (Confirm("确认故障原因已消除，复位后不恢复加载？")) Runtime().ResetFault(); return Task.CompletedTask; });
        SelectedModeEnableCommand = Command(async () =>
        {
            var runtime = Runtime();
            if (ControlModeIndex == 0)
            {
                if (!runtime.CanControlVelocity) throw new InvalidOperationException("当前连接不支持 CSV 速度模式");
                await runtime.EnableVelocityAsync(CancellationToken.None);
                AddEvent("CSV 速度模式：零速使能请求已发送");
            }
            else
            {
                if (!runtime.CanWrite) throw new InvalidOperationException("当前连接未开放 CST 电子负载");
                if (!Confirm("确认当前负载指令为零、护罩和独立急停有效，进入 CST 电子负载模式？")) return;
                runtime.Enable();
                AddEvent("CST 电子负载模式：零转矩使能请求已发送");
            }
        });
        SelectedModeDisableCommand = Command(async () =>
        {
            var runtime = Runtime();
            if (ControlModeIndex == 0 && runtime.CanControlVelocity)
                await runtime.StopVelocityAsync(true, CancellationToken.None);
            else
                await runtime.StopAsync(true, CancellationToken.None);
            AddEvent($"{ControlModes[Math.Clamp(ControlModeIndex, 0, ControlModes.Length - 1)]}：已请求回零并失能");
        });
        DirectEnableCommand = Command(async () =>
        {
            var runtime = Runtime();
            if (!runtime.CanControlVelocity) throw new InvalidOperationException("请先选择 EtherCAT 直连并连接");
            await runtime.EnableVelocityAsync(CancellationToken.None);
            AddEvent("EtherCAT CSV 零速使能请求已发送");
        });
        DirectRunCommand = Command(async () =>
        {
            var runtime = Runtime();
            if (!runtime.CanControlVelocity) throw new InvalidOperationException("请先选择 EtherCAT 直连并连接");
            if (ControlModeIndex != 0) throw new InvalidOperationException("请先选择 CSV 速度模式");
            var rpm = Number(DirectRpmText);
            var ramp = Number(DirectRampRpmPerSecText);
            if (!runtime.Latest.ServoOn)
            {
                await runtime.EnableVelocityAsync(CancellationToken.None);
                AddEvent("CSV 运行：已自动发送零速使能，等待 Servo ON");
                var watch = System.Diagnostics.Stopwatch.StartNew();
                while (!runtime.Latest.ServoOn)
                {
                    if (!runtime.Latest.Connected || runtime.Latest.ErrorCode != 0 || runtime.Latest.Interlocks != Interlock.None)
                        throw new InvalidOperationException("CSV 自动使能失败：驱动器或联锁状态异常");
                    if (watch.Elapsed.TotalSeconds >= 5)
                        throw new TimeoutException("CSV 自动使能超时，驱动器未进入 Servo ON；请检查状态字和 EtherCAT 同步");
                    await Task.Delay(30);
                }
            }
            if (!Confirm($"确认以 {ramp:F0} rpm/s 斜坡运行到 {rpm:F0} rpm？")) return;
            await runtime.SetVelocityAsync(rpm, ramp, CancellationToken.None);
            AddEvent($"EtherCAT CSV 目标转速：{rpm:F0} rpm");
        });
        DirectHaltCommand = Command(async () =>
        {
            var runtime = Runtime();
            if (!runtime.CanControlVelocity) throw new InvalidOperationException("当前不是 EtherCAT 直连");
            await runtime.StopVelocityAsync(false, CancellationToken.None);
            AddEvent("EtherCAT CSV 已按最近一次运行斜坡请求减速停止，保持使能");
        });
        DirectStopCommand = Command(async () =>
        {
            var runtime = Runtime();
            if (!runtime.CanControlVelocity) throw new InvalidOperationException("当前不是 EtherCAT 直连");
            await runtime.StopVelocityAsync(true, CancellationToken.None);
            AddEvent("EtherCAT CSV 已按最近一次运行斜坡请求减速至零并失能");
        });
        LoadCommand = Command(async () =>
        {
            if (_orchestrator?.Running == true) throw new InvalidOperationException("自动配方运行中禁止手动加载");
            if (ControlModeIndex != 1) throw new InvalidOperationException("请先选择 CST 电子负载模式");
            var torque = Number(TorqueText);
            var ramp = Number(RampText);
            var power = Number(PowerText);
            var mode = (LoadMode)LoadModeIndex;
            var prompt = mode == LoadMode.LoadedStart
                ? $"确认 DUT 将按已核实方向启动；检测到持续正向转速后加载 {torque:F2} N·m？"
                : "确认按当前限值开始加载？";
            if (!Confirm(prompt)) return;
            if (_mock != null) _mock.SetSpeed(Number(SimRpmText));
            var runtime = Runtime();
            if (!runtime.Latest.ServoOn)
            {
                runtime.Enable();
                AddEvent("CST 加载：已自动发送零转矩使能，等待 Servo ON");
                var watch = System.Diagnostics.Stopwatch.StartNew();
                while (!runtime.Latest.ServoOn)
                {
                    if (!runtime.Latest.Connected || runtime.Latest.ErrorCode != 0 || runtime.Latest.Interlocks != Interlock.None)
                        throw new InvalidOperationException("CST 自动使能失败：驱动器或联锁状态异常");
                    if (watch.Elapsed.TotalSeconds >= 5)
                        throw new TimeoutException("CST 自动使能超时：驱动器未进入 Servo ON");
                    await Task.Delay(30);
                }
            }
            runtime.Load(torque, ramp, mode, power);
            AddEvent(mode == LoadMode.LoadedStart
                ? $"CST 带载启动已待命：转速确认后加载 {torque:F3} N·m"
                : $"CST 加载请求：{torque:F3} N·m");
        });
        StopCommand = Command(async () => { Runtime().RequestStop(); await CancelRecipeAsync(); await Runtime().StopAsync(false, CancellationToken.None); });
        StartDutCommand = Command(async () =>
        {
            var runtime = _dutRuntime ?? throw new InvalidOperationException("请先连接 VESC/M1。");
            if (!runtime.CanControlDut) throw new InvalidOperationException("VESC/M1 控制未启用或未连接；请配置独立串口并关闭 VESC Tool。");
            var value = Number(DutCommandValueText);
            var mode = DutControlModeIndex switch
            {
                0 => DutControlMode.SpeedRpm,
                1 => DutControlMode.MotorCurrentA,
                2 => DutControlMode.PositionDegrees,
                3 => DutControlMode.DutyCycle,
                _ => throw new InvalidOperationException("未知的 VESC 控制方式。")
            };
            await runtime.SetDutAsync(mode, value, CancellationToken.None);
            AddEvent($"M1/VESC 直接指令：{DutControlModes[DutControlModeIndex]} = {value:G}");
        });
        StopDutCommand = Command(async () =>
        {
            if (_dutRuntime == null) return;
            await _dutRuntime.StopDutAsync(CancellationToken.None);
            _dutCommandMode = null;
            AddEvent("M1/VESC 已发送零电流停机指令");
        });
        RefreshDutPortsCommand = Command(() => { RefreshDutPorts(); return Task.CompletedTask; });
        ConnectDutCommand = Command(async () =>
        {
            RefreshDutPorts();
            var port = SelectedDutPort ?? throw new InvalidOperationException("请先在下拉框中选择 VESC 串口。");
            if (_sensorRuntime != null && string.Equals(port.PortName, SelectedSensorPort?.PortName, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException($"{port.PortName} 正被 DYN-200 使用，请先断开扭矩传感器或选择其他串口。");
            if (_dutRuntime == null)
            {
                var moduleConfig = _config with { Vesc = _config.Vesc with { Enabled = false } };
                var vescBridge = new VescBridge(new MockRealtimeBridge(moduleConfig), moduleConfig.Vesc);
                vescBridge.Diagnostic += RecordVescDiagnostic;
                IRealtimeBridge moduleBridge = vescBridge;
                _dutRuntime = new BenchRuntime(moduleBridge, moduleConfig, new NullSessionRecorder());
                await _dutRuntime.StartAsync(CancellationToken.None);
            }
            try
            {
                _dutScanStatus = $"正在连接 · {port.DisplayName}";
                PropertyChanged?.Invoke(this, new(nameof(DutControllerStatusDisplay)));
                AddEvent($"VESC/M1：连接用户选择的 {port.DisplayName}");
                // VescBridge 内部按 DTR 状态与波特率做多轮探测（约 2s/轮），
                // 总预算必须覆盖全部探测轮次，否则会在中途被取消并误报超时。
                using var timeout = new CancellationTokenSource(TimeSpan.FromMilliseconds(8000));
                await _dutRuntime.ConnectDutAsync(port.PortName, timeout.Token);
                _connectedDutPortName = port.PortName;
                AddEvent($"VESC/M1 已连接：{port.DisplayName} / {_config.Vesc.BaudRate}");
            }
            catch (Exception ex)
            {
                throw new IOException($"连接所选串口 {port.DisplayName} 失败。", ex);
            }
            finally
            {
                _dutScanStatus = null;
                PropertyChanged?.Invoke(this, new(nameof(DutControllerStatusDisplay)));
            }
        });
        DisconnectDutCommand = Command(async () =>
        {
            _dutRuntime?.RequireManualAccess();
            if (_dutRuntime != null) { await _dutRuntime.DisposeAsync(); _dutRuntime = null; }
            _connectedDutPortName = null;
            AddEvent("VESC/M1 已断开");
        });
        DutDutyCommand = Command(() => SendDutDirectAsync(DutControlMode.DutyCycle, DutDutyText, "占空比"));
        DutSpeedCommand = Command(() => SendDutDirectAsync(DutControlMode.SpeedRpm, DutSpeedCommandText, "转速 RPM"));
        DutCurrentCommand = Command(() => SendDutDirectAsync(DutControlMode.MotorCurrentA, DutCurrentText, "相电流 Iq"));
        DutPositionCommand = Command(() => SendDutDirectAsync(DutControlMode.PositionDegrees, DutPositionText, "位置"));
        AdjustDutValueCommand = new AsyncCommand(p => { AdjustDutValue(p?.ToString()); return Task.CompletedTask; });
        RefreshSensorPortsCommand = Command(() => { RefreshSensorPorts(); return Task.CompletedTask; });
        ConnectSensorCommand = Command(async () =>
        {
            var port = SelectedSensorPort?.PortName;
            if (string.IsNullOrWhiteSpace(port)) throw new InvalidOperationException("请先选择 DYN-200 串口。");
            if (_dutRuntime?.CanControlDut == true && string.Equals(port, SelectedDutPort?.PortName, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException($"{port} 已被 VESC/M1 使用。");
            if (_sensorRuntime != null) { await _sensorRuntime.DisposeAsync(); _sensorRuntime = null; }
            var sensor = _config.TorqueSensor with { Enabled = true, PortName = port };
            var moduleConfig = _config with { TorqueSensor = sensor };
            IRealtimeBridge moduleBridge = new TorqueSensorBridge(new MockRealtimeBridge(moduleConfig), sensor);
            _sensorRuntime = new BenchRuntime(moduleBridge, moduleConfig, new NullSessionRecorder());
            try { await _sensorRuntime.StartAsync(CancellationToken.None); }
            catch { await _sensorRuntime.DisposeAsync(); _sensorRuntime = null; throw; }
            AddEvent($"DYN-200 已连接：{port} / {sensor.BaudRate}");
        });
        DisconnectSensorCommand = Command(async () =>
        {
            _sensorRuntime?.RequireManualAccess();
            if (_sensorRuntime != null) { await _sensorRuntime.DisposeAsync(); _sensorRuntime = null; }
            AddEvent("DYN-200 已断开");
        });
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
            if (dlg.ShowDialog() == true) { RecipeJson = await File.ReadAllTextAsync(dlg.FileName); TestResults.Clear(); PropertyChanged?.Invoke(this, new(nameof(RecipeJson))); Refresh(); }
        });
        GenerateCurveCommand = Command(() =>
        {
            if (_orchestrator?.Running == true) throw new InvalidOperationException("先停止当前配方");
            var plan = new TorqueCurvePlan(Number(CurveStartRpmText), Number(CurveEndRpmText), Number(CurveRpmStepText),
                Number(CurveStartTorqueText), Number(CurveEndTorqueText), Number(CurveTorqueStepText));
            var recipe = TorqueCurveRecipeFactory.Create(plan, _config);
            RecipeJson = JsonSerializer.Serialize(recipe, BenchConfig.Json);
            TestResults.Clear();
            PropertyChanged?.Invoke(this, new(nameof(RecipeJson)));
            AddEvent($"已生成安全受限扭矩曲线：{recipe.Points.Count} 点");
            return Task.CompletedTask;
        });
        SaveRecipeCommand = Command(async () =>
        {
            if (_orchestrator?.Running == true) throw new InvalidOperationException("自动测试期间配方已锁定");
            var recipe = ParseRecipe();
            var dlg = new SaveFileDialog { Filter = "测试配方 (*.json)|*.json", FileName = "recipe.json" };
            if (dlg.ShowDialog() == true) await File.WriteAllTextAsync(dlg.FileName, JsonSerializer.Serialize(recipe, BenchConfig.Json));
        });
        ValidateRecipeCommand = Command(() => { var r = ParseRecipe(); AddEvent($"配方有效：{r.Points.Count} 点"); return Task.CompletedTask; });
        RunRecipeCommand = Command(async () =>
        {
            var runtime = Runtime(); var recipe = ParseRecipe();
            _testMotorModel = MotorModel.Trim();
            if (string.IsNullOrWhiteSpace(_testMotorModel)) throw new InvalidOperationException("请先输入电机型号，再开始性能测试。");
            AddEvent($"AUTO_TEST 电机型号：{_testMotorModel}");
            if (_recipeTask is { IsCompleted: false }) throw new InvalidOperationException("配方正在运行");
            var precheck = RunPrecheck(); PrecheckSummary = precheck;
            if (!precheck.StartsWith("PASS", StringComparison.Ordinal)) throw new InvalidOperationException(precheck);
            if (!Confirm($"确认启动电机参数自动测量？\n配方: {recipe.Name}")) return;
            var testStarted = DateTime.Now;
            _testFileTag = SafeFileName(_testMotorModel);
            var sessionsRoot = Path.GetFullPath(_config.DataDirectory, _root);
            _testArchiveDirectory = Path.Combine(sessionsRoot,
                $"{testStarted:yyyyMMdd_HHmmss_fff}_{_testFileTag}");
            Directory.CreateDirectory(_testArchiveDirectory);
            AddEvent($"本次测试日志目录：{_testArchiveDirectory}");
            if (!runtime.Latest.ServoOn)
            {
                runtime.Enable();
                var enableDeadline = DateTimeOffset.UtcNow.AddSeconds(5);
                while (!runtime.Latest.ServoOn)
                {
                    if (DateTimeOffset.UtcNow >= enableDeadline) throw new TimeoutException("Servo ON 超时");
                    await Task.Delay(30);
                }
            }
            _recipeCts?.Dispose(); _recipeCts = new();
            TestResults.Clear();
            await File.WriteAllTextAsync(Path.Combine(_recorder!.DirectoryPath, "recipe_" + Guid.NewGuid().ToString("N") + ".json"), JsonSerializer.Serialize(recipe, BenchConfig.Json));
            _orchestrator = new(runtime, _config, _recorder);
            _orchestrator.PointCompleted += result => System.Windows.Application.Current.Dispatcher.Invoke(() => OnAutoTestPointCompleted(result));
            _orchestrator.StatusChanged += () => System.Windows.Application.Current.Dispatcher.Invoke(Refresh);
            runtime.AcquireAutoTest();
            _dutRuntime?.AcquireAutoTest();
            _sensorRuntime?.AcquireAutoTest();
            _recipeTask = _orchestrator.RunAsync(recipe, _recipeCts.Token,
                precheck: ct => RunAutomaticPrecheckAsync(ct),
                preparePoint: async (p, ct) =>
                {
                    _autoTestIqCommand = 0;
                    _autoTestDutyCommand = 0;
                    if (_mock != null) _mock.SetPerformanceDrive(_config.AutoTest.SimulationNoLoadRpm);
                    if (_dutRuntime?.CanControlDut == true)
                    {
                        var target = _config.AutoTest.DutDriveValue;
                        var interval = TimeSpan.FromMilliseconds(50);
                        var step = _config.AutoTest.DutDutyRampPerSecond * interval.TotalSeconds;
                        for (var duty = 0.0; duty < target; duty = Math.Min(target, duty + step))
                        {
                            _autoTestDutyCommand = Math.Min(target, duty + step);
                            await _dutRuntime.SetDutAsync(DutControlMode.DutyCycle, _autoTestDutyCommand, ct, ControlOwner.AutoTest);
                            EnsureDutRampSafety();
                            await Task.Delay(interval, ct);
                        }
                        AddEvent($"AUTO_TEST 固定 Drive Condition：Duty={_autoTestDutyCommand:P0}；RPM 仅测量、不闭环控制");
                    }
                },
                stopDut: async (coordinatedRampSeconds, ct) =>
                {
                    if (_mock != null) _mock.SetSpeed(0);
                    if (_dutRuntime != null)
                    {
                        if (coordinatedRampSeconds == 0)
                        {
                            _autoTestDutyCommand = 0;
                            await _dutRuntime.StopDutAsync(ct);
                            return;
                        }
                        if (_orchestrator?.EndReason == "COMPLETED_STALL_200_RPM")
                        {
                            _autoTestDutyCommand = 0;
                            await _dutRuntime.SetDutAsync(DutControlMode.DutyCycle, 0, ct, ControlOwner.AutoTest);
                            await _dutRuntime.StopDutAsync(ct);
                            return;
                        }
                        if (_orchestrator?.EndReason == "COMPLETED_STALL_CROSSING")
                        {
                            _autoTestDutyCommand = 0;
                            try { await _dutRuntime.SetDutAsync(DutControlMode.DutyCycle, 0, ct, ControlOwner.AutoTest); }
                            catch { /* StopDutAsync below bypasses the healthy-state gate and repeats zero-current frames. */ }
                            await _dutRuntime.StopDutAsync(ct);
                            return;
                        }
                        var interval = TimeSpan.FromMilliseconds(50);
                        var rampPerSecond = coordinatedRampSeconds is > 0
                            ? Math.Max(.01, _autoTestDutyCommand / coordinatedRampSeconds.Value)
                            : _config.AutoTest.DutDutyRampPerSecond;
                        var decrement = rampPerSecond * interval.TotalSeconds;
                        for (var duty = Math.Max(0, _autoTestDutyCommand - decrement); duty > 0; duty = Math.Max(0, duty - decrement))
                        {
                            await _dutRuntime.SetDutAsync(DutControlMode.DutyCycle, duty, ct, ControlOwner.AutoTest);
                            await Task.Delay(interval, ct);
                        }
                        _autoTestDutyCommand = 0;
                        await _dutRuntime.SetDutAsync(DutControlMode.DutyCycle, 0, ct, ControlOwner.AutoTest);
                        await _dutRuntime.StopDutAsync(ct);
                    }
                },
                analyze: async ct => await ExportAsync(),
                ensureExternalSafety: EnsureExternalTelemetryHealthy);
            try { await _recipeTask; AddEvent("配方完成"); }
            finally
            {
                AddAutoTestStopEvent(_orchestrator);
                runtime.RequestStop();
                try { await runtime.StopAsync(false, CancellationToken.None); } catch { }
                if (_dutRuntime != null) try { await _dutRuntime.StopDutAsync(CancellationToken.None); } catch { }
                runtime.ReleaseAutoTest(); _dutRuntime?.ReleaseAutoTest(); _sensorRuntime?.ReleaseAutoTest();
                await RestoreAutoTestIdleAfterStopAsync();
                try { await ExportAsync(); }
                catch (Exception ex) { AddEvent("自动生成测试日志失败：" + ex.Message); }
            }
        });
        PrecheckCommand = Command(() => { PrecheckSummary = RunPrecheck(); AddEvent("预检查：" + PrecheckSummary); Refresh(); return Task.CompletedTask; });
        ExportCommand = Command(ExportAsync);
    }
    private async Task RestoreAutoTestIdleAfterStopAsync()
    {
        if (_orchestrator == null) return;
        var deadline = DateTimeOffset.UtcNow.AddSeconds(_config.AutoTest.StopConfirmTimeoutSeconds);
        while (DateTimeOffset.UtcNow < deadline)
        {
            var s = Snapshot;
            if (s != null && Math.Abs(s.DutSpeedRpm ?? double.MaxValue) <= _config.AutoTest.ZeroRpmTolerance &&
                Math.Abs(s.ActualTorqueNm) <= _config.Limits.ZeroTorqueNm)
            {
                _orchestrator.ReturnToIdleAfterConfirmedStop();
                foreach (var key in _precheckStates.Keys.ToArray()) _precheckStates[key] = "WAITING";
                PrecheckSummary = "等待检查";
                AddEvent($"AUTO_TEST 已确认安全停止并恢复待机；上次结束原因：{_orchestrator.EndReason}");
                Refresh();
                return;
            }
            await Task.Delay(50);
        }
        AddEvent("AUTO_TEST 未确认 DUT/Servo 完全归零，保持故障状态；请检查设备后执行预检查");
        Refresh();
    }
    private void EnsureDutRampSafety()
    {
        var s = Snapshot ?? throw new SafetyTripException("DUT 启动期间无 Telemetry");
        if (Math.Abs(s.DutSpeedRpm ?? 0) >= _config.Limits.MaxSpeedRpm) throw new SafetyTripException("DUT 启动期间触发 Overspeed");
        if (Math.Abs(s.DutCurrentA ?? 0) >= _config.Limits.MaxDutBusCurrentA) throw new SafetyTripException("DUT 启动期间 DC Bus Current 达到硬限制");
        if (Math.Abs((s.DutBusV ?? 0) * (s.DutCurrentA ?? 0)) >= _config.Limits.MaxPowerW) throw new SafetyTripException("DUT 启动期间输入功率达到硬限制");
        if (s.DutMotorTempC >= _config.Limits.DutMotorTripC) throw new SafetyTripException("DUT 启动期间温度达到硬限制");
    }
    private void AddAutoTestStopEvent(TestOrchestrator? orchestrator)
    {
        if (orchestrator?.StopSnapshot is not { } s) return;
        var torque = s.ExternalTorqueNm ?? s.ActualTorqueNm;
        var rpm = s.DutSpeedRpm ?? s.SpeedRpm;
        var ibus = s.DutCurrentA;
        var iphase = s.DutPhaseCurrentA;
        double? pin = s.DutBusV is { } v && ibus is { } i ? Math.Abs(v * i) : null;
        AddEvent($"TEST STOP | Reason={orchestrator.EndReason} | Torque={torque:F3} N·m | Speed={rpm:F1} RPM | DC Bus Current={ibus:F2} A | Phase Current={iphase:F2} A | Bus Power={pin:F1} W" +
            (string.IsNullOrWhiteSpace(orchestrator.StopDetail) ? "" : $" | Detail={orchestrator.StopDetail}"));
        AddEvent("BOTTLENECK | " + AnalyzeBottleneck(s));
    }
    private void OnAutoTestPointCompleted(PointResult r)
    {
        TestResults.Add(r);
        AddEvent($"POINT {r.PointId} | Servo target/actual={r.ServoTargetTorque?.Mean:F3}/{r.ServoActualTorque?.Mean:F3} N·m | DYN={r.Torque?.Mean:F3} N·m | RPM={r.Speed?.Mean:F1} | Iq cmd/actual={r.IqCommand?.Mean:F1}/{r.IqActual?.Mean:F1} A | Vbus={r.Vbus?.Mean:F1} V | Ibus={r.DcBusCurrent?.Mean:F2} A | Pin={r.InputPower?.Mean:F1} W | Pout={r.MechanicalPower?.Mean:F1} W | Duty={r.Duty?.Mean:F1}% | Motor/FET Temp={r.Temperature?.Mean:F1}/{r.ControllerTemperature?.Mean:F1}°C | VESC Fault={r.DutFaultCode ?? 0}");
    }
    private string AnalyzeBottleneck(BenchSnapshot s)
    {
        var iqCommand = Math.Abs(s.DutIqCommandA ?? _autoTestIqCommand);
        var iqActual = Math.Abs(s.DutPhaseCurrentA ?? 0);
        var servoTarget = Math.Abs(s.TargetTorqueNm);
        var servoActual = Math.Abs(s.ActualTorqueNm);
        var dyn = Math.Abs(s.ExternalTorqueNm ?? 0);
        var ibus = Math.Abs(s.DutCurrentA ?? 0);
        var pin = Math.Abs((s.DutBusV ?? 0) * (s.DutCurrentA ?? 0));
        var duty = Math.Abs(s.DutDutyCyclePct ?? 0);
        if (s.DutMotorTempC >= _config.Limits.DutMotorTripC * .95) return $"DUT_THERMAL，温度 {s.DutMotorTempC:F1}/{_config.Limits.DutMotorTripC:F1}°C";
        if (ibus >= _config.Limits.MaxDutBusCurrentA * .95) return $"DC_CURRENT_LIMIT，Ibus {ibus:F2}/{_config.Limits.MaxDutBusCurrentA:F1} A";
        if (pin >= _config.Limits.MaxPowerW * .95) return $"INPUT_POWER_LIMIT，Pin {pin:F1}/{_config.Limits.MaxPowerW:F0} W";
        if (duty >= _config.Vesc.MaxDutyCycle * 100 * .95 && iqActual < iqCommand * .9) return $"DUT_VOLTAGE_DUTY_SATURATION，Duty {duty:F1}% 且 Iq {iqActual:F1}/{iqCommand:F1} A";
        if (iqCommand > 1 && iqActual < iqCommand * .9) return $"DUT_CURRENT_NOT_REACHED，Iq {iqActual:F1}/{iqCommand:F1} A";
        if (servoTarget > .05 && servoActual < servoTarget * .9) return $"SERVO_TORQUE_NOT_REACHED，Servo {servoActual:F3}/{servoTarget:F3} N·m";
        if (servoActual > .05 && dyn < servoActual * .8) return $"MECHANICAL_OR_DYN_MISMATCH，DYN {dyn:F3} / Servo {servoActual:F3} N·m";
        return $"NO_SINGLE_LIMIT_IDENTIFIED，Servo={servoActual:F3} N·m，DYN={dyn:F3} N·m，Iq={iqActual:F1} A，Ibus={ibus:F2} A，Duty={duty:F1}%";
    }
    private void AdjustDutValue(string? action)
    {
        switch (action)
        {
            case "DutyUp": DutDutyText = (Number(DutDutyText) + .01).ToString("F2", CultureInfo.InvariantCulture); PropertyChanged?.Invoke(this, new(nameof(DutDutyText))); break;
            case "DutyDown": DutDutyText = (Number(DutDutyText) - .01).ToString("F2", CultureInfo.InvariantCulture); PropertyChanged?.Invoke(this, new(nameof(DutDutyText))); break;
            case "SpeedUp": DutSpeedCommandText = (Number(DutSpeedCommandText) + 100).ToString("F0", CultureInfo.InvariantCulture); PropertyChanged?.Invoke(this, new(nameof(DutSpeedCommandText))); break;
            case "SpeedDown": DutSpeedCommandText = (Number(DutSpeedCommandText) - 100).ToString("F0", CultureInfo.InvariantCulture); PropertyChanged?.Invoke(this, new(nameof(DutSpeedCommandText))); break;
            case "CurrentUp": DutCurrentText = (Number(DutCurrentText) + .1).ToString("F2", CultureInfo.InvariantCulture); PropertyChanged?.Invoke(this, new(nameof(DutCurrentText))); break;
            case "CurrentDown": DutCurrentText = (Number(DutCurrentText) - .1).ToString("F2", CultureInfo.InvariantCulture); PropertyChanged?.Invoke(this, new(nameof(DutCurrentText))); break;
            case "PositionUp": DutPositionText = (Number(DutPositionText) + 1).ToString("F2", CultureInfo.InvariantCulture); PropertyChanged?.Invoke(this, new(nameof(DutPositionText))); break;
            case "PositionDown": DutPositionText = (Number(DutPositionText) - 1).ToString("F2", CultureInfo.InvariantCulture); PropertyChanged?.Invoke(this, new(nameof(DutPositionText))); break;
        }
    }
    private async Task SendDutDirectAsync(DutControlMode mode, string text, string label)
    {
        var runtime = _dutRuntime ?? throw new InvalidOperationException("请先连接 VESC/M1。");
        if (!runtime.CanControlDut) throw new InvalidOperationException("VESC/M1 通信未就绪或遥测已超时。");
        var value = Number(text);
        if (mode == DutControlMode.MotorCurrentA && Math.Abs(value) >= 10 &&
            !Confirm($"高电流指令 {value:F1} A 会在轻载时快速加速电机。确认机械区域已清空、硬件急停有效并继续？")) return;
        await runtime.SetDutAsync(mode, value, CancellationToken.None);
        _dutCommandMode = mode; _dutCommandValue = value;
        PropertyChanged?.Invoke(this, new(nameof(DutCommandStatusDisplay)));
        AddEvent($"M1/VESC {label}指令：{value:G}");
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
    private Recipe? TryRecipe()
    {
        try { return JsonSerializer.Deserialize<Recipe>(RecipeJson, BenchConfig.Json); }
        catch { return null; }
    }
    private string RunPrecheck()
    {
        var failures = new List<string>();
        var warnings = new List<string>();
        foreach (var key in _precheckStates.Keys.ToArray()) _precheckStates[key] = "CURRENT";
        var dut = _dutRuntime?.Latest;
        if (_runtime?.IsSimulation != true && (_dutRuntime?.CanControlDut != true || dut is not { Connected: true })) { failures.Add("VESC 未连接"); _precheckStates["VESC"] = "FAIL"; }
        else if (dut is { ErrorCode: not 0 } || (_runtime?.IsSimulation != true && (dut is not { Timestamp: var dutTimestamp } || dutTimestamp == default || DateTimeOffset.UtcNow - dutTimestamp > TimeSpan.FromMilliseconds(_config.AutoTest.TelemetryTimeoutMs)))) { failures.Add("VESC Fault 或 Telemetry 超时"); _precheckStates["VESC"] = "FAIL"; }
        else _precheckStates["VESC"] = "PASS";
        if (_runtime?.Latest is not { Connected: true, EtherCatOnline: true, DriveReady: true } servo) { failures.Add("Servo 未达到 EtherCAT OP/SYNC"); _precheckStates["Servo"] = "FAIL"; }
        else
        {
            if (servo.ErrorCode != 0 || servo.Interlocks != Interlock.None) failures.Add("Servo 有故障或联锁");
            if (Math.Abs(servo.TargetTorqueNm) > _config.Limits.ZeroTorqueNm) failures.Add("目标扭矩未归零");
            _precheckStates["Servo"] = failures.Any(x => x.StartsWith("Servo") || x.Contains("目标扭矩")) ? "FAIL" : "PASS";
        }
        var sensor = _sensorRuntime?.Latest ?? (_runtime?.IsSimulation == true ? _runtime.Latest : null);
        if (sensor is not { Connected: true, ExternalHealthy: true } || DateTimeOffset.UtcNow - sensor.Timestamp > TimeSpan.FromMilliseconds(_config.AutoTest.TelemetryTimeoutMs)) { failures.Add("DYN-200 未连接、CRC 异常或数据未持续更新"); _precheckStates["DYN"] = "FAIL"; }
        else _precheckStates["DYN"] = "PASS";
        var merged = Snapshot;
        if (merged == null || Math.Abs(merged.DutSpeedRpm ?? double.MaxValue) > _config.AutoTest.ZeroRpmTolerance || Math.Abs(merged.ActualTorqueNm) > _config.Limits.ZeroTorqueNm || Math.Abs(merged.ExternalTorqueNm ?? double.MaxValue) > _config.AutoTest.DynZeroTorqueToleranceNm) { failures.Add($"Zero 检查未通过：DUT RPM、Servo Torque 或 DYN Torque 未归零（DYN 容差 ±{_config.AutoTest.DynZeroTorqueToleranceNm:F2} N·m）"); _precheckStates["Zero"] = "FAIL"; }
        else _precheckStates["Zero"] = "PASS";
        try
        {
            var recipe = ParseRecipe();
            if (_config.Limits.MaxSpeedRpm < _config.AutoTest.NominalMaxSpeedRpm || _config.Limits.MaxDutBusCurrentA <= 0 || _config.Limits.MaxPowerW <= 0 || _config.Limits.MaxTorqueNm < _config.AutoTest.TorqueRangeMaxNm || _config.AutoTest.TorqueStepNm <= 0 || _config.AutoTest.TorqueRampRateNmPerSec <= 0 || _config.AutoTest.StableTimeoutSeconds <= 0 || _config.AutoTest.TelemetryTimeoutMs <= 0 || recipe.Points.Any(p => p.RampNmPerSec <= 0)) throw new InvalidDataException("上限、扭矩步长、斜坡或超时参数非法");
            _precheckStates["Config"] = "PASS";
        }
        catch (Exception ex) { failures.Add("Config 无效：" + ex.Message); _precheckStates["Config"] = "FAIL"; }
        Refresh();
        return failures.Count == 0 ? "PASS · " + (warnings.Count == 0 ? "五项预检查通过" : string.Join("；", warnings)) : "FAIL · " + string.Join("；", failures) + "。建议：先回零并恢复通信/清除故障后重试";
    }
    private async Task RunAutomaticPrecheckAsync(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var snapshot = Snapshot;
        if (_sensorRuntime?.CanZeroExternalTorque == true && snapshot != null &&
            Math.Abs(snapshot.DutSpeedRpm ?? double.MaxValue) <= _config.AutoTest.ZeroRpmTolerance &&
            Math.Abs(snapshot.ActualTorqueNm) <= _config.Limits.ZeroTorqueNm &&
            Math.Abs(snapshot.ExternalTorqueNm ?? 0) > _config.AutoTest.DynZeroTorqueToleranceNm)
        {
            await _sensorRuntime.ZeroExternalTorqueAsync(ct);
            AddEvent($"AUTO_TEST DYN 软件清零：原始零偏 {snapshot.ExternalTorqueNm:F3} N·m");
            await Task.Delay(150, ct);
        }
        var result = RunPrecheck();
        if (!result.StartsWith("PASS", StringComparison.Ordinal)) throw new SafetyTripException(result);
    }
    private void EnsureExternalTelemetryHealthy()
    {
        if (_runtime?.IsSimulation == true) return;
        var now = DateTimeOffset.UtcNow;
        var servo = _runtime?.Latest;
        var dutSnapshot = _dutRuntime?.Latest;
        var dynSnapshot = _sensorRuntime?.Latest;
        if ((_orchestrator?.TargetTorqueNm ?? 0) >= 2.0 && now - _lastLinkTraceAt >= TimeSpan.FromMilliseconds(50))
        {
            static string Age(DateTimeOffset nowValue, BenchSnapshot? snapshot) => snapshot == null || snapshot.Timestamp == default
                ? "missing" : (nowValue - snapshot.Timestamp).TotalMilliseconds.ToString("F1", CultureInfo.InvariantCulture);
            _recorder?.Event("link_age_trace", $"target={_orchestrator?.TargetTorqueNm:F3}; servoAgeMs={Age(now, servo)}; " +
                $"vescAgeMs={Age(now, dutSnapshot)}; dynAgeMs={Age(now, dynSnapshot)}; " +
                $"vescConnected={dutSnapshot?.Connected}; dynConnected={dynSnapshot?.Connected}; dynHealthy={dynSnapshot?.ExternalHealthy}");
            _lastLinkTraceAt = now;
        }
        if (dutSnapshot is not { Connected: true } dut || now - dut.Timestamp > TimeSpan.FromMilliseconds(_config.AutoTest.TelemetryTimeoutMs)) throw new SafetyTripException("VESC Telemetry age 超限");
        if (dynSnapshot is not { Connected: true, ExternalHealthy: true } dyn || now - dyn.Timestamp > TimeSpan.FromMilliseconds(_config.AutoTest.TelemetryTimeoutMs)) throw new SafetyTripException("DYN-200 Telemetry age/CRC 超限");
    }
    private string PrecheckColor(string name) => _precheckStates[name] switch { "CURRENT" => "#29C3E6", "PASS" => "#24D18F", "WARNING" => "#F4C542", "FAIL" => "#F04B36", _ => "#60798A" };
    private BenchSnapshot MergeModuleSnapshots(BenchSnapshot value)
    {
        if (_dutRuntime?.Latest is { } dut) value = value with { DutBusV = dut.DutBusV, DutCurrentA = dut.DutCurrentA,
            DutPhaseCurrentA = dut.DutPhaseCurrentA, DutIqCommandA = dut.DutIqCommandA, DutDutyCyclePct = dut.DutDutyCyclePct,
            DutMotorTempC = dut.DutMotorTempC, DutControllerTempC = dut.DutControllerTempC, DutFaultCode = dut.DutFaultCode, DutSpeedRpm = dut.DutSpeedRpm };
        if (_sensorRuntime?.Latest is { } sensor) value = value with { ExternalHealthy = sensor.ExternalHealthy,
            ExternalTorqueNm = sensor.ExternalTorqueNm, ExternalSpeedRpm = sensor.ExternalSpeedRpm };
        return value;
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
        var messages = new List<string>();
        for (Exception? current = ex; current != null; current = current.InnerException)
            if (!messages.Contains(current.Message, StringComparer.Ordinal)) messages.Add(current.Message);
        var detail = string.Join("\n", messages);
        AddEvent(detail);
        try { _recorder?.Event("ui_error", ex.ToString()); } catch (Exception) { _runtime?.RequestStop(true); }
        MessageBox.Show(detail, "Motor Load Bench", MessageBoxButton.OK, MessageBoxImage.Warning);
    }
    private void AddEvent(string message)
    {
        var singleLine = string.Join(" ", message.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
        if (singleLine.Length > 300) singleLine = singleLine[..297] + "...";
        Events.Insert(0, $"{DateTime.Now:HH:mm:ss.fff}  {singleLine}");
        while (Events.Count > 200) Events.RemoveAt(Events.Count - 1);
    }
    public async Task ConnectEtherCatReadOnlyAsync()
    {
        try { await ProbeEtherCatAsync("scan"); }
        catch (Exception ex) { ShowError(ex); }
        finally { Refresh(); }
    }
    private async Task ProbeEtherCatAsync(string action)
    {
        if (_busy || _runtime != null) throw new InvalidOperationException("请先断开当前会话，再进行 EtherCAT 诊断");
        _busy = true;
        if (action == "scan") _etherCatReadOnlyStatus = null;
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
            if (action == "scan")
            {
                using var report = JsonDocument.Parse(output);
                var slave = report.RootElement.GetProperty("slaves")[0].GetProperty("name").GetString() ?? "未知从站";
                _etherCatReadOnlyStatus = $"EtherCAT PRE-OP · {slave} · 只读扫描成功";
            }
            AddEvent($"EtherCAT {action} 诊断完成；完整报告已保存到 artifacts/ethercat/{action}.json");
            AddEvent("诊断只读取设备信息；尚未建立周期控制连接，Servo ON 和加载不可用");
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
            else if (ConnectionMode == 1) { _mock = null; bridge = new EtherCatDirectBridge(_config, _root); }
            else { _mock = null; bridge = new AdsRealtimeBridge(_config, Convert.ToUInt32(hash[..8], 16)); }
            var recorder = new SessionRecorder(Path.GetFullPath(_config.DataDirectory, _root), _config, bridge.IsSimulation, hash);
            var runtime = new BenchRuntime(bridge, _config, recorder);
            runtime.SnapshotOverlay = MergeModuleSnapshots;
            runtime.EmergencyStopDutAsync = ct => _dutRuntime?.StopDutAsync(ct) ??
                Task.FromException(new InvalidOperationException("DUT 紧急停机通道已断开"));
            try { await runtime.StartAsync(CancellationToken.None); }
            catch { await runtime.DisposeAsync(); throw; }
            _recorder = recorder; _runtime = runtime;
            RefreshDutPorts(); RefreshSensorPorts();
            AddEvent(ConnectionMode switch { 0 => "仿真连接成功", 1 => "EtherCAT 直连成功：OP/SYNC 已保持", _ => "ADS连接成功" });
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
        try
        {
            await DisconnectAsync();
            if (_dutRuntime != null) { await _dutRuntime.DisposeAsync(); _dutRuntime = null; }
            if (_sensorRuntime != null) { await _sensorRuntime.DisposeAsync(); _sensorRuntime = null; }
            return _runtime == null;
        }
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
        var recordedMotorModel = string.IsNullOrWhiteSpace(_testMotorModel) ? MotorModel.Trim() : _testMotorModel;
        var fileTag = _testFileTag ?? SafeFileName(recordedMotorModel);
        var directory = _testArchiveDirectory ?? Path.Combine(Path.GetFullPath(_config.DataDirectory, _root),
            $"{DateTime.Now:yyyyMMdd_HHmmss_fff}_{fileTag}");
        Directory.CreateDirectory(directory);
        var results = new List<PointResult>();
        foreach (var p in Directory.GetFiles(_recorder.DirectoryPath, "point_*.json"))
        {
            var r = JsonSerializer.Deserialize<PointResult>(await File.ReadAllTextAsync(p), BenchConfig.Json);
            if (r != null) results.Add(r);
        }
        results = results
            .OrderBy(r => r.PointId == "NO_LOAD" ? 0 : r.PointId == "STALL_FINAL" ? 2 : 1)
            .ThenBy(r => r.ServoTargetTorque?.Mean ?? r.Torque?.Mean ?? double.MaxValue)
            .ThenBy(r => r.Torque?.Mean ?? double.MaxValue)
            .ThenBy(r => r.PointId, StringComparer.Ordinal)
            .ToList();
        var summary = new { generatedUtc = DateTimeOffset.UtcNow, motorModel = recordedMotorModel, simulation = _runtime?.IsSimulation ?? ConnectionMode == 0, results };
        await File.WriteAllTextAsync(Path.Combine(directory, $"{fileTag}_summary.json"), JsonSerializer.Serialize(summary, BenchConfig.Json));
        var csv = new StringBuilder("motor_model,point_id,attempt,result,servo_target_torque_nm,servo_actual_torque_nm,dyn_torque_nm,dut_rpm,iq_command_a,iq_actual_a,vbus_v,ibus_a,pin_w,pout_w,efficiency_pct,duty_pct,motor_temperature_c,controller_temperature_c,vesc_fault_code,torque_source,raw_path\n");
        foreach (var r in results)
        {
            static string N(double? v) => v?.ToString("G17", CultureInfo.InvariantCulture) ?? "";
            static string Q(string s) => "\"" + s.Replace("\"", "\"\"") + "\"";
            csv.AppendLine(string.Join(',', Q(recordedMotorModel), Q(r.PointId), r.Attempt, r.Result, N(r.ServoTargetTorque?.Mean), N(r.ServoActualTorque?.Mean), N(r.Torque?.Mean), N(r.Speed?.Mean),
                N(r.IqCommand?.Mean), N(r.IqActual?.Mean), N(r.Vbus?.Mean), N(r.DcBusCurrent?.Mean), N(r.InputPower?.Mean),
                N(r.MechanicalPower?.Mean), N(r.Efficiency?.Mean), N(r.Duty?.Mean), N(r.Temperature?.Mean), N(r.ControllerTemperature?.Mean), r.DutFaultCode ?? 0, r.Source, Q(r.RawDataPath)));
        }
        await File.WriteAllTextAsync(Path.Combine(directory, $"{fileTag}_points.csv"), csv.ToString(), new UTF8Encoding(true));
        await File.WriteAllTextAsync(Path.Combine(directory, $"{fileTag}_motor_test_log.csv"), csv.ToString(), new UTF8Encoding(true));
        var sessionsRoot = Directory.GetParent(directory)?.FullName;
        if (sessionsRoot != null)
        {
            var comparison = new StringBuilder(csv.ToString().Split('\n', 2)[0].TrimEnd('\r')).AppendLine();
            foreach (var log in Directory.GetFiles(sessionsRoot, "*_motor_test_log.csv", SearchOption.AllDirectories).OrderBy(path => path, StringComparer.OrdinalIgnoreCase))
            {
                var lines = await File.ReadAllLinesAsync(log);
                foreach (var line in lines.Skip(1)) if (!string.IsNullOrWhiteSpace(line)) comparison.AppendLine(line);
            }
            await File.WriteAllTextAsync(Path.Combine(sessionsRoot, "motor_comparison.csv"), comparison.ToString(), new UTF8Encoding(true));
        }
        var passed = results.Where(r => r.Result.StartsWith("PASS", StringComparison.Ordinal) && r.Torque != null && r.Speed != null).ToArray();
        var noLoad = passed.OrderBy(r => Math.Abs(r.Torque!.Mean)).FirstOrDefault();
        var maxEfficiency = passed.Where(r => r.Efficiency != null).OrderByDescending(r => r.Efficiency!.Mean).FirstOrDefault();
        var maxMechanicalPower = passed.Where(r => r.MechanicalPower != null).OrderByDescending(r => r.MechanicalPower!.Mean).FirstOrDefault();
        var maxTestedTorque = passed.OrderByDescending(r => r.Torque!.Mean).FirstOrDefault();
        var minimumValidRpm = passed.OrderBy(r => r.Speed!.Mean).FirstOrDefault();
        await File.WriteAllTextAsync(Path.Combine(directory, $"{fileTag}_metadata.json"), JsonSerializer.Serialize(new
        {
            motorModel = recordedMotorModel, recipe = TryRecipe()?.Name, generatedUtc = DateTimeOffset.UtcNow,
            endReason = _orchestrator?.EndReason ?? "manual", pointCount = results.Count,
            keyResults = new { noLoadPoint = noLoad?.PointId, maximumEfficiencyPoint = maxEfficiency?.PointId,
                maximumPowerPoint = maxMechanicalPower?.PointId, maximumTestedTorquePoint = maxTestedTorque?.PointId,
                minimumValidRpmPoint = minimumValidRpm?.PointId },
            config = _config
        }, BenchConfig.Json));
        var sourceRaw = Path.Combine(_recorder.DirectoryPath, "raw.csv");
        var sourceSamples = Path.Combine(_recorder.DirectoryPath, "samples.csv");
        if (File.Exists(sourceRaw)) File.Copy(sourceRaw, Path.Combine(directory, $"{fileTag}_raw.csv"), true);
        else if (File.Exists(sourceSamples)) File.Copy(sourceSamples, Path.Combine(directory, $"{fileTag}_raw.csv"), true);
        var sourceEvents = Path.Combine(_recorder.DirectoryPath, "events.jsonl");
        if (File.Exists(sourceEvents)) File.Copy(sourceEvents, Path.Combine(directory, $"{fileTag}_events.jsonl"), true);
        static string H(string? value) => System.Net.WebUtility.HtmlEncode(value ?? "");
        var rows = string.Join("", results.Select(r => $"<tr><td>{H(r.PointId)}</td><td>{H(r.Result)}</td><td>{r.Torque?.Mean:F3}</td><td>{r.Speed?.Mean:F1}</td><td>{r.DcBusCurrent?.Mean:F2}</td><td>{r.InputPower?.Mean:F1}</td><td>{r.MechanicalPower?.Mean:F1}</td><td>{r.Efficiency?.Mean:F1}</td><td>{r.IqCommand?.Mean:F1}</td><td>{r.IqActual?.Mean:F1}</td><td>{r.Vbus?.Mean:F1}</td><td>{r.Duty?.Mean:F1}</td><td>{r.Temperature?.Mean:F1}</td><td>{r.ControllerTemperature?.Mean:F1}</td><td>{r.DutFaultCode ?? 0}</td></tr>"));
        var html = $"<!doctype html><meta charset=utf-8><title>KAMINGO Motor Test</title><style>body{{font:14px 'Microsoft YaHei';margin:32px;color:#172b40}}table{{border-collapse:collapse;font-size:12px}}th,td{{border:1px solid #ccd6df;padding:6px 8px;white-space:nowrap}}th{{background:#173846;color:white}}</style><h1>电机性能测试报告</h1><p><b>电机型号:</b> {H(recordedMotorModel)}</p><p>配方: {H(TryRecipe()?.Name)}　End Reason: {H(_orchestrator?.EndReason)}</p><p>No-load: {H(noLoad?.PointId)}　Max efficiency: {H(maxEfficiency?.PointId)}　Max power: {H(maxMechanicalPower?.PointId)}　Max tested torque: {H(maxTestedTorque?.PointId)}　Minimum valid RPM: {H(minimumValidRpm?.PointId)}</p><img src='{H(fileTag)}_curve.png' style='max-width:100%'><h2>测试点</h2><table><tr><th>点</th><th>状态</th><th>DYN扭矩 N·m</th><th>DUT转速 RPM</th><th>Ibus A</th><th>Pin W</th><th>Pout W</th><th>效率 %</th><th>Iq命令 A</th><th>Iq实测 A</th><th>Vbus V</th><th>Duty %</th><th>电机温度 °C</th><th>控制器温度 °C</th><th>VESC Fault</th></tr>{rows}</table>";
        await File.WriteAllTextAsync(Path.Combine(directory, $"{fileTag}_report.html"), html, new UTF8Encoding(true));
        static string M(string? value) => (value ?? "").Replace("|", "\\|").Replace("\r", " ").Replace("\n", " ");
        var mdRows = string.Join(Environment.NewLine, results.Select(r => $"| {M(r.PointId)} | {M(r.Result)} | {r.Torque?.Mean:F3} | {r.Speed?.Mean:F1} | {r.DcBusCurrent?.Mean:F2} | {r.InputPower?.Mean:F1} | {r.MechanicalPower?.Mean:F1} | {r.Efficiency?.Mean:F1} | {r.Temperature?.Mean:F1} |"));
        var markdown = $"# 电机性能测试报告\n\n- 电机型号：**{M(recordedMotorModel)}**\n- 测试时间：{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}\n- 配方：{M(TryRecipe()?.Name)}\n- 结束原因：{M(_orchestrator?.EndReason)}\n- 测试点数：{results.Count}\n\n![{M(recordedMotorModel)} 性能曲线]({fileTag}_curve.png)\n\n| 测试点 | 状态 | DYN扭矩 N·m | DUT转速 RPM | Ibus A | Pin W | Pout W | 效率 % | 电机温度 °C |\n|---|---:|---:|---:|---:|---:|---:|---:|---:|\n{mdRows}\n";
        await File.WriteAllTextAsync(Path.Combine(directory, $"{fileTag}_测试报告.md"), markdown, new UTF8Encoding(true));
        ExportVisualsRequested?.Invoke(directory, fileTag);
        AddEvent($"已导出 {results.Count} 个结果：metadata/raw/points/curve/report");
    }

    private void RecordVescDiagnostic(string code, string message)
    {
        try
        {
            var directory = Path.Combine(_root, "artifacts", "diagnostics");
            Directory.CreateDirectory(directory);
            var line = JsonSerializer.Serialize(new { timestampUtc = DateTimeOffset.UtcNow, code, message }) + Environment.NewLine;
            File.AppendAllText(Path.Combine(directory, "vesc_link.jsonl"), line, new UTF8Encoding(false));
            _recorder?.Event(code, message);
        }
        catch { }
        if (code == "vesc_telemetry_stale" ||
            code == "vesc_worker_failed" && message.Contains("recognized=True", StringComparison.Ordinal))
            _ = RecoverDutLinkAsync();
    }

    private async Task RecoverDutLinkAsync()
    {
        if (Interlocked.Exchange(ref _dutRecoveryRunning, 1) != 0) return;
        var runtime = _dutRuntime;
        var portName = _connectedDutPortName;
        try
        {
            if (runtime == null || string.IsNullOrWhiteSpace(portName)) return;
            _recorder?.Event("vesc_recovery_pending", $"port={portName}; verifyMs=300; action=stop servo only if link remains unhealthy");

            // A stale notification is emitted on the first missed deadline. The same
            // worker can parse a waiting frame immediately afterwards. Require the link
            // to remain unhealthy before turning a short delay into a physical COM close.
            await Task.Delay(300, CancellationToken.None);
            if (!ReferenceEquals(_dutRuntime, runtime)) return;
            if (runtime.CanControlDut)
            {
                _recorder?.Event("vesc_recovery_cancelled", $"port={portName}; reason=telemetry recovered before intervention");
                return;
            }

            _recorder?.Event("vesc_recovery_start", $"port={portName}; policy=stop servo, clear DUT command, wait for USB re-enumeration up to 120s");
            _dutCommandMode = null;
            _dutCommandValue = 0;

            try
            {
                if (_orchestrator?.Running == true)
                {
                    Runtime().RequestStop();
                    await CancelRecipeAsync();
                }
                if (_runtime != null)
                {
                    _runtime.RequestStop();
                    await _runtime.StopAsync(true, CancellationToken.None);
                    _recorder?.Event("vesc_recovery_servo_stopped", "负载伺服已回零并失能");
                }
            }
            catch (Exception ex)
            {
                _recorder?.Event("vesc_recovery_stop_error", ex.ToString());
            }

            var deadline = DateTimeOffset.UtcNow.AddSeconds(120);
            var attempt = 0;
            while (DateTimeOffset.UtcNow < deadline)
            {
                if (!ReferenceEquals(_dutRuntime, runtime)) return;
                if (!runtime.AvailableDutPorts.Contains(portName, StringComparer.OrdinalIgnoreCase))
                {
                    SetDutScanStatus($"Waiting for {portName} to reconnect...");
                    await Task.Delay(TimeSpan.FromSeconds(1), CancellationToken.None);
                    continue;
                }

                attempt++;
                SetDutScanStatus($"Reconnecting {portName} (attempt {attempt})...");
                try
                {
                    using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(8));
                    await runtime.ConnectDutAsync(portName, timeout.Token);
                    if (!runtime.CanControlDut) throw new IOException("重连后没有收到有效 VESC 遥测。");
                    _recorder?.Event("vesc_recovery_ok", $"port={portName}; attempt={attempt}; commandsRestored=false");
                    System.Windows.Application.Current.Dispatcher.Invoke(() => AddEvent($"VESC/M1 已安全重连：{portName}；原电机命令未恢复"));
                    return;
                }
                catch (Exception ex)
                {
                    _recorder?.Event("vesc_recovery_retry", $"port={portName}; attempt={attempt}; error={ex}");
                    await Task.Delay(TimeSpan.FromSeconds(1), CancellationToken.None);
                }
            }
            _recorder?.Event("vesc_recovery_failed", $"port={portName}; waitSeconds=120; connectAttempts={attempt}; manual power cycle required");
            System.Windows.Application.Current.Dispatcher.Invoke(() => AddEvent($"VESC/M1 自动重连失败：{portName}；请检查主控 USB/供电并重新上电"));
        }
        finally
        {
            SetDutScanStatus(null);
            Interlocked.Exchange(ref _dutRecoveryRunning, 0);
        }
    }

    private void SetDutScanStatus(string? status)
    {
        var dispatcher = System.Windows.Application.Current?.Dispatcher;
        if (dispatcher == null || dispatcher.CheckAccess())
        {
            _dutScanStatus = status;
            PropertyChanged?.Invoke(this, new(nameof(DutControllerStatusDisplay)));
            return;
        }
        dispatcher.Invoke(() =>
        {
            _dutScanStatus = status;
            PropertyChanged?.Invoke(this, new(nameof(DutControllerStatusDisplay)));
        });
    }
    private static string SafeFileName(string value)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var safe = new string(value.Trim().Select(ch => invalid.Contains(ch) ? '_' : ch).ToArray());
        return string.IsNullOrWhiteSpace(safe) ? "未命名电机" : safe;
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

    public async Task CaptureServoEnableVescAsync(string portName, CancellationToken ct)
    {
        if (_runtime != null || _dutRuntime != null) throw new InvalidOperationException("诊断启动前设备必须处于未连接状态");
        ReadConfig();
        var moduleConfig = _config with { Vesc = _config.Vesc with { Enabled = false } };
        var vescBridge = new VescBridge(new MockRealtimeBridge(moduleConfig), moduleConfig.Vesc);
        vescBridge.Diagnostic += RecordVescDiagnostic;
        _dutRuntime = new BenchRuntime(vescBridge, moduleConfig, new NullSessionRecorder());
        try
        {
            await _dutRuntime.StartAsync(ct);
            await _dutRuntime.ConnectDutAsync(portName, ct);
            _connectedDutPortName = portName;
            ConnectionMode = 1;
            await ConnectAsync();
            _recorder?.Event("combined_capture_start", $"vesc={portName}; servo=EtherCAT direct; command=CST zero enable");
            if (!_dutRuntime.CanControlDut) throw new IOException("COM4 已打开，但 VESC 遥测未就绪");
            var before = _dutRuntime.Latest;
            _recorder?.Event("vesc_before_servo_enable", $"timestamp={before.Timestamp:O}; vbus={before.DutBusV}; rpm={before.DutSpeedRpm}; fault={before.DutFaultCode}");
            Runtime().Enable();
            var enableDeadline = DateTimeOffset.UtcNow.AddSeconds(5);
            while (!Runtime().Latest.ServoOn)
            {
                ct.ThrowIfCancellationRequested();
                if (DateTimeOffset.UtcNow >= enableDeadline) throw new TimeoutException("CST 零指令使能超时");
                await Task.Delay(25, ct);
            }
            _recorder?.Event("servo_zero_enable_confirmed", $"servoTimestamp={Runtime().Latest.Timestamp:O}; targetTorque={Runtime().Latest.TargetTorqueNm}; actualTorque={Runtime().Latest.ActualTorqueNm}");
            var monitorDeadline = DateTimeOffset.UtcNow.AddSeconds(60);
            var linkInterrupted = false;
            while (DateTimeOffset.UtcNow < monitorDeadline)
            {
                ct.ThrowIfCancellationRequested();
                if (!_dutRuntime.CanControlDut)
                {
                    linkInterrupted = true;
                    var failed = _dutRuntime.Latest;
                    _recorder?.Event("combined_capture_recovery_wait", $"VESC unavailable after servo enable; timestamp={failed.Timestamp:O}; vbus={failed.DutBusV}; rpm={failed.DutSpeedRpm}; fault={failed.DutFaultCode}");
                    var recoveryDeadline = DateTimeOffset.UtcNow.AddSeconds(30);
                    while (!_dutRuntime.CanControlDut && DateTimeOffset.UtcNow < recoveryDeadline)
                    {
                        ct.ThrowIfCancellationRequested();
                        await Task.Delay(100, ct);
                    }
                    if (!_dutRuntime.CanControlDut)
                    {
                        _recorder?.Event("combined_capture_failure", "VESC automatic recovery did not restore telemetry within 30 seconds");
                        throw new IOException("伺服零指令使能后 VESC 遥测失效，自动重连未恢复");
                    }
                    _recorder?.Event("combined_capture_recovered", $"VESC telemetry restored; servoOn={Runtime().Latest.ServoOn}; commandsRestored=false");
                }
                await Task.Delay(50, ct);
            }
            if (linkInterrupted)
                throw new IOException("伺服零指令使能后的 60 秒监测期间 VESC 通信曾中断；即使自动恢复也不能判定为连续稳定");
            var after = _dutRuntime.Latest;
            _recorder?.Event("combined_capture_pass", $"60s VESC telemetry continuous; timestamp={after.Timestamp:O}; vbus={after.DutBusV}; rpm={after.DutSpeedRpm}; fault={after.DutFaultCode}");
        }
        finally
        {
            if (_runtime != null)
            {
                try { await _runtime.StopAsync(true, CancellationToken.None); } catch (Exception ex) { _recorder?.Event("combined_capture_stop_error", ex.ToString()); }
                await _runtime.DisposeAsync();
                _runtime = null;
            }
            if (_dutRuntime != null)
            {
                try { await _dutRuntime.StopDutAsync(CancellationToken.None); } catch { }
                await _dutRuntime.DisposeAsync();
                _dutRuntime = null;
            }
        }
    }

    public async Task CaptureDutRunAsync(string portName, double rpm, CancellationToken ct)
    {
        if (_runtime != null || _dutRuntime != null) throw new InvalidOperationException("Diagnostic requires disconnected devices.");
        ReadConfig();
        var moduleConfig = _config with { Vesc = _config.Vesc with { Enabled = false } };
        var vescBridge = new VescBridge(new MockRealtimeBridge(moduleConfig), moduleConfig.Vesc);
        vescBridge.Diagnostic += RecordVescDiagnostic;
        _dutRuntime = new BenchRuntime(vescBridge, moduleConfig, new NullSessionRecorder());
        var interrupted = false;
        try
        {
            await _dutRuntime.StartAsync(ct);
            await _dutRuntime.ConnectDutAsync(portName, ct);
            _connectedDutPortName = portName;
            await _dutRuntime.SetDutAsync(DutControlMode.SpeedRpm, rpm, ct);
            RecordVescDiagnostic("dut_scope_command", $"port={portName}; speedRpm={rpm:F0}");
            var deadline = DateTimeOffset.UtcNow.AddSeconds(8);
            while (DateTimeOffset.UtcNow < deadline)
            {
                ct.ThrowIfCancellationRequested();
                if (!_dutRuntime.CanControlDut) { interrupted = true; break; }
                await Task.Delay(20, ct);
            }

            if (interrupted)
            {
                var recoveryDeadline = DateTimeOffset.UtcNow.AddSeconds(20);
                while (!_dutRuntime.CanControlDut && DateTimeOffset.UtcNow < recoveryDeadline)
                {
                    ct.ThrowIfCancellationRequested();
                    await Task.Delay(50, ct);
                }
            }
            if (_dutRuntime.CanControlDut) await _dutRuntime.StopDutAsync(CancellationToken.None);
            if (interrupted) throw new IOException($"{portName} disappeared after the {rpm:F0} RPM command; stop was sent after recovery={_dutRuntime.CanControlDut}.");
        }
        finally
        {
            if (_dutRuntime != null)
            {
                if (_dutRuntime.CanControlDut) try { await _dutRuntime.StopDutAsync(CancellationToken.None); } catch { }
                await _dutRuntime.DisposeAsync();
                _dutRuntime = null;
            }
            _connectedDutPortName = null;
        }
    }
    public void Refresh()
    {
        if (Volatile.Read(ref _dutRecoveryRunning) != 0 && DateTimeOffset.UtcNow >= _nextDutPortRefreshAt)
        {
            _nextDutPortRefreshAt = DateTimeOffset.UtcNow.AddSeconds(1);
            RefreshDutPorts();
        }
        if (_runtime != null) while (_runtime.Alarms.TryDequeue(out var alarm)) AddEvent($"{alarm.Code}: {alarm.Message}");
        foreach (var p in new[] { nameof(ConnectionText), nameof(EtherCatStatusColor), nameof(EtherCatStatusText), nameof(ServoHeaderSummary), nameof(ModeBanner), nameof(LimitsText), nameof(DirectSpeedRangeText), nameof(DirectRampRangeText), nameof(DirectRampLabel), nameof(RpmDisplay), nameof(TorqueDisplay),
            nameof(PowerDisplay), nameof(BusDisplay), nameof(TemperatureDisplay), nameof(SensorTorqueDisplay), nameof(SensorSpeedDisplay),
            nameof(SensorPowerDisplay), nameof(SensorStatusDisplay), nameof(SensorConnectionColor), nameof(SensorConnectionText), nameof(DutConnectionColor), nameof(DutConnectionText), nameof(EfficiencyDisplay),
            nameof(ServoSpeedDisplay), nameof(ServoBusVoltageDisplay), nameof(ServoBusCurrentDisplay), nameof(ServoPhaseCurrentDisplay), nameof(ServoDutyDisplay),
            nameof(ServoTorqueDisplay), nameof(ServoInputPowerDisplay), nameof(ServoOutputPowerDisplay), nameof(ServoFeedbackPowerDisplay), nameof(ServoEfficiencyDisplay), nameof(ServoLoadDisplay), nameof(ServoMotorTemperatureDisplay),
            nameof(DutBusVoltageDisplay), nameof(DutBusCurrentDisplay), nameof(DutPhaseCurrentDisplay), nameof(DutDutyDisplay), nameof(DutMotorTemperatureDisplay), nameof(DutEfficiencyDisplay), nameof(DutSpeedDisplay), nameof(DutControllerStatusDisplay), nameof(DutCommandStatusDisplay),
            nameof(CstLoadStatusDisplay), nameof(CstLimitsText), nameof(StateText), nameof(SessionText),
            nameof(Phase), nameof(ServoEnableStatusDisplay), nameof(ConfigurationInfo), nameof(RecipeNameDisplay),
            nameof(AutoTestStatus), nameof(AutoTestStatusColor), nameof(AutoProgressText), nameof(PrecheckSummary), nameof(AutoTestConfigurationEnabled), nameof(IsAutoTestRunning), nameof(Phase), nameof(AutoCurrentPoint), nameof(AutoTargetTorque), nameof(AutoSampleCount), nameof(AutoLiveMetrics), nameof(VescPrecheckColor), nameof(ServoPrecheckColor), nameof(DynPrecheckColor), nameof(ZeroPrecheckColor), nameof(ConfigPrecheckColor) }) PropertyChanged?.Invoke(this, new(p));
    }
    private void RefreshDutPorts()
    {
        var previous = SelectedDutPort;
        var targetPortName = _connectedDutPortName ?? previous?.PortName ?? _config.Vesc.PortName;
        DutPorts.Clear();
        foreach (var port in WindowsSerialPortDiscovery.GetAllPortInfos())
            DutPorts.Add(port);
        SelectedDutPort = previous != null ? DutPorts.FirstOrDefault(p => string.Equals(p.PortName, previous.PortName, StringComparison.OrdinalIgnoreCase)) : null;
        SelectedDutPort ??= DutPorts.FirstOrDefault(p => string.Equals(p.PortName, _config.Vesc.PortName, StringComparison.OrdinalIgnoreCase));
        // Keep the intended VESC port visible while the USB CDC device is absent.
        // This preserves identity across re-enumeration without probing COM1/COM2.
        if (SelectedDutPort == null && !string.IsNullOrWhiteSpace(targetPortName))
        {
            var offline = new WindowsSerialPortDiscovery.PortInfo(targetPortName, $"{targetPortName} (offline - waiting for USB)");
            DutPorts.Add(offline);
            SelectedDutPort = offline;
        }
        PropertyChanged?.Invoke(this, new(nameof(SelectedDutPort)));
        PropertyChanged?.Invoke(this, new(nameof(DutControllerStatusDisplay)));
    }
    private void RefreshSensorPorts()
    {
        var previous = SelectedSensorPort;
        SensorPorts.Clear();
        foreach (var port in WindowsSerialPortDiscovery.GetAllPortInfos()) SensorPorts.Add(port);
        SelectedSensorPort = previous != null ? SensorPorts.FirstOrDefault(p => string.Equals(p.PortName, previous.PortName, StringComparison.OrdinalIgnoreCase)) : null;
        SelectedSensorPort ??= SensorPorts.FirstOrDefault(p => string.Equals(p.PortName, _config.TorqueSensor.PortName, StringComparison.OrdinalIgnoreCase)) ?? SensorPorts.FirstOrDefault();
        PropertyChanged?.Invoke(this, new(nameof(SelectedSensorPort)));
        PropertyChanged?.Invoke(this, new(nameof(SensorStatusDisplay)));
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
