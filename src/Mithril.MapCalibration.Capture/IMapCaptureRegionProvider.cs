namespace Mithril.MapCalibration.Capture;

/// <summary>
/// Reads the single map-capture bbox (spec §7) as the <b>live overlay-window
/// bounds</b> (#940 one-rect model). The overlay window's desktop rect IS the
/// capture region AND the calibration frame; there is no separately-persisted
/// rect — persistence rides the overlay's existing <c>WindowLayoutBinder</c>
/// wiring. The auto-attempt reads <see cref="Current"/> to know whether a region
/// has been framed.
/// </summary>
public interface IMapCaptureRegionProvider
{
    /// <summary><see langword="null"/> → no bbox set yet (gates the auto-attempt, spec §10/§11).</summary>
    CaptureRect? Current { get; }
}
