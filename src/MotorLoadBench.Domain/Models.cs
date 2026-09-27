namespace MotorLoadBench.Domain;

public enum BenchState { Disconnected, DriveReady, ServoOnIdle, RampUp, Running, RampDown, Fault }
public enum LoadMode { ConstantTorque, ConstantPower, TorqueMap, LoadedStart }
public enum TorqueSource { ServoEstimated, ExternalSensor }
public enum OnFail { Abort, Skip, RetryOnce }
public enum ControlOwner { Manual, AutoTest, Safety }
[Flags] public enum Interlock : uint { None = 0, Emergency = 1, Guard = 2, Regeneration = 4, EtherCat = 8, Heartbeat = 16, Overspeed = 32, Overtemp = 64, Overvoltage = 128, DriveFault = 256, Direction = 512, Invalid = 1024, OverTorque = 2048, PeakTimeout = 4096 }
public record BenchCommand(bool EnableRequest = false, bool DisableRequest = false, bool ResetFault = false,
    double TargetTorqueNm = 0, double TorqueRampNmPerSec = .1, uint Heartbeat = 0,
    LoadMode Mode = LoadMode.ConstantTorque, double TargetPowerW = 0, bool StopRequest = false);
public record BenchSnapshot
{
    public DateTimeOffset Timestamp { get; init; } = DateTimeOffset.UtcNow;
    public BenchState State { get; init; }
    public bool Connected { get; init; }
    public bool EtherCatOnline { get; init; }
    public bool DriveReady { get; init; }
    public bool ServoOn { get; init; }
    public double TargetTorqueNm { get; init; }
    public double ActualTorqueNm { get; init; }
    public double SpeedRpm { get; init; }
    public double? DcBusV { get; init; }
    public double? MotorTempC { get; init; }
    public double? BrakeTempC { get; init; }
    public double? ServoCurrentA { get; init; }
    public double? ServoBusCurrentA { get; init; }
    public double? ServoPhaseCurrentA { get; init; }
    public double? ServoDutyCyclePct { get; init; }
    public double? ServoEfficiencyPct { get; init; }
    public double? ServoLoadPct { get; init; }
    public double? ServoFeedbackPowerW { get; init; }
    public double? ExternalTorqueNm { get; init; }
    public double? ExternalSpeedRpm { get; init; }
    public double? DutBusV { get; init; }
    public double? DutCurrentA { get; init; }
    public double? DutPhaseCurrentA { get; init; }
    public double? DutIqCommandA { get; init; }
    public double? DutDutyCyclePct { get; init; }
    public double? DutMotorTempC { get; init; }
    public double? DutControllerTempC { get; init; }
    public byte? DutFaultCode { get; init; }
    public double? DutSpeedRpm { get; init; }
    public bool ExternalHealthy { get; init; }
    public uint ErrorCode { get; init; }
    public uint? AuxiliaryFaultCode { get; init; }
    public string? DriveFaultDetail { get; init; }
    public Interlock Interlocks { get; init; }
    public uint PlcHeartbeat { get; init; }
    public double MechanicalPowerW => Math.Abs(ActualTorqueNm * SpeedRpm * Math.PI / 30);
    // DUT motor efficiency uses DC-bus electrical input and the independent
    // DYN-200 shaft measurement. VESC phase current and estimated servo torque
    // must never be substituted into this calculation.
    public double? DutElectricalInputPowerW => DutBusV is { } v && DutCurrentA is { } i &&
        double.IsFinite(v) && double.IsFinite(i) && v > 0 && i > 0 ? v * i : null;
    public double? DutMechanicalOutputPowerW => ExternalHealthy && ExternalTorqueNm is { } t &&
        double.IsFinite(t) && DutSpeedRpm is { } r && double.IsFinite(r)
            ? Math.Abs(t * r * Math.PI / 30) : null;
    public double? DutMotorEfficiencyPct => DutElectricalInputPowerW is > 1e-6 && DutMechanicalOutputPowerW is { } p
        ? p / DutElectricalInputPowerW.Value * 100 : null;
    public double? InputPowerW => DutElectricalInputPowerW;
    public double? MeasuredPowerW => DutMechanicalOutputPowerW;
    public double? EfficiencyPct => DutMotorEfficiencyPct;
    public double? ServoInputPowerW => DcBusV is { } v && ServoBusCurrentA is { } i && double.IsFinite(v) && double.IsFinite(i) ? Math.Abs(v * i) : null;
    public double? ServoOutputPowerW => MeasuredPowerW;
}
public record MapPoint(double Rpm, double TorqueNm);
public record TestPoint
{
    public string Id { get; init; } = "P01";
    public double Rpm { get; init; } = 300;
    public double SpeedWindowRpm { get; init; } = 30;
    public double TorqueNm { get; init; } = .1;
    public double TorqueToleranceNm { get; init; } = .02;
    public double RampNmPerSec { get; init; } = .1;
    public double StableSeconds { get; init; } = 1;
    public double SampleSeconds { get; init; } = 2;
    public double MaxDurationSeconds { get; init; } = 20;
    public double TemperatureLimitC { get; init; } = 70;
    public OnFail OnFail { get; init; }
}
public record Recipe
{
    public int Version { get; init; } = 1;
    public string Name { get; init; } = "低风险仿真配方";
    public List<TestPoint> Points { get; init; } = [new()];
}
public record Alarm(DateTimeOffset Timestamp, string Code, string Message);
public record Statistics(int Count, double Mean, double Rms, double Min, double Max, double StdDev)
{
    public static Statistics From(IEnumerable<double> values)
    {
        var a = values.ToArray();
        if (a.Length == 0 || a.Any(x => !double.IsFinite(x))) throw new ArgumentException("无有效统计样本");
        var mean = a.Average();
        return new(a.Length, mean, Math.Sqrt(a.Average(x => x * x)), a.Min(), a.Max(), Math.Sqrt(a.Average(x => (x - mean) * (x - mean))));
    }
}
public record PointResult(string PointId, int Attempt, string Result, string? Reason, Statistics? Torque,
    Statistics? Speed, Statistics? MechanicalPower, string RawDataPath, Statistics? Efficiency = null,
    TorqueSource Source = TorqueSource.ServoEstimated, Statistics? DcBusCurrent = null,
    Statistics? IqCommand = null, Statistics? IqActual = null, Statistics? Vbus = null,
    Statistics? InputPower = null, Statistics? Duty = null, Statistics? Temperature = null,
    Statistics? ServoTargetTorque = null, Statistics? ServoActualTorque = null,
    Statistics? ControllerTemperature = null, byte? DutFaultCode = null);
