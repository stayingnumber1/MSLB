using System.Globalization;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using MotorLoadBench.Domain;

namespace MotorLoadBench.UI;

// Speed (columns) x torque (rows) grid of the Map scan. A PASS cell is shaded by efficiency on a fixed
// 50-95 % scale; a click raises CellClicked (skip / restore, retest / undo).
public sealed class MapGridControl : FrameworkElement
{
    private const double EfficiencyLow = 50, EfficiencyHigh = 95;
    private static readonly Color Low = Color.FromRgb(27, 70, 78), Mid = Color.FromRgb(30, 143, 117), High = Color.FromRgb(120, 230, 200);
    private IReadOnlyList<MapCellSnapshot> _cells = [];
    private double[] _rpms = [];
    private double[] _torques = [];
    private MapCellSnapshot? _hover;
    private Point _mouse;
    public event Action<MapCellSnapshot>? CellClicked;
    // Drawn above the grid when set (exported PNG).
    public string? Title { get; set; }

    public void SetCells(IReadOnlyList<MapCellSnapshot> cells)
    {
        _cells = cells;
        _rpms = [.. cells.Select(c => c.Rpm).Distinct().Order()];
        _torques = [.. cells.Select(c => c.TorqueNm).Distinct().OrderDescending()];
        _hover = _hover == null ? null : cells.FirstOrDefault(c => c.Id == _hover.Id);
        InvalidateVisual();
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e); _mouse = e.GetPosition(this);
        _hover = HitTest(_mouse); Cursor = _hover == null ? null : Cursors.Hand; InvalidateVisual();
    }
    protected override void OnMouseLeave(MouseEventArgs e) { base.OnMouseLeave(e); _hover = null; InvalidateVisual(); }
    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonUp(e);
        if (HitTest(e.GetPosition(this)) is { } cell) CellClicked?.Invoke(cell);
    }

    protected override void OnRender(DrawingContext dc)
    {
        dc.DrawRectangle(new SolidColorBrush(Color.FromRgb(10, 23, 38)), null, new Rect(RenderSize));
        var dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        void Text(string value, double x, double y, Brush brush, double size = 11, bool center = false, FontWeight? weight = null)
        {
            var text = new FormattedText(value, CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
                new Typeface(new FontFamily("Microsoft YaHei UI"), FontStyles.Normal, weight ?? FontWeights.Normal, FontStretches.Normal), size, brush, dpi);
            dc.DrawText(text, new Point(center ? x - text.Width / 2 : x, center ? y - text.Height / 2 : y));
        }
        var muted = new SolidColorBrush(Color.FromRgb(159, 182, 195));
        if (_cells.Count == 0)
        {
            Text("还没有网格：填写转速列表后点「生成网格」，或点「载入 YKB-145 计划」", 24, 24, muted, 13);
            return;
        }
        var plot = PlotRect();
        if (!string.IsNullOrWhiteSpace(Title)) Text(Title, plot.X, 4, Brushes.White, 14, weight: FontWeights.SemiBold);
        Text("扭矩 N·m", 4, plot.Y - 18, muted, 10);
        Text("转速 rpm", plot.X + plot.Width / 2, plot.Bottom + 30, muted, 10, center: true);
        var cw = plot.Width / _rpms.Length; var ch = plot.Height / _torques.Length;
        for (var i = 0; i < _torques.Length; i++)
            Text(_torques[i].ToString("0.00", CultureInfo.InvariantCulture), plot.X - 8 - 30, plot.Y + ch * (i + .5) - 7, muted, 11);
        for (var j = 0; j < _rpms.Length; j++)
            Text(_rpms[j].ToString("0", CultureInfo.InvariantCulture), plot.X + cw * (j + .5), plot.Bottom + 12, muted, 11, center: true);

        foreach (var cell in _cells)
        {
            var r = CellRect(plot, cell);
            var showText = r.Width >= 34 && r.Height >= 16;
            var glyphSize = Math.Clamp(r.Height * .55, 9, 14);
            switch (cell.State)
            {
                case MapCellState.Pass:
                    var efficiency = cell.Result?.Efficiency?.Mean;
                    var fill = efficiency is { } e ? Shade(e) : Color.FromRgb(44, 110, 98);
                    dc.DrawRoundedRectangle(new SolidColorBrush(fill), null, r, 3, 3);
                    if (showText) Text(efficiency is { } v ? v.ToString("F1", CultureInfo.InvariantCulture) : "—", r.X + r.Width / 2, r.Y + r.Height / 2,
                        Luminance(fill) > .45 ? new SolidColorBrush(Color.FromRgb(8, 32, 30)) : Brushes.White, glyphSize, center: true);
                    if (cell.Result?.Result == "PASS_WITH_WARNING") dc.DrawEllipse(Brushes.Orange, null, new Point(r.Right - 5, r.Y + 5), 3, 3);
                    break;
                case MapCellState.Running:
                    dc.DrawRoundedRectangle(new SolidColorBrush(Color.FromRgb(18, 56, 74)), new Pen(new SolidColorBrush(Color.FromRgb(41, 195, 230)), 2), r, 3, 3);
                    break;
                case MapCellState.Unreachable:
                    dc.DrawRoundedRectangle(new SolidColorBrush(Color.FromRgb(42, 50, 59)), null, r, 3, 3);
                    Text("×", r.X + r.Width / 2, r.Y + r.Height / 2, new SolidColorBrush(Color.FromRgb(138, 154, 168)), glyphSize + 2, center: true);
                    break;
                case MapCellState.Failed:
                    dc.DrawRoundedRectangle(new SolidColorBrush(Color.FromRgb(59, 31, 34)), new Pen(new SolidColorBrush(Color.FromRgb(240, 75, 54)), 1.2), r, 3, 3);
                    Text("!", r.X + r.Width / 2, r.Y + r.Height / 2, new SolidColorBrush(Color.FromRgb(240, 75, 54)), glyphSize, center: true, weight: FontWeights.Bold);
                    break;
                case MapCellState.Skipped:
                    dc.DrawRoundedRectangle(new SolidColorBrush(Color.FromRgb(22, 34, 46)), null, r, 3, 3);
                    Text("跳过", r.X + r.Width / 2, r.Y + r.Height / 2, new SolidColorBrush(Color.FromRgb(94, 115, 132)), Math.Min(glyphSize, 11), center: true);
                    break;
                default:
                    var pen = cell.Retest
                        ? new Pen(new SolidColorBrush(Color.FromRgb(243, 200, 132)), 1.5) { DashStyle = DashStyles.Dash }
                        : new Pen(new SolidColorBrush(Color.FromRgb(62, 99, 120)), 1);
                    dc.DrawRoundedRectangle(null, pen, r, 3, 3);
                    if (cell.Retest && showText) Text("重测", r.X + r.Width / 2, r.Y + r.Height / 2, new SolidColorBrush(Color.FromRgb(243, 200, 132)), Math.Min(glyphSize, 11), center: true);
                    break;
            }
            if (ReferenceEquals(cell, _hover) || cell.Id == _hover?.Id)
                dc.DrawRoundedRectangle(null, new Pen(Brushes.White, 1.5), r, 3, 3);
        }
        DrawLegend(dc, Text, plot);
        if (_hover != null) DrawTooltip(dc, Text, plot, _hover);
    }

    private void DrawLegend(DrawingContext dc, Action<string, double, double, Brush, double, bool, FontWeight?> text, Rect plot)
    {
        var y = ActualHeight - 16; var x = plot.X;
        var muted = new SolidColorBrush(Color.FromRgb(159, 182, 195));
        var gradient = new LinearGradientBrush(new GradientStopCollection { new(Low, 0), new(Mid, .5), new(High, 1) }, 0);
        dc.DrawRectangle(gradient, null, new Rect(x, y, 90, 9));
        text($"效率 {EfficiencyLow:F0}–{EfficiencyHigh:F0} %", x + 96, y - 3, muted, 10, false, null); x += 190;
        void Item(Brush? fill, Pen? pen, string label, string glyph = "")
        {
            dc.DrawRoundedRectangle(fill, pen, new Rect(x, y - 1, 14, 11), 2, 2);
            if (glyph.Length > 0) text(glyph, x + 7, y + 4, muted, 9, true, null);
            text(label, x + 19, y - 3, muted, 10, false, null); x += 30 + label.Length * 11;
        }
        Item(null, new Pen(new SolidColorBrush(Color.FromRgb(62, 99, 120)), 1), "待测");
        Item(new SolidColorBrush(Color.FromRgb(18, 56, 74)), new Pen(new SolidColorBrush(Color.FromRgb(41, 195, 230)), 2), "当前");
        Item(new SolidColorBrush(Color.FromRgb(42, 50, 59)), null, "不可达", "×");
        Item(new SolidColorBrush(Color.FromRgb(59, 31, 34)), new Pen(new SolidColorBrush(Color.FromRgb(240, 75, 54)), 1), "失败/不稳", "!");
        Item(new SolidColorBrush(Color.FromRgb(22, 34, 46)), null, "跳过");
        Item(null, new Pen(new SolidColorBrush(Color.FromRgb(243, 200, 132)), 1.5) { DashStyle = DashStyles.Dash }, "重测");
    }

    private void DrawTooltip(DrawingContext dc, Action<string, double, double, Brush, double, bool, FontWeight?> text, Rect plot, MapCellSnapshot c)
    {
        var r = c.Result;
        var lines = new List<string>
        {
            $"{c.Id}   {State(c)}",
            r?.Torque == null ? "尚无测量" : string.Create(CultureInfo.InvariantCulture, $"DYN {r.Torque.Mean:F3} N·m   DUT {r.Speed?.Mean:F0} rpm   η {r.Efficiency?.Mean:F1} %"),
        };
        if (r?.InputPower != null || r?.MechanicalPower != null)
            lines.Add(string.Create(CultureInfo.InvariantCulture, $"Pin {r?.InputPower?.Mean:F1} W   Pout {r?.MechanicalPower?.Mean:F1} W   Duty {r?.Duty?.Mean:F0} %   {r?.Temperature?.Mean:F0} °C"));
        if (!string.IsNullOrWhiteSpace(c.Reason)) lines.Add(c.Reason.Length > 60 ? c.Reason[..57] + "..." : c.Reason);
        lines.Add(c.State switch
        {
            MapCellState.Running => "正在测量",
            MapCellState.Pending when c.Retest => "单击：撤销重测",
            MapCellState.Pending => "单击：跳过",
            MapCellState.Skipped => "单击：恢复待测",
            _ => "单击：加入重测"
        });
        const double width = 360; var height = 12 + lines.Count * 19;
        var x = Math.Min(_mouse.X + 16, ActualWidth - width - 4); var y = Math.Max(4, Math.Min(_mouse.Y - height - 10, ActualHeight - height - 4));
        dc.DrawRoundedRectangle(new SolidColorBrush(Color.FromArgb(240, 17, 38, 55)), new Pen(Brushes.CadetBlue, 1), new Rect(x, y, width, height), 4, 4);
        for (var i = 0; i < lines.Count; i++)
            text(lines[i], x + 10, y + 6 + i * 19, i == 0 ? Brushes.White : i == lines.Count - 1 ? Brushes.LightSkyBlue : Brushes.LightGray, i == 0 ? 12 : 11, false, null);
    }

    private static string State(MapCellSnapshot c) => c.State switch
    {
        MapCellState.Pass => c.Result?.Result ?? "PASS",
        MapCellState.Pending when c.Retest => "待重测",
        MapCellState.Pending => "待测",
        MapCellState.Running => "测量中",
        MapCellState.Unreachable => "不可达",
        MapCellState.Failed => "失败",
        MapCellState.Skipped => "跳过",
        _ => c.State.ToString()
    };

    private Rect PlotRect() { var top = string.IsNullOrWhiteSpace(Title) ? 20 : 48; return new(52, top, Math.Max(10, ActualWidth - 64), Math.Max(10, ActualHeight - 64 - top)); }
    private Rect CellRect(Rect plot, MapCellSnapshot cell)
    {
        var cw = plot.Width / _rpms.Length; var ch = plot.Height / _torques.Length;
        var j = Array.FindIndex(_rpms, r => Math.Abs(r - cell.Rpm) < 1e-6);
        var i = Array.FindIndex(_torques, t => Math.Abs(t - cell.TorqueNm) < 1e-9);
        var pad = Math.Clamp(Math.Min(cw, ch) * .08, 1.5, 4);
        return new Rect(plot.X + cw * j + pad, plot.Y + ch * i + pad, Math.Max(1, cw - 2 * pad), Math.Max(1, ch - 2 * pad));
    }
    private MapCellSnapshot? HitTest(Point p)
    {
        if (_cells.Count == 0) return null;
        var plot = PlotRect();
        return _cells.FirstOrDefault(c => CellRect(plot, c).Contains(p));
    }
    private static Color Shade(double efficiency)
    {
        var t = Math.Clamp((efficiency - EfficiencyLow) / (EfficiencyHigh - EfficiencyLow), 0, 1);
        return t < .5 ? Lerp(Low, Mid, t * 2) : Lerp(Mid, High, (t - .5) * 2);
    }
    private static Color Lerp(Color a, Color b, double t) => Color.FromRgb((byte)(a.R + (b.R - a.R) * t), (byte)(a.G + (b.G - a.G) * t), (byte)(a.B + (b.B - a.B) * t));
    private static double Luminance(Color c) => (.2126 * c.R + .7152 * c.G + .0722 * c.B) / 255;
}
