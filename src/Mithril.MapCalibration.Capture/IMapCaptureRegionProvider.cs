namespace Mithril.MapCalibration.Capture;

/// <summary>
/// Reads the single map-capture bbox (spec §7) from the SHELL-persisted capture
/// rect (#947), converted to physical desktop pixels. The capture region is a
/// persisted desktop rectangle sourced independently of any window — see
/// <see cref="IMapCaptureRectStore"/> for why it no longer rides the live
/// overlay-window geometry. The auto-attempt reads <see cref="Current"/> to know
/// whether a region has been framed.
/// </summary>
public interface IMapCaptureRegionProvider
{
    /// <summary><see langword="null"/> → no bbox set yet (gates the auto-attempt, spec §10/§11).</summary>
    CaptureRect? Current { get; }
}
