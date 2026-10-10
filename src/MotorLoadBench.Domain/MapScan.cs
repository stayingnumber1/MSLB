using System.Globalization;

namespace MotorLoadBench.Domain;

// Speed x torque Map scan: the DUT speed loop holds each column speed, the load servo (CST)
// steps the torque. Plan = what to measure; MapScanGrid = per-cell progress shared by the
// orchestrator thread and the UI (skip / retest clicks).
public enum MapLoadMode { Staircase, ReturnToZero }
public enum MapColumnOrder { Ascending, Interleaved }
// DynClosedLoop: the servo command is adjusted until the DYN-200 shaft torque reads the target. On this
// bench DYN ~= 1.12 x servo command + ~0.25 N·m servo-side drag at 3600-3900 rpm (P1 history 2026-10),
// so ServoCommand targets land 0.3-0.6 N·m above the plan's (DYN) torque.
public enum MapTorqueReference { DynClosedLoop, ServoCommand }
public enum MapCellState { Pending, Running, Pass, Unreachable, Failed, Skipped }

public record MapColumn(double Rpm, double MaxTorqueNm);

public record MapScanPlan
{
    public int Version { get; init; } = 1;
    public string Name { get; init; } = "Map 扫描";
    public List<MapColumn> Columns { get; init; } = [];
    public double StartTorqueNm { get; init; } = .25;
    public double TorqueStepNm { get; init; } = .25;
    public double SampleSeconds { get; init; } = 2;
    public double TorqueRampNmPerSec { get; init; } = .3;
    public double SpeedRampRpmPerSec { get; init; } = 500;
    public double StableTimeoutSeconds { get; init; } = 10;
    // Rolling window for the WAIT_STABLE check (P1 uses its own AutoTest window).
    public double StabilityWindowSeconds { get; init; } = .2;
    public MapLoadMode LoadMode { get; init; } = MapLoadMode.Staircase;
    public MapColumnOrder Order { get; init; } = MapColumnOrder.Interleaved;
    public MapTorqueReference TorqueReference { get; init; } = MapTorqueReference.DynClosedLoop;
    public double PauseTempC { get; init; } = 90;
    public double ResumeTempC { get; init; } = 60;
    // 0 = stop the DUT while cooling or paused.
    public double CoolingRpm { get; init; } = 800;
    public double CoolingTimeoutSeconds { get; init; } = 1800;
    public double SpeedDropFraction { get; init; } = .03;
    // Counted only after the torque ramp, restarted while the speed recovers.
    public double SpeedDropConfirmSeconds { get; init; } = 2;
    public double DutySaturationPct { get; init; } = 93;
    // Below this fraction of the column speed the load is removed at once (no confirm time).
    public double SpeedCollapseFraction { get; init; } = .5;
    public double SpeedSettleSeconds { get; init; } = 10;
    // DYN closed loop: the servo command may exceed the cell torque by at most this much (it may always go down to 0).
    public double DynCorrectionMaxNm { get; init; } = .1;
}

public static class MapScanPlanner
{
    // YKB-145 expanded fixed-point plan (docs/image.png): 13 speeds, 0.25 N·m steps, 117 points.
    public static MapScanPlan Ykb145() => new()
    {
        Name = "YKB-145 定点 Map",
        Columns =
        [
            new(650, 3.00), new(800, 3.00), new(1000, 2.75), new(1250, 2.75), new(1500, 2.50), new(1750, 2.50),
            new(2000, 2.25), new(2300, 2.00), new(2600, 2.00), new(2750, 2.00), new(3000, 1.75), new(3250, 1.75), new(3500, 1.00)
        ]
    };

    public static string PointId(double rpm, double torqueNm) =>
        string.Create(CultureInfo.InvariantCulture, $"R{rpm:0000}_T{torqueNm:0.00}").Replace('.', '_');
    public static string NoLoadId(double rpm) => string.Create(CultureInfo.InvariantCulture, $"R{rpm:0000}_NL");

    public static IReadOnlyList<double> Torques(MapScanPlan plan, MapColumn column)
    {
        var values = new List<double>();
        for (var i = 0; ; i++)
        {
            var torque = Math.Round(plan.StartTorqueNm + i * plan.TorqueStepNm, 4);
            if (torque > column.MaxTorqueNm + 1e-9) break;
            if (values.Count >= 200) throw new InvalidDataException($"{column.Rpm:F0} rpm 列扭矩点过多，请增大步长。");
            values.Add(torque);
        }
        return values;
    }

