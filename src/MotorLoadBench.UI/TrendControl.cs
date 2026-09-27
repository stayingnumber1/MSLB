using System.Windows;
using System.Windows.Media;
using MotorLoadBench.Domain;
namespace MotorLoadBench.UI;

public sealed class TrendControl : FrameworkElement
{
    private readonly List<BenchSnapshot> _samples = [];
    public int WindowSeconds { get; set; } = 60;
    public void Push(BenchSnapshot s)
    {
        _samples.Add(s);
        if (_samples.Count > 12000) { var compact = _samples.Where((_, i) => i % 2 == 0).ToList(); _samples.Clear(); _samples.AddRange(compact); }
        InvalidateVisual();
    }
    protected override void OnRender(DrawingContext dc)
    {
        base.OnRender(dc);
        dc.DrawRectangle(new SolidColorBrush(Color.FromRgb(10, 23, 38)), null, new Rect(RenderSize));
        if (_samples.Count < 2 || ActualWidth < 100 || ActualHeight < 90) return;
        var end = _samples[^1].Timestamp;
        var rows = WindowSeconds == 0 ? _samples : _samples.Where(s => (end - s.Timestamp).TotalSeconds <= WindowSeconds).ToList();
        if (rows.Count < 2) return;
        var start = rows[0].Timestamp;
        var span = Math.Max(.01, (end - start).TotalSeconds);
        DrawTrack("DUT RPM", rows.Select(s => s.DutSpeedRpm ?? s.ExternalSpeedRpm ?? s.SpeedRpm).ToArray(), Colors.DeepSkyBlue, 0);
        DrawTrack("Torque N·m: DYN measured / servo target",
            rows.Select(s => s.ExternalHealthy && s.ExternalTorqueNm is { } torque ? torque : double.NaN).ToArray(),
            Colors.MediumAquamarine, 1, rows.Select(s => s.TargetTorqueNm).ToArray());
        DrawTrack("Power W: Pin / Pout", rows.Select(s => s.InputPowerW ?? 0).ToArray(), Colors.MediumPurple, 2,
            rows.Select(s => s.MeasuredPowerW ?? 0).ToArray());
        void DrawTrack(string label, double[] values, Color color, int track, double[]? second = null)
        {
            var all = (second == null ? values : values.Concat(second)).Where(double.IsFinite).ToArray();
            if (all.Length == 0) return;
            var min = Math.Min(0, all.Min()); var max = Math.Max(.01, all.Max()); var range = Math.Max(.01, max - min);
            var height = ActualHeight / 3; var top = track * height;
            var text = new FormattedText(label + $"  [{min:F2}, {max:F2}]", System.Globalization.CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
                new Typeface("Consolas"), 11, new SolidColorBrush(color), VisualTreeHelper.GetDpi(this).PixelsPerDip);
            dc.DrawText(text, new Point(10, top + 4));
            for (int j = 1; j <= 3; j++) dc.DrawLine(new Pen(new SolidColorBrush(Color.FromRgb(29, 48, 66)), 1), new Point(8, top + 22 + (height - 30) * j / 3), new Point(ActualWidth - 8, top + 22 + (height - 30) * j / 3));
            Line(values, new SolidColorBrush(color)); if (second != null) Line(second, Brushes.LightSlateGray);
            void Line(double[] array, Brush brush)
            {
                var geometry = new StreamGeometry();
                using (var ctx = geometry.Open())
                {
                    var figureOpen = false;
                    for (int i = 0; i < rows.Count; i++)
                    {
                        if (!double.IsFinite(array[i])) { figureOpen = false; continue; }
                        var p = new Point(8 + (rows[i].Timestamp - start).TotalSeconds / span * (ActualWidth - 16),
                            top + 22 + (1 - (array[i] - min) / range) * (height - 30));
                        if (!figureOpen) { ctx.BeginFigure(p, false, false); figureOpen = true; }
                        else ctx.LineTo(p, true, false);
                    }
                }
                geometry.Freeze(); dc.DrawGeometry(null, new Pen(brush, 1.4), geometry);
            }
        }
    }
}
