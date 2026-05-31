namespace Mithril.MapCalibration.Capture;

/// <summary>
/// Enters the "drag a rectangle over the map" mode on the overlay. <see cref="BeginDraw"/>
/// arms a Snipping-Tool-style snip selector; on confirm it moves/resizes the
/// overlay window to the snipped rect (#940 one-rect model — the overlay bounds
/// ARE the capture region, persisted by the overlay's existing
/// <c>WindowLayoutBinder</c>). The drag interaction is WPF + manual-verify; this
/// seam keeps the hotkey command unit-testable (it just begins the mode).
/// </summary>
public interface IMapBboxDrawController
{
    /// <summary>Arm the drag-to-rect bbox draw mode on the overlay.</summary>
    void BeginDraw();
}
