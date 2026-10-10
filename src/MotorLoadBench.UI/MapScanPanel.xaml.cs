using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace MotorLoadBench.UI;

public partial class MapScanPanel : UserControl
{
    private MapScanViewModel? _vm;

    public MapScanPanel()
    {
        InitializeComponent();
        DataContextChanged += (_, e) => Attach(e.NewValue as MapScanViewModel);
        GridView.CellClicked += cell => _vm?.CellClicked(cell.Id);
    }

    private void Attach(MapScanViewModel? vm)
    {
        if (_vm != null) { _vm.GridChanged -= OnGridChanged; _vm.ExportVisualRequested -= OnExportVisual; }
        _vm = vm;
        if (_vm == null) return;
        _vm.GridChanged += OnGridChanged; _vm.ExportVisualRequested += OnExportVisual;
        OnGridChanged();
    }

    private void OnGridChanged() => GridView.SetCells(_vm?.Grid?.Snapshot() ?? []);

    // Rendered off-screen at a fixed size, so the export also works while another tab is shown.
    private void OnExportVisual(string path)
    {
        const int width = 1600, height = 760;
        var view = new MapGridControl { Title = _vm?.ExportTitle };
        view.SetCells(_vm?.Grid?.Snapshot() ?? []);
        view.Measure(new Size(width, height)); view.Arrange(new Rect(0, 0, width, height)); view.UpdateLayout();
        var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(view);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = File.Create(path); encoder.Save(stream);
    }
}
