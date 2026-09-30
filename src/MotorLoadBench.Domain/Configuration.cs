using System.Text.Json;
using System.Text.Json.Serialization;

namespace MotorLoadBench.Domain;

public record Limits
{
    public double MaxTorqueNm { get; init; } = .2;
    public double ContinuousTorqueNm { get; init; } = .2;
    public double PeakSeconds { get; init; } = 2;
    public double PeakCooldownSeconds { get; init; } = 30;
    public double MaxSpeedRpm { get; init; } = 6000;
    public double MaxSpeedRampRpmPerSec { get; init; } = 6000;
    public double MinLoadSpeedRpm { get; init; } = 50;
    public double LoadedStartTriggerRpm { get; init; } = 3;
    public int LoadedStartTriggerConfirmMs { get; init; } = 200;
    public double LoadedStartReverseTripRpm { get; init; } = 30;
    public int LoadedStartReverseConfirmMs { get; init; } = 300;
    public double MaxPowerW { get; init; } = 1050;
    public double MaxDutBusCurrentA { get; init; } = 35;
    public double MotorWarnC { get; init; } = 60;
    public double MotorTripC { get; init; } = 70;
    public double DutMotorTripC { get; init; } = 150;
    public double BrakeTripC { get; init; } = 80;
    public double? DcBusTripV { get; init; }
    public double MaxRampNmPerSec { get; init; } = .3;
    public double StopRampNmPerSec { get; init; } = .2;
    public double ZeroTorqueNm { get; init; } = .01;
    public double StopTimeoutSeconds { get; init; } = 5;
    public int HeartbeatTimeoutMs { get; init; } = 500;
    public int SnapshotTimeoutMs { get; init; } = 200;
    public int OverTorqueDelayMs { get; init; } = 50;
    public double OverTorqueMarginNm { get; init; } = .05;
    public List<MapPoint> TorqueEnvelope { get; init; } = [new(0, .2), new(6000, .2)];
}
public record DriveConfig
{
    public string Name { get; init; } = "SV660N / SV635N 待确认";
    public int CstMode { get; init; } = 10;
    public int InstallationSign { get; init; } = -1;
    public int ExpectedRotationSign { get; init; } = 1;
    public double? RawTorquePerNm { get; init; }
    public double? RatedTorqueNm { get; init; }
    public double? RpmPerRawVelocity { get; init; }
    public bool ModelVerified { get; init; }
    public bool PdoVerified { get; init; }
    public bool DirectionVerified { get; init; }
    public bool RegenerationVerified { get; init; }
    public bool HardwareSafetyVerified { get; init; }
    public bool AllowHardwareWrites { get; init; }
}
public record AdsConfig
{
    public string AmsNetId { get; init; } = "127.0.0.1.1.1";
    public int Port { get; init; } = 851;
    public string CommandSymbol { get; init; } = "GVL_Hmi.Command";
    public string CommitSymbol { get; init; } = "GVL_Hmi.CommandCommit";
    public string StatusSymbol { get; init; } = "GVL_Hmi.Status";
    public int TimeoutMs { get; init; } = 200;
    public int ReconnectMs { get; init; } = 2000;
}
public enum TorqueSensorProtocol { Hex6, Hex8, Ascii }
public record TorqueSensorConfig
{
    public bool Enabled { get; init; }
    public string PortName { get; init; } = "COM3";
    public int BaudRate { get; init; } = 19200;
    public int DataBits { get; init; } = 8;
    public string Parity { get; init; } = "None";
    public int StopBits { get; init; } = 1;
    public TorqueSensorProtocol Protocol { get; init; } = TorqueSensorProtocol.Hex6;
    public int TorqueDecimals { get; init; } = 3;
    public int SpeedDecimals { get; init; }
    public int DirectionSign { get; init; } = 1;
    public int StaleAfterMs { get; init; } = 100;
}
public record VescConfig
{
    public bool Enabled { get; init; }
    public string PortName { get; init; } = "COM4";
    public int BaudRate { get; init; } = 115200;
    public double MotorPolePairs { get; init; } = 1;
    public double MaxRpm { get; init; } = 6000;
    public double CurrentModeMaxRpm { get; init; } = 6000;
    public double MaxMotorCurrentA { get; init; } = 100;
    public double MaxBrakeCurrentA { get; init; } = 3;
    public double MaxDutyCycle { get; init; } = .95;
    public double DefaultRampRpmPerSec { get; init; } = 100;
    public int CommandRateHz { get; init; } = 50;
    public int TelemetryRateHz { get; init; } = 10;
    public int StaleAfterMs { get; init; } = 500;
}
public record BenchConfig
{
    public int Version { get; init; } = 1;
    public DriveConfig Drive { get; init; } = new();
    public Limits Limits { get; init; } = new();
    public AdsConfig Ads { get; init; } = new();
    public TorqueSensorConfig TorqueSensor { get; init; } = new();
    public VescConfig Vesc { get; init; } = new();
    public AutoTestConfig AutoTest { get; init; } = new();
    public List<MapPoint> TorqueMap { get; init; } = [new(0, 0), new(100, .05), new(6000, .15)];
    public string DataDirectory { get; init; } = "artifacts/sessions";
    public static JsonSerializerOptions Json { get; } = new()
    {
        WriteIndented = true, PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() }
    };
    public void Validate()
    {
        var l = Limits;
        double[] positive = [l.MaxTorqueNm, l.ContinuousTorqueNm, l.PeakSeconds, l.PeakCooldownSeconds, l.MaxSpeedRpm,
            l.MinLoadSpeedRpm, l.LoadedStartTriggerRpm, l.LoadedStartReverseTripRpm,
            l.MaxSpeedRampRpmPerSec, l.MaxPowerW, l.MaxDutBusCurrentA, l.MaxRampNmPerSec, l.StopRampNmPerSec, l.ZeroTorqueNm, l.StopTimeoutSeconds];
        if (Version != 1 || positive.Any(x => !double.IsFinite(x) || x <= 0) ||
            l.ContinuousTorqueNm > l.MaxTorqueNm || l.MinLoadSpeedRpm >= l.MaxSpeedRpm ||
            !double.IsFinite(l.MotorWarnC) || !double.IsFinite(l.MotorTripC) || !double.IsFinite(l.DutMotorTripC) || !double.IsFinite(l.BrakeTripC) ||
            l.DutMotorTripC <= 0 ||
            l.MotorWarnC >= l.MotorTripC || l.HeartbeatTimeoutMs < 100 || l.SnapshotTimeoutMs < 20 ||
            l.OverTorqueDelayMs < 1 || l.LoadedStartTriggerConfirmMs < 50 || l.LoadedStartReverseConfirmMs < 50 ||
            l.LoadedStartTriggerRpm >= l.MinLoadSpeedRpm || l.LoadedStartReverseTripRpm >= l.MaxSpeedRpm ||
            !double.IsFinite(l.OverTorqueMarginNm) || l.OverTorqueMarginNm < 0 ||
            Math.Abs(Drive.InstallationSign) != 1 || Math.Abs(Drive.ExpectedRotationSign) != 1 || Drive.InstallationSign * Drive.ExpectedRotationSign != -1 ||
            Drive.CstMode is < 1 or > 127 || string.IsNullOrWhiteSpace(DataDirectory))
            throw new InvalidDataException("配置无效：限值、方向或版本不合法，禁止使能。");
        ValidateMap(TorqueMap); ValidateMap(l.TorqueEnvelope);
        var sensor = TorqueSensor;
        if (sensor.Enabled && (string.IsNullOrWhiteSpace(sensor.PortName) || sensor.BaudRate <= 0 ||
            sensor.DataBits is < 5 or > 8 || sensor.StopBits is < 1 or > 2 || sensor.TorqueDecimals is < 0 or > 6 ||
            sensor.SpeedDecimals is < 0 or > 6 || Math.Abs(sensor.DirectionSign) != 1 || sensor.StaleAfterMs is < 20 or > 5000 ||
            !(new[] { "None", "Odd", "Even", "Mark", "Space" }).Contains(sensor.Parity, StringComparer.OrdinalIgnoreCase)))
            throw new InvalidDataException("扭矩传感器串口配置无效。");
        var vesc = Vesc;
        if (vesc.Enabled && (string.IsNullOrWhiteSpace(vesc.PortName) || vesc.BaudRate <= 0 ||
            !double.IsFinite(vesc.MotorPolePairs) || vesc.MotorPolePairs <= 0 ||
            !double.IsFinite(vesc.MaxRpm) || vesc.MaxRpm <= 0 ||
            !double.IsFinite(vesc.CurrentModeMaxRpm) || vesc.CurrentModeMaxRpm <= 0 ||
            vesc.CurrentModeMaxRpm > vesc.MaxRpm ||
            !double.IsFinite(vesc.MaxMotorCurrentA) || vesc.MaxMotorCurrentA <= 0 ||
            !double.IsFinite(vesc.MaxBrakeCurrentA) || vesc.MaxBrakeCurrentA <= 0 ||
            !double.IsFinite(vesc.MaxDutyCycle) || vesc.MaxDutyCycle is <= 0 or > 1 ||
            !double.IsFinite(vesc.DefaultRampRpmPerSec) || vesc.DefaultRampRpmPerSec <= 0 ||
            vesc.CommandRateHz is < 10 or > 100 || vesc.TelemetryRateHz is < 1 or > 50 ||
            vesc.StaleAfterMs is < 100 or > 5000))
            throw new InvalidDataException("VESC 串口、电转速比例或刷新周期配置无效。");
        var auto = AutoTest;
        double[] autoPositive = [auto.NominalVoltageV, auto.NominalMaxSpeedRpm, auto.SoftCurrentA, auto.SoftPowerW,
            auto.ReducedTorqueStepNm, auto.TorqueRampRateNmPerSec, auto.PointSampleWindowSeconds,
            auto.StableHoldSeconds, auto.StableTimeoutSeconds, auto.ZeroRpmTolerance, auto.DynZeroTorqueToleranceNm,
            auto.TorqueRangeMaxNm, auto.TorqueStepNm, auto.SpeedPlotMaxRpm, auto.LowSpeedStopRpm, auto.StallStopRpm, auto.StallConfirmSeconds,
            auto.StallCrossingStopRampSeconds, auto.StallCrossingTorqueRampNmPerSec,
            auto.TorqueStableFraction, auto.RpmStableFraction, auto.IbusStableFraction,
            auto.DutDriveValue, auto.DutDutyRampPerSecond, auto.OverTemperatureStopRampSeconds,
            auto.DutCurrentRampAperSec, auto.SimulationNoLoadRpm, auto.StopConfirmTimeoutSeconds];
        if (autoPositive.Any(x => !double.IsFinite(x) || x <= 0) || auto.TelemetryTimeoutMs is < 20 or > 5000 ||
            auto.LowSpeedStopRpm >= auto.NominalMaxSpeedRpm ||
            auto.SimulationNoLoadRpm > auto.NominalMaxSpeedRpm ||
            auto.TorqueStableFraction > 1 || auto.RpmStableFraction > 1 || auto.IbusStableFraction > 1 ||
            auto.StallStopRpm >= auto.NominalMaxSpeedRpm || auto.StallCrossingTorqueRampNmPerSec > 10 ||
            auto.DutDriveMode is not "DutyCycle" || auto.DutDriveValue is <= 0 or > 1 || auto.DutDriveValue > Vesc.MaxDutyCycle)
            throw new InvalidDataException("自动测试的额定值、软限制、稳定窗口或 Telemetry Timeout 配置无效。");
        if (Drive.AllowHardwareWrites) ValidateHardware();
    }
    public void ValidateHardware()
    {
        if (!Drive.AllowHardwareWrites || !Drive.ModelVerified || !Drive.PdoVerified || !Drive.DirectionVerified ||
            !Drive.RegenerationVerified || !Drive.HardwareSafetyVerified ||
            Drive.RawTorquePerNm is not > 0 || !double.IsFinite(Drive.RawTorquePerNm.Value) ||
            Drive.RpmPerRawVelocity is not > 0 || !double.IsFinite(Drive.RpmPerRawVelocity.Value) ||
            Limits.DcBusTripV is not > 0 || !double.IsFinite(Limits.DcBusTripV.Value))
            throw new InvalidOperationException("实机写入锁定：型号/ESI/PDO/方向/回生/STO或缩放尚未核实。可以使用仿真或ADS只读。");
    }
    public static void ValidateMap(IReadOnlyList<MapPoint> map)
    {
        if (map.Count < 2 || map.Any(x => !double.IsFinite(x.Rpm) || !double.IsFinite(x.TorqueNm) || x.Rpm < 0 || x.TorqueNm < 0) ||
            map.Zip(map.Skip(1)).Any(p => p.First.Rpm >= p.Second.Rpm)) throw new InvalidDataException("转矩表需要至少两个按转速严格递增的有效点。");
    }
    public static void ValidateRecipe(Recipe r, Limits l)
    {
        if (r.Version != 1 || r.Points.Count == 0 || r.Points.Select(x => x.Id).Distinct().Count() != r.Points.Count)
            throw new InvalidDataException("配方为空、ID重复或版本不支持。");
        foreach (var p in r.Points)
        {
            double[] vals = [p.Rpm, p.SpeedWindowRpm, p.TorqueNm, p.TorqueToleranceNm, p.RampNmPerSec,
                p.StableSeconds, p.SampleSeconds, p.MaxDurationSeconds, p.TemperatureLimitC];
            if (string.IsNullOrWhiteSpace(p.Id) || vals.Any(x => !double.IsFinite(x)) || p.Rpm <= l.MinLoadSpeedRpm ||
                p.Rpm + p.SpeedWindowRpm > l.MaxSpeedRpm || p.SpeedWindowRpm <= 0 || p.TorqueNm <= 0 || p.TorqueNm > l.MaxTorqueNm ||
                p.RampNmPerSec <= 0 || p.RampNmPerSec > l.MaxRampNmPerSec || p.TorqueToleranceNm <= 0 ||
                p.StableSeconds <= 0 || p.SampleSeconds <= 0 || p.MaxDurationSeconds <= p.SampleSeconds + 2 * p.StableSeconds ||
                p.TemperatureLimitC > l.MotorTripC || p.TorqueNm * (p.Rpm + p.SpeedWindowRpm) * Math.PI / 30 > l.MaxPowerW ||
                p.TorqueNm > LoadMath.Interpolate(l.TorqueEnvelope, p.Rpm + p.SpeedWindowRpm))
                throw new InvalidDataException($"测试点 {p.Id} 超出当前配置或时间/容差无效。");
        }
    }
}
