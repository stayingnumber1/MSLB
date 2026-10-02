using System.ComponentModel;
using System.Windows;
using System.Windows.Threading;
using System.Windows.Media.Imaging;
namespace MotorLoadBench.UI;
public partial class MainWindow : Window
{
    private readonly MainViewModel _vm = new();
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromMilliseconds(50) };
    private bool _closing;
    public MainWindow()
    {
        InitializeComponent(); DataContext = _vm; Loaded += SmokeIfRequested; Loaded += ConnectEtherCatIfRequested; Loaded += CaptureServoEnableVescIfRequested; Loaded += CaptureDutRunIfRequested;
        _vm.ExportVisualsRequested += ExportVisuals;
        _timer.Tick += (_, _) => { _vm.Refresh(); if (_vm.Snapshot is { Connected: true } s) { Trend.WindowSeconds = _vm.TrendWindowIndex switch { 0 => 10, 1 => 60, _ => 0 }; Trend.Push(s); } PerformanceCurve.SetPoints(_vm.TestResults); };
        _timer.Start(); Closing += OnClosing;
    }
    private void ExportVisuals(string directory, string motorModelTag)
    {
        SaveVisual(PerformanceCurve, System.IO.Path.Combine(directory, motorModelTag + "_curve.png"));
        SaveVisual(Trend, System.IO.Path.Combine(directory, motorModelTag + "_realtime.png"));
    }
    private static void SaveVisual(FrameworkElement visual, string path)
    {
        if (visual.ActualWidth < 2 || visual.ActualHeight < 2) return;
        var bitmap = new RenderTargetBitmap((int)visual.ActualWidth, (int)visual.ActualHeight, 96, 96,
            System.Windows.Media.PixelFormats.Pbgra32);
        bitmap.Render(visual);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = System.IO.File.Create(path); encoder.Save(stream);
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
            Width = 1920; Height = 1080;
            var visual = (FrameworkElement)Content;
            visual.Measure(new Size(1920, 1080)); visual.Arrange(new Rect(0, 0, 1920, 1080)); visual.UpdateLayout();
            var bitmap = new System.Windows.Media.Imaging.RenderTargetBitmap(1920, 1080, 96, 96, System.Windows.Media.PixelFormats.Pbgra32);
            var drawing = new System.Windows.Media.DrawingVisual();
            using (var context = drawing.RenderOpen()) { context.DrawRectangle(new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(9,20,33)), null, new Rect(0,0,1920,1080)); context.DrawRectangle(new System.Windows.Media.VisualBrush(visual), null, new Rect(0,0,1920,1080)); }
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
    private async void CaptureServoEnableVescIfRequested(object sender, RoutedEventArgs e)
    {
        var args = Environment.GetCommandLineArgs();
        if (!args.Contains("--capture-servo-enable-vesc")) return;
        var portIndex = Array.FindIndex(args, value =>
            string.Equals(value, "--vesc-port", StringComparison.OrdinalIgnoreCase));
        var portName = portIndex >= 0 && portIndex + 1 < args.Length
            ? args[portIndex + 1]
            : "COM9";
        Hide();
        var directory = System.IO.Path.Combine(Environment.CurrentDirectory, "artifacts", "diagnostics");
        System.IO.Directory.CreateDirectory(directory);
        var resultPath = System.IO.Path.Combine(directory,
            $"combined_capture_result_{DateTime.UtcNow:yyyyMMdd_HHmmssfff}.txt");
        var latestResultPath = System.IO.Path.Combine(directory, "combined_capture_result.txt");
        try
        {
            var monitorIndex = Array.FindIndex(args, value =>
                string.Equals(value, "--monitor-seconds", StringComparison.OrdinalIgnoreCase));
            var monitorSeconds = 60;
            if (monitorIndex >= 0 && (monitorIndex + 1 >= args.Length ||
                !int.TryParse(args[monitorIndex + 1], out monitorSeconds) ||
                monitorSeconds is < 60 or > 300))
                throw new ArgumentOutOfRangeException(nameof(monitorSeconds), "CST diagnostic duration must be 60-300 seconds.");
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(monitorSeconds + 60));
            await _vm.CaptureServoEnableVescAsync(portName, TimeSpan.FromSeconds(monitorSeconds), timeout.Token);
            await WriteCaptureResultAsync(resultPath, latestResultPath,
                $"PASS: {portName}/VESC remained healthy for {monitorSeconds} seconds after EtherCAT CST zero-command enable.");
            _closing = true; _timer.Stop(); System.Windows.Application.Current.Shutdown(0);
        }
        catch (Exception ex)
        {
            await WriteCaptureResultAsync(resultPath, latestResultPath, "FAIL: " + ex);
            _closing = true; _timer.Stop(); System.Windows.Application.Current.Shutdown(1);
        }
    }
    private static async Task WriteCaptureResultAsync(string resultPath, string latestResultPath, string content)
    {
        await System.IO.File.WriteAllTextAsync(resultPath, content);
        try { await System.IO.File.WriteAllTextAsync(latestResultPath, content); }
        catch (System.IO.IOException) { }
    }
    private async void CaptureDutRunIfRequested(object sender, RoutedEventArgs e)
    {
        var args = Environment.GetCommandLineArgs();
        if (!args.Contains("--capture-dut-run")) return;
        var portIndex = Array.FindIndex(args, value => string.Equals(value, "--vesc-port", StringComparison.OrdinalIgnoreCase));
        var portName = portIndex >= 0 && portIndex + 1 < args.Length ? args[portIndex + 1] : "COM9";
        Hide();
        var directory = System.IO.Path.Combine(Environment.CurrentDirectory, "artifacts", "diagnostics");
        System.IO.Directory.CreateDirectory(directory);
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(45));
            await _vm.CaptureDutRunAsync(portName, 1000, timeout.Token);
            await System.IO.File.WriteAllTextAsync(System.IO.Path.Combine(directory, "dut_run_capture_result.txt"), "PASS: 1000 RPM diagnostic completed without a USB interruption.");
            _closing = true; _timer.Stop(); System.Windows.Application.Current.Shutdown(0);
        }
        catch (Exception ex)
        {
            await System.IO.File.WriteAllTextAsync(System.IO.Path.Combine(directory, "dut_run_capture_result.txt"), "FAIL: " + ex);
            _closing = true; _timer.Stop(); System.Windows.Application.Current.Shutdown(1);
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
