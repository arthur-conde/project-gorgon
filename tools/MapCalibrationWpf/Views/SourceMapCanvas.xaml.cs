namespace Mithril.Tools.MapCalibrationWpf.Views;

using System.Windows.Controls;
using System.Windows.Input;
using Mithril.Tools.MapCalibrationWpf.ViewModels;

/// <summary>
/// Source-map canvas. Renders the per-area PNG and overlays committed-ref +
/// projection-dot ItemsControls. Click handler converts the click position to
/// a texture pixel (1:1 mapping per the Stretch="None" decision) and forwards
/// to <see cref="AreaWorkspaceViewModel.PlaceRefAt"/>.
/// </summary>
public partial class SourceMapCanvas : UserControl
{
    public SourceMapCanvas()
    {
        InitializeComponent();
    }

    private void OnCanvasLeftClick(object sender, MouseButtonEventArgs e)
    {
        if (DataContext is not AreaWorkspaceViewModel vm) return;
        var p = e.GetPosition(OverlayCanvas);
        vm.PlaceRefAt(p.X, p.Y);
    }
}