    // Interleaved = lowest, highest, 2nd lowest, 2nd highest ... so the hot low-speed / high-torque
    // columns are not run back to back.
    public static IReadOnlyList<double> OrderedSpeeds(MapScanPlan plan)
    {
        var sorted = plan.Columns.Select(c => c.Rpm).Order().ToList();
        if (plan.Order == MapColumnOrder.Ascending) return sorted;
        var ordered = new List<double>(sorted.Count);
        for (int low = 0, high = sorted.Count - 1; low <= high; low++, high--)
        {
            ordered.Add(sorted[low]);
            if (high != low) ordered.Add(sorted[high]);
        }
        return ordered;
    }

    // "650:3, 800:3, 1000" -> columns; an entry without ":cap" takes defaultCapNm.
    public static List<MapColumn> ParseColumns(string text, double defaultCapNm)
    {
        var columns = new List<MapColumn>();
        foreach (var token in (text ?? "").Split([',', '，', ';', '；', '\r', '\n', '\t', ' '], StringSplitOptions.RemoveEmptyEntries))
        {
            var parts = token.Split([':', '：']);
            if (parts.Length > 2 || !double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var rpm))
                throw new InvalidDataException($"转速列表项无效：{token}（格式 转速 或 转速:上限）");
            var cap = defaultCapNm;
            if (parts.Length == 2 && !double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out cap))
                throw new InvalidDataException($"扭矩上限无效：{token}");
            if (columns.Any(c => Math.Abs(c.Rpm - rpm) < 1e-6)) throw new InvalidDataException($"转速重复：{rpm:G}");
            columns.Add(new(rpm, cap));
        }
        if (columns.Count == 0) throw new InvalidDataException("转速列表为空。");
        return [.. columns.OrderBy(c => c.Rpm)];
    }

    public static string FormatColumns(IEnumerable<MapColumn> columns) =>
        string.Join(", ", columns.OrderBy(c => c.Rpm).Select(c => string.Create(CultureInfo.InvariantCulture, $"{c.Rpm:0.##}:{c.MaxTorqueNm:0.00}")));

    public static void Validate(MapScanPlan p, BenchConfig config)
    {
        var l = config.Limits;
        void Require(bool ok, string message) { if (!ok) throw new InvalidDataException("Map 扫描参数无效：" + message); }
        double[] finite = [p.StartTorqueNm, p.TorqueStepNm, p.SampleSeconds, p.TorqueRampNmPerSec, p.SpeedRampRpmPerSec,
            p.StableTimeoutSeconds, p.StabilityWindowSeconds, p.PauseTempC, p.ResumeTempC, p.CoolingRpm, p.CoolingTimeoutSeconds, p.SpeedDropFraction,
            p.SpeedDropConfirmSeconds, p.DutySaturationPct, p.SpeedCollapseFraction, p.SpeedSettleSeconds, p.DynCorrectionMaxNm];
        Require(p.Version == 1, "版本不支持");
        Require(finite.All(double.IsFinite), "含非有限数值");
        Require(p.Columns.Count is >= 1 and <= 64, "转速列数需在 1–64 之间");
        Require(p.StartTorqueNm > 0 && p.TorqueStepNm >= .01, "起始扭矩需 > 0，步长 ≥ 0.01 N·m");
        Require(p.SampleSeconds is >= .5 and <= 30, "采样时间需在 0.5–30 s");
        Require(p.TorqueRampNmPerSec > 0 && p.TorqueRampNmPerSec <= l.MaxRampNmPerSec, $"扭矩斜坡需在 0–{l.MaxRampNmPerSec:F2} N·m/s");
        Require(p.SpeedRampRpmPerSec is >= 50 and <= 5000, "升速斜坡需在 50–5000 rpm/s");
        Require(p.StableTimeoutSeconds is >= 2 and <= 60, "稳定超时需在 2–60 s");
        Require(p.StabilityWindowSeconds is >= .1 and <= 2, "稳定判定窗口需在 0.1–2 s");
        Require(p.ResumeTempC > 0 && p.ResumeTempC < p.PauseTempC && p.PauseTempC < l.DutMotorTripC,
            $"温控需满足 0 < 恢复 < 暂停 < 硬跳闸 {l.DutMotorTripC:F0} °C");
        Require(p.CoolingTimeoutSeconds is >= 60 and <= 7200, "冷却超时需在 60–7200 s");
        Require(p.SpeedDropFraction is > 0 and <= .3, "转速跌落判据需在 0–30%");
        Require(p.SpeedCollapseFraction is >= .1 and < .95 && p.SpeedCollapseFraction < 1 - p.SpeedDropFraction, "转速塌陷判据需低于跌落判据");
        Require(p.SpeedDropConfirmSeconds is >= .05 and <= 5, "跌落确认时间需在 0.05–5 s");
        Require(p.DutySaturationPct is >= 50 and <= 100, "Duty 判据需在 50–100%");
        Require(p.SpeedSettleSeconds is >= 2 and <= 60, "转速稳定超时需在 2–60 s");
        Require(p.DynCorrectionMaxNm is >= 0 and <= .5, "DYN 修正幅度需在 0–0.5 N·m");
        var maxRpm = Math.Min(l.MaxSpeedRpm, config.Vesc.MaxRpm);
        Require(p.CoolingRpm == 0 || (p.CoolingRpm > l.MinLoadSpeedRpm && p.CoolingRpm < maxRpm), "冷却转速需为 0 或在加载转速范围内");
        // The servo command can exceed the cell torque by the DYN correction.
        var margin = p.TorqueReference == MapTorqueReference.DynClosedLoop ? p.DynCorrectionMaxNm : 0;
        foreach (var c in p.Columns)
        {
            Require(double.IsFinite(c.Rpm) && double.IsFinite(c.MaxTorqueNm), "转速或上限含非有限数值");
            Require(c.Rpm > l.MinLoadSpeedRpm && c.Rpm < maxRpm, $"{c.Rpm:G} rpm 超出 {l.MinLoadSpeedRpm:F0}–{maxRpm:F0} rpm");
            Require(c.MaxTorqueNm >= p.StartTorqueNm, $"{c.Rpm:G} rpm 列上限低于起始扭矩");
            var envelope = Math.Min(l.MaxTorqueNm, LoadMath.Interpolate(l.TorqueEnvelope, c.Rpm));
            Require(c.MaxTorqueNm + margin <= envelope + 1e-9, $"{c.Rpm:G} rpm 列上限 {c.MaxTorqueNm + margin:F2} N·m 超过伺服扭矩上限 {envelope:F2}");
            // Points hold the torque for seconds (a staircase column for its whole upper part): above the
            // continuous torque the servo bridge would clip to it after PeakSeconds.
            Require(c.MaxTorqueNm + margin <= l.ContinuousTorqueNm + 1e-9,
                $"{c.Rpm:G} rpm 列上限 {c.MaxTorqueNm + margin:F2} N·m 超过伺服连续扭矩 {l.ContinuousTorqueNm:F2}（峰值只允许 {l.PeakSeconds:F0} s）");
            Require((c.MaxTorqueNm + margin) * c.Rpm * Math.PI / 30 <= l.MaxPowerW, $"{c.Rpm:G} rpm × {c.MaxTorqueNm + margin:F2} N·m 超过功率上限 {l.MaxPowerW:F0} W");
            _ = Torques(p, c);
        }
        Require(p.Columns.Select(c => c.Rpm).Distinct().Count() == p.Columns.Count, "转速重复");
    }
}

