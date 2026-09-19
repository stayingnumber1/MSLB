using System.ComponentModel;
using System.Windows;
using System.Windows.Threading;
namespace MotorLoadBench.UI;
public partial class MainWindow : Window
{
    private readonly MainViewModel _vm = new();
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromMilliseconds(50) };
    private bool _closing;
    public MainWindow()
    {
        InitializeComponent(); DataContext = _vm; Loaded += SmokeIfRequested; Loaded += ConnectEtherCatIfRequested;
        _timer.Tick += (_, _) => { _vm.Refresh(); if (_vm.Snapshot is { Connected: true } s) { Trend.WindowSeconds = _vm.TrendWindowIndex switch { 0 => 10, 1 => 60, _ => 0 }; Trend.Push(s); } };
        _timer.Start(); Closing += OnClosing;
    }
    private async void ConnectEtherCatIfRequested(object sender, RoutedEventArgs e)
    {
        if (!Environment.GetCommandLineArgs().Contains("--ethercat-connect")) return;
        await _vm.ConnectEtherCatReadOnlyAsync();
    }
    private async void SmokeIfRequested(object sender, RoutedEventArgs e)
    {
        if (!Environment.GetCommandLineArgs().Contains("--ui-smoke")) return;
        Hide();
        var dir = System.IO.Path.Combine(Environment.CurrentDirectory, "artifacts", "ui-smoke");
        System.IO.Directory.CreateDirectory(dir);
        try
        {
            await _vm.SmokeAsync();
            var visual = (FrameworkElement)Content;
            visual.Measure(new Size(1370, 850)); visual.Arrange(new Rect(0, 0, 1370, 850)); visual.UpdateLayout();
            var bitmap = new System.Windows.Media.Imaging.RenderTargetBitmap(1370, 850, 96, 96, System.Windows.Media.PixelFormats.Pbgra32);
            var drawing = new System.Windows.Media.DrawingVisual();
            using (var context = drawing.RenderOpen()) { context.DrawRectangle(new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(9,20,33)), null, new Rect(0,0,1370,850)); context.DrawRectangle(new System.Windows.Media.VisualBrush(visual), null, new Rect(0,0,1370,850)); }
            bitmap.Render(drawing);
            var encoder = new System.Windows.Media.Imaging.PngBitmapEncoder(); encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bitmap));
            using (var file = System.IO.File.Create(System.IO.Path.Combine(dir, "dashboard.png"))) encoder.Save(file);
            if (!await _vm.CloseAsync()) throw new InvalidOperationException("UI close failed");
            await System.IO.File.WriteAllTextAsync(System.IO.Path.Combine(dir, "result.txt"), "PASS: WPF loaded, mock connected, servo enabled, 0.1 Nm achieved, screenshot saved, stop and disable confirmed.");
            _closing = true; _timer.Stop(); System.Windows.Application.Current.Shutdown(0);
        }
        catch (Exception ex)
        {
            await System.IO.File.WriteAllTextAsync(System.IO.Path.Combine(dir, "result.txt"), "FAIL: " + ex);
            _closing = true; System.Windows.Application.Current.Shutdown(1);
        }
    }
    private async void OnClosing(object? sender, CancelEventArgs e)
    {
        if (_closing) return;
        e.Cancel = true;
        var ok = await _vm.CloseAsync();
        if (ok) { _closing = true; _timer.Stop(); Close(); }
    }
}
