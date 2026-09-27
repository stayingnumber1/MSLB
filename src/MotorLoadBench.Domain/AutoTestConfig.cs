namespace MotorLoadBench.Domain;

public record AutoTestConfig
{
    public double NominalVoltageV { get; init; } = 30.0;
    public double NominalMaxSpeedRpm { get; init; } = 3000;
    public double SoftCurrentA { get; init; } = 31.5;
    public double SoftPowerW { get; init; } = 945;
    public double ReducedTorqueStepNm { get; init; } = .02;
    public double TorqueRampRateNmPerSec { get; init; } = .30;
    public double PointSampleWindowSeconds { get; init; } = 1.0;
    public double StableHoldSeconds { get; init; } = .5;
    public double StableTimeoutSeconds { get; init; } = 10.0;
    public double TorqueStableFraction { get; init; } = .05;
    public double RpmStableFraction { get; init; } = .05;
    public double IbusStableFraction { get; init; } = .05;
    public int TelemetryTimeoutMs { get; init; } = 200;
    public double ZeroRpmTolerance { get; init; } = 10;
    public double DynZeroTorqueToleranceNm { get; init; } = .05;
    public double TorqueRangeMaxNm { get; init; } = 4.0;
    public double TorqueStepNm { get; init; } = .10;
    public double SpeedPlotMaxRpm { get; init; } = 3500;
    public double LowSpeedStopRpm { get; init; } = 100;
    public double StallStopRpm { get; init; } = 200;
    public double StallConfirmSeconds { get; init; } = .2;
    public double StallCrossingStopRampSeconds { get; init; } = .4;
    public double StallCrossingTorqueRampNmPerSec { get; init; } = 7.0;
    public string DutDriveMode { get; init; } = "DutyCycle";
    public double DutDriveValue { get; init; } = .95;
    public double DutDutyRampPerSecond { get; init; } = .5;
    public double OverTemperatureStopRampSeconds { get; init; } = 1.5;
    public double DutCurrentRampAperSec { get; init; } = 10.0;
    public double SimulationNoLoadRpm { get; init; } = 3000;
    public double StopConfirmTimeoutSeconds { get; init; } = 10;
}