public sealed record MapCellSnapshot(string Id, double Rpm, double TorqueNm, MapCellState State, string? Reason,
    PointResult? Result, bool Retest, int Attempts);
public sealed record MapCellRecord(double Rpm, double TorqueNm, MapCellState State, string? Reason, PointResult? Result, int Attempts);
public sealed record MapScanState(MapScanPlan Plan, List<MapCellRecord> Cells, DateTimeOffset SavedUtc, string? MotorModel = null,
    List<PointResult>? NoLoad = null);

public sealed class MapScanGrid
{
    private sealed class Cell(string id, double rpm, double torque)
    {
        public string Id { get; } = id;
        public double Rpm { get; } = rpm;
        public double TorqueNm { get; } = torque;
        public MapCellState State { get; set; }
        public string? Reason { get; set; }
        public PointResult? Result { get; set; }
        public int Attempts { get; set; }
        // A finished cell clicked for retest goes back to Pending; a second click restores this.
        public MapCellState? RetestFrom { get; set; }
        public MapCellSnapshot View() => new(Id, Rpm, TorqueNm, State, Reason, Result, RetestFrom != null, Attempts);
    }

    private readonly object _gate = new();
    private readonly List<Cell> _cells = [];
    private readonly Dictionary<string, Cell> _byId = [];
    private int _version;
    public MapScanPlan Plan { get; private set; }
    public int Version => Volatile.Read(ref _version);

    public MapScanGrid(MapScanPlan plan)
    {
        Plan = plan;
        foreach (var column in plan.Columns.OrderBy(c => c.Rpm))
            foreach (var torque in MapScanPlanner.Torques(plan, column))
            {
                var cell = new Cell(MapScanPlanner.PointId(column.Rpm, torque), column.Rpm, torque);
                _cells.Add(cell); _byId[cell.Id] = cell;
            }
    }

    public static MapScanGrid FromState(MapScanState state)
    {
        var grid = new MapScanGrid(state.Plan);
        foreach (var record in state.Cells)
        {
            if (!grid._byId.TryGetValue(MapScanPlanner.PointId(record.Rpm, record.TorqueNm), out var cell)) continue;
            // A cell that was running when the state was saved has no result: measure it again.
            cell.State = record.State == MapCellState.Running ? MapCellState.Pending : record.State;
            cell.Reason = record.Reason; cell.Result = record.Result; cell.Attempts = record.Attempts;
        }
        return grid;
    }

