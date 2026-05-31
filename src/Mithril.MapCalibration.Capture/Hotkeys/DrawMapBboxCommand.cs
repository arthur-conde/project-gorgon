using System.Threading;
using System.Threading.Tasks;
using Mithril.Shared.Hotkeys;

namespace Mithril.MapCalibration.Capture.Hotkeys;

/// <summary>
/// "Draw the map capture bbox" hotkey. Arms the drag-to-rect mode on the overlay
/// (<see cref="IMapBboxDrawController.BeginDraw"/>); the drag completion persists
/// the rect via <see cref="IMapCaptureRegionProvider.Set"/>.
/// <see cref="RespectsFocusGate"/> is <see langword="false"/>: framing the bbox
/// happens with the overlay (Mithril) focused, not the game.
/// </summary>
public sealed class DrawMapBboxCommand : IHotkeyCommand
{
    private readonly IMapBboxDrawController _drawController;

    public DrawMapBboxCommand(IMapBboxDrawController drawController) => _drawController = drawController;

    public string Id => "mapcalibration.draw_bbox";
    public string DisplayName => "Draw Map Capture Region";
    public string? Category => "Map Calibration";
    public HotkeyBinding? DefaultBinding => null;
    public bool RespectsFocusGate => false;

    public Task ExecuteAsync(CancellationToken cancellationToken)
    {
        _drawController.BeginDraw();
        return Task.CompletedTask;
    }
}
