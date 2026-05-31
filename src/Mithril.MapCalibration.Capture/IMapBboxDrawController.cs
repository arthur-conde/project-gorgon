namespace Mithril.MapCalibration.Capture;

/// <summary>
/// Enters the "drag a rectangle over the map" mode on the overlay; the drag
/// completion writes the desktop-pixel rect to <see cref="IMapCaptureRegionProvider.Set"/>.
/// The drag interaction is WPF + manual-verify (Task 28); this seam keeps the
/// hotkey command unit-testable (it just begins the mode).
/// </summary>
public interface IMapBboxDrawController
{
    /// <summary>Arm the drag-to-rect bbox draw mode on the overlay.</summary>
    void BeginDraw();
}