    public MapScanState ToState(string? motorModel)
    {
        lock (_gate)
            return new(Plan, [.. _cells.Select(c => new MapCellRecord(c.Rpm, c.TorqueNm, c.State, c.Reason, c.Result, c.Attempts))],
                DateTimeOffset.UtcNow, motorModel);
    }

    // Keeps the cells (columns, start and step) and takes every other setting from plan.
    public void ReplaceSettings(MapScanPlan plan)
    {
        lock (_gate) Plan = plan with { Columns = Plan.Columns, StartTorqueNm = Plan.StartTorqueNm, TorqueStepNm = Plan.TorqueStepNm, Name = Plan.Name };
        Touch();
    }

    public IReadOnlyList<MapCellSnapshot> Snapshot() { lock (_gate) return [.. _cells.Select(c => c.View())]; }
    public int Total { get { lock (_gate) return _cells.Count; } }
    public int Count(MapCellState state) { lock (_gate) return _cells.Count(c => c.State == state); }
    public bool HasResults { get { lock (_gate) return _cells.Any(c => c.State is MapCellState.Pass or MapCellState.Unreachable or MapCellState.Failed); } }

    // Pending -> Skipped -> Pending; a finished cell -> Pending (retest) -> back to its result. Running: no change.
    public bool Toggle(string id)
    {
        lock (_gate)
        {
            if (!_byId.TryGetValue(id, out var c) || c.State == MapCellState.Running) return false;
            switch (c.State)
            {
                case MapCellState.Pending when c.RetestFrom is { } previous: c.State = previous; c.RetestFrom = null; break;
                case MapCellState.Pending: c.State = MapCellState.Skipped; break;
                case MapCellState.Skipped: c.State = MapCellState.Pending; break;
                default: c.RetestFrom = c.State; c.State = MapCellState.Pending; c.Attempts = 0; break;
            }
        }
        Touch(); return true;
    }

    public double? NextColumn()
    {
        lock (_gate)
            foreach (var rpm in MapScanPlanner.OrderedSpeeds(Plan))
                if (_cells.Any(c => SameRpm(c, rpm) && c.State == MapCellState.Pending)) return rpm;
        return null;
    }

    public double? NextPendingTorque(double rpm)
    {
        lock (_gate)
            return _cells.Where(c => SameRpm(c, rpm) && c.State == MapCellState.Pending).OrderBy(c => c.TorqueNm)
                .Select(c => (double?)c.TorqueNm).FirstOrDefault();
    }

    public int PendingCount(double rpm) { lock (_gate) return _cells.Count(c => SameRpm(c, rpm) && c.State == MapCellState.Pending); }

    public void SetRunning(double rpm, double torque) => Update(rpm, torque, c => { c.State = MapCellState.Running; c.Attempts++; });

    public void Complete(double rpm, double torque, MapCellState state, string? reason, PointResult? result) =>
        Update(rpm, torque, c => { c.State = state; c.Reason = reason; c.RetestFrom = null; if (result != null) c.Result = result; });

    // Thermal or pause interruption: back to Pending without a result; returns the attempt count.
    public int Defer(double rpm, double torque, string reason)
    {
        var attempts = 0;
        Update(rpm, torque, c => { c.State = MapCellState.Pending; c.Reason = reason; attempts = c.Attempts; });
        return attempts;
    }

    // Marks every still-pending cell of the column above torqueNm (or all of them when torqueNm is null).
    public int MarkPendingUnreachable(double rpm, double? torqueNm, string reason)
    {
        int count;
        lock (_gate)
        {
            var targets = _cells.Where(c => SameRpm(c, rpm) && c.State == MapCellState.Pending && (torqueNm is not { } t || c.TorqueNm > t + 1e-9)).ToList();
            foreach (var c in targets) { c.State = MapCellState.Unreachable; c.Reason = reason; c.RetestFrom = null; }
            count = targets.Count;
        }
        if (count > 0) Touch();
        return count;
    }

    public void ClearRunning()
    {
        bool changed;
        lock (_gate)
        {
            var running = _cells.Where(c => c.State == MapCellState.Running).ToList();
            foreach (var c in running) c.State = MapCellState.Pending;
            changed = running.Count > 0;
        }
        if (changed) Touch();
    }

    private void Update(double rpm, double torque, Action<Cell> change)
    {
        lock (_gate)
        {
            var c = _byId.GetValueOrDefault(MapScanPlanner.PointId(rpm, torque)) ?? throw new InvalidOperationException($"网格中没有点 {rpm}/{torque}");
            change(c);
        }
        Touch();
    }
    private static bool SameRpm(Cell c, double rpm) => Math.Abs(c.Rpm - rpm) < 1e-6;
    private void Touch() => Interlocked.Increment(ref _version);
}
