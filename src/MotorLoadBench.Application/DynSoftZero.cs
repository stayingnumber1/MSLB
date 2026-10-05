using MotorLoadBench.Domain;

namespace MotorLoadBench.Application;

public enum DynZeroDecision { Zero, NoData, NotAtRest, TooLarge }

// Software zero of the DYN-200 before a precheck or a test start. The precheck's Zero item rejects a
// DYN reading beyond AutoTest.DynZeroTorqueToleranceNm, and the zero inside the recipe ran only after
// that precheck, so a drifted sensor blocked every start (field 2026-10-05: +0.066 N·m). Zero only with
// the bench at rest (DUT stationary, servo torque ~0) and never an offset above MaxOffsetNm: that is a
// sensor or mechanical problem to look at, not a drift to hide.
public static class DynSoftZero
{
    public const double MaxOffsetNm = 0.30;

    public static DynZeroDecision Decide(BenchSnapshot? s, BenchConfig config)
    {
        if (s?.ExternalTorqueNm is not { } reading || s.ExternalHealthy != true) return DynZeroDecision.NoData;
        if (Math.Abs(s.DutSpeedRpm ?? double.MaxValue) > config.AutoTest.ZeroRpmTolerance ||
            Math.Abs(s.ActualTorqueNm) > config.Limits.ZeroTorqueNm) return DynZeroDecision.NotAtRest;
        if (Math.Abs(reading) > MaxOffsetNm) return DynZeroDecision.TooLarge;
        return DynZeroDecision.Zero;
    }
}
