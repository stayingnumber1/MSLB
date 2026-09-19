using MotorLoadBench.Domain;

namespace MotorLoadBench.Application;

public record TorqueCurvePlan(
    double StartRpm,
    double EndRpm,
    double RpmStep,
    double StartTorqueNm,
    double EndTorqueNm,
    double TorqueStepNm,
    double RampNmPerSec = .1,
    double StableSeconds = 1,
    double SampleSeconds = 2,
    double MaxDurationSeconds = 20);

public static class TorqueCurveRecipeFactory
{
    public static Recipe Create(TorqueCurvePlan plan, BenchConfig config)
    {
        config.Validate();
        var values = new[] { plan.StartRpm, plan.EndRpm, plan.RpmStep, plan.StartTorqueNm,
            plan.EndTorqueNm, plan.TorqueStepNm, plan.RampNmPerSec, plan.StableSeconds,
            plan.SampleSeconds, plan.MaxDurationSeconds };
        if (values.Any(v => !double.IsFinite(v)) || plan.StartRpm <= config.Limits.MinLoadSpeedRpm ||
            plan.EndRpm < plan.StartRpm || plan.EndRpm >= config.Limits.MaxSpeedRpm || plan.RpmStep <= 0 ||
            plan.StartTorqueNm <= 0 || plan.EndTorqueNm < plan.StartTorqueNm || plan.TorqueStepNm <= 0)
            throw new InvalidDataException("扭矩曲线范围无效或超出当前转速限值。");

        var points = new List<TestPoint>();
        foreach (var rpm in Steps(plan.StartRpm, plan.EndRpm, plan.RpmStep))
        {
            const double speedWindowRpm = 30;
            var limitRpm = rpm + speedWindowRpm;
            var powerLimit = config.Limits.MaxPowerW * 30 / (Math.PI * limitRpm);
            var safeTorque = Math.Min(config.Limits.MaxTorqueNm,
                Math.Min(powerLimit, LoadMath.Interpolate(config.Limits.TorqueEnvelope, limitRpm)));
            foreach (var torque in Steps(plan.StartTorqueNm, plan.EndTorqueNm, plan.TorqueStepNm))
            {
                if (torque > safeTorque + 1e-9) continue;
                points.Add(new TestPoint
                {
                    Id = $"R{rpm:0000}_T{torque:0.000}".Replace('.', '_'),
                    Rpm = rpm,
                    SpeedWindowRpm = speedWindowRpm,
                    TorqueNm = torque,
                    RampNmPerSec = plan.RampNmPerSec,
                    StableSeconds = plan.StableSeconds,
                    SampleSeconds = plan.SampleSeconds,
                    MaxDurationSeconds = plan.MaxDurationSeconds,
                    TemperatureLimitC = config.Limits.MotorTripC
                });
            }
        }
        if (points.Count == 0) throw new InvalidDataException("当前功率、转矩包络和转速限值下没有可执行的曲线点。");
        var recipe = new Recipe { Name = $"扭矩曲线 {plan.StartRpm:F0}-{plan.EndRpm:F0} rpm", Points = points };
        BenchConfig.ValidateRecipe(recipe, config.Limits);
        return recipe;
    }

    private static IEnumerable<double> Steps(double start, double end, double step)
    {
        var count = checked((int)Math.Floor((end - start) / step + 1e-9));
        if (count > 10_000) throw new InvalidDataException("扫描点过多，请增大步长。");
        for (var i = 0; i <= count; i++) yield return start + i * step;
        if (start + count * step < end - 1e-9) yield return end;
    }
}
