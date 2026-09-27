using System.Globalization;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using MotorLoadBench.Domain;

namespace MotorLoadBench.UI;

public sealed class PerformanceCurveControl : FrameworkElement
{
    private IReadOnlyList<PointResult> _points = [];
    private PointResult? _hovered;
    private Point _mouse;
    public double TorqueMaxNm { get; set; } = 4.0;
    public double SpeedMaxRpm { get; set; } = 3500;
    public double CurrentMaxA { get; set; } = 35;
    public double PowerMaxW { get; set; } = 1050;
    public void SetPoints(IReadOnlyList<PointResult> points) { _points = points; InvalidateVisual(); }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e); _mouse = e.GetPosition(this); _hovered = HitTestPoint(_mouse); InvalidateVisual();
    }
    protected override void OnMouseLeave(MouseEventArgs e)
    {
        base.OnMouseLeave(e); _hovered = null; InvalidateVisual();
    }

    protected override void OnRender(DrawingContext dc)
    {
        dc.DrawRectangle(new SolidColorBrush(Color.FromRgb(10, 23, 38)), null, new Rect(RenderSize));
        var dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        void Text(string value, double x, double y, Brush brush, double size = 11) => dc.DrawText(
            new FormattedText(value, CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
                new Typeface("Microsoft YaHei UI"), size, brush, dpi), new Point(x, y));

        Text("综合性能曲线", 16, 10, Brushes.White, 15);
        Text("● DUT Speed", 150, 13, Brushes.DeepSkyBlue);
        Text("● DUT DC Bus Current", 270, 13, Brushes.Orange);
        Text("● DYN Mechanical Output Power", 455, 13, Brushes.LightGreen);
        Text("● DUT Efficiency", 700, 13, Brushes.MediumPurple);
        var plot = PlotRect();
        dc.DrawRectangle(null, new Pen(new SolidColorBrush(Color.FromRgb(49, 85, 107)), 1), plot);
        for (var i = 0; i <= 4; i++)
        {
            var x = plot.X + plot.Width * i / 4;
            dc.DrawLine(new Pen(new SolidColorBrush(Color.FromRgb(28, 51, 69)), 1), new Point(x, plot.Y), new Point(x, plot.Bottom));
            Text(i.ToString(), x - 3, plot.Bottom + 7, Brushes.LightGray, 9);
        }
        for (var i = 0; i <= 7; i++)
        {
            var y = plot.Bottom - plot.Height * i / 7;
            dc.DrawLine(new Pen(new SolidColorBrush(Color.FromRgb(28, 51, 69)), 1), new Point(plot.X, y), new Point(plot.Right, y));
            Text((i * 500).ToString(), 24, y - 6, Brushes.DeepSkyBlue, 8);
        }
        for (var i = 0; i <= 5; i++)
        {
            var y = plot.Bottom - plot.Height * i / 5;
            Text((CurrentMaxA * i / 5).ToString("F0", CultureInfo.InvariantCulture), plot.Right + 8, y - 6, Brushes.Orange, 9);
        }
        Text("DYN-200 Measured Torque / N·m", plot.X + plot.Width / 2 - 92, plot.Bottom + 23, Brushes.LightGray, 10);
        Text("RPM", 12, plot.Y - 2, Brushes.DeepSkyBlue, 9);
        Text("DC Bus Current / A", plot.Right - 36, plot.Y - 20, Brushes.Orange, 9);

        var valid = ValidPoints();
        if (valid.Length == 0)
        {
            Text("等待 VALID 测试点；Torque 0–4 N·m / Speed 0–3500 RPM / Ibus 0–35 A", plot.X + 24, plot.Y + 28, Brushes.SlateGray);
            return;
        }
        Draw(valid, valid.Select(p => p.Speed!.Mean).ToArray(), SpeedMaxRpm, Brushes.DeepSkyBlue);
        Draw(valid, valid.Select(p => p.DcBusCurrent?.Mean ?? double.NaN).ToArray(), CurrentMaxA, Brushes.Orange);
        Draw(valid, valid.Select(p => p.MechanicalPower?.Mean ?? double.NaN).ToArray(), PowerMaxW, Brushes.LightGreen);
        Draw(valid, valid.Select(p => p.Efficiency?.Mean ?? double.NaN).ToArray(), 120, Brushes.MediumPurple);
        if (_hovered != null) DrawTooltip(dc, Text, plot, _hovered);

        void Draw(PointResult[] points, double[] values, double maximum, Brush brush)
        {
            var geometry = new StreamGeometry(); using var context = geometry.Open(); var started = false;
            for (var i = 0; i < points.Length; i++)
            {
                if (!double.IsFinite(values[i])) continue;
                var point = ScreenPoint(plot, points[i], values[i], maximum);
                if (!started) { context.BeginFigure(point, false, false); started = true; } else context.LineTo(point, true, false);
                var selected = ReferenceEquals(points[i], _hovered);
                dc.DrawEllipse(new SolidColorBrush(Color.FromArgb(selected ? (byte)110 : (byte)45, 255, 255, 255)), new Pen(brush, selected ? 2.5 : 1.4), point, selected ? 6 : 3.5, selected ? 6 : 3.5);
            }
            dc.DrawGeometry(null, new Pen(brush, 1.7), geometry);
        }
    }

    private Rect PlotRect() => new(62, 44, Math.Max(10, ActualWidth - 112), Math.Max(10, ActualHeight - 82));
    private PointResult[] ValidPoints() => _points.Where(p => p.Result.StartsWith("PASS", StringComparison.Ordinal) && p.Torque != null && p.Speed != null).OrderBy(p => p.Torque!.Mean).ToArray();
    private Point ScreenPoint(Rect plot, PointResult point, double value, double maximum) => new(
        plot.X + Math.Clamp(point.Torque!.Mean / TorqueMaxNm, 0, 1) * plot.Width,
        plot.Bottom - Math.Clamp(value / maximum, 0, 1) * plot.Height);

    private PointResult? HitTestPoint(Point mouse)
    {
        var plot = PlotRect(); PointResult? best = null; var bestDistance = 12.0;
        foreach (var point in ValidPoints())
        {
            var series = new[] { (point.Speed?.Mean, SpeedMaxRpm), (point.DcBusCurrent?.Mean, CurrentMaxA),
                (point.MechanicalPower?.Mean, PowerMaxW), (point.Efficiency?.Mean, 120d) };
            foreach (var (value, maximum) in series)
            {
                if (value is not { } v || !double.IsFinite(v)) continue;
                var p = ScreenPoint(plot, point, v, maximum); var distance = (p - mouse).Length;
                if (distance < bestDistance) { bestDistance = distance; best = point; }
            }
        }
        return best;
    }

    private void DrawTooltip(DrawingContext dc, Action<string, double, double, Brush, double> text, Rect plot, PointResult p)
    {
        const double width = 250, height = 100;
        var x = Math.Min(_mouse.X + 14, plot.Right - width - 4); var y = Math.Max(plot.Y + 4, Math.Min(_mouse.Y - height - 8, plot.Bottom - height - 4));
        var box = new Rect(x, y, width, height);
        dc.DrawRoundedRectangle(new SolidColorBrush(Color.FromArgb(238, 17, 38, 55)), new Pen(Brushes.CadetBlue, 1), box, 4, 4);
        text($"{p.PointId}  {p.Result}", x + 9, y + 7, Brushes.White, 11);
        text($"Torque  {p.Torque?.Mean:F3} N·m    RPM  {p.Speed?.Mean:F1}", x + 9, y + 27, Brushes.LightCyan, 10);
        text($"DC Bus Current  {p.DcBusCurrent?.Mean:F2} A", x + 9, y + 46, Brushes.Orange, 10);
        text($"Pout  {p.MechanicalPower?.Mean:F1} W    Efficiency  {p.Efficiency?.Mean:F1} %", x + 9, y + 65, Brushes.LightGreen, 10);
    }
}
