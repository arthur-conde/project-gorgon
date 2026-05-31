using Microsoft.Extensions.Logging;
using Mithril.Overlay;

namespace Mithril.MapCalibration.Capture;

/// <summary>
/// Shell-side <see cref="IMapBboxDrawController"/>. <see cref="BeginDraw"/> arms
/// the drag-to-rect bbox mode and surfaces the instruction on the overlay status
/// chip. The drag interaction itself (mouse capture on the overlay window →
/// <see cref="IMapCaptureRegionProvider.Set"/> on mouse-up, converting client to
/// desktop pixels) is WPF + manual-verify (Task 28, spec §7) and is attached to
/// the overlay window's input surface; this controller is the DI-resolvable entry
/// point the hotkey invokes.
/// </summary>
public sealed class MapBboxDrawController : IMapBboxDrawController
{
    private readonly IOverlayWindow _overlay;
    private readonly IMapCaptureRegionProvider _region;
    private readonly ILogger? _logger;

    public MapBboxDrawController(
        IOverlayWindow overlay,
        IMapCaptureRegionProvider region,
        ILogger? logger = null)
    {
        _overlay = overlay;
        _region = region;
        _logger = logger;
    }

    public void BeginDraw()
    {
        _logger?.LogInformation("Map-bbox draw mode armed; drag a rectangle over the in-game map.");
        // Surface the instruction; the actual drag handler (manual-verify, Task 28)
        // writes the resolved desktop-pixel rect to _region.Set on mouse-up.
        _overlay.SetStatusMessage("Drag a rectangle over the in-game map to set the capture region.");
        _ = _region; // wired for the drag handler that completes the rect (manual-verify).
    }
}
