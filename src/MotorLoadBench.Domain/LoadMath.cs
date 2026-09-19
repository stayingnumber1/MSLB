namespace MotorLoadBench.Domain;

public static class LoadMath
{
    public static double Interpolate(IReadOnlyList<MapPoint> map, double rpm)
    {
        rpm = Math.Abs(rpm);
        if (rpm <= map[0].Rpm) return map[0].TorqueNm;
        for (int i = 1; i < map.Count; i++)
            if (rpm <= map[i].Rpm)
            {
                var a = map[i - 1]; var b = map[i];
                return a.TorqueNm + (b.TorqueNm - a.TorqueNm) * (rpm - a.Rpm) / (b.Rpm - a.Rpm);
            }
        return map[^1].TorqueNm;
    }
    public static double Slew(double current, double target, double rate, double dt) =>
        current + Math.Clamp(target - current, -rate * dt, rate * dt);
    public static double Demand(BenchCommand c, BenchConfig config, double rpm, double temp)
    {
        var l = config.Limits;
        if (!double.IsFinite(rpm) || !double.IsFinite(temp) || !double.IsFinite(c.TargetTorqueNm) ||
            !double.IsFinite(c.TargetPowerW) || c.TargetTorqueNm < 0 || c.TargetPowerW < 0) throw new ArgumentException("非法工程量");
        // Passive-load policy: never create motion at standstill or follow an unexpected reversal.
        if (Math.Abs(rpm) < l.MinLoadSpeedRpm || Math.Sign(rpm) != config.Drive.ExpectedRotationSign) return 0;
        var cap = Math.Min(l.MaxTorqueNm, Interpolate(l.TorqueEnvelope, rpm));
        cap = Math.Min(cap, l.MaxPowerW / (Math.Abs(rpm) * Math.PI / 30));
        if (temp > l.MotorWarnC) cap *= Math.Clamp((l.MotorTripC - temp) / (l.MotorTripC - l.MotorWarnC), 0, 1);
        var magnitude = c.Mode switch
        {
            LoadMode.ConstantPower => c.TargetPowerW / (Math.Abs(rpm) * Math.PI / 30),
            LoadMode.TorqueMap => Interpolate(config.TorqueMap, rpm),
            _ => c.TargetTorqueNm
        };
        return Math.Min(cap, magnitude) * config.Drive.InstallationSign;
    }
}
