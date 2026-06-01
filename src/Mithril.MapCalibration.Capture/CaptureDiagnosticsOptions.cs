namespace Mithril.MapCalibration.Capture;

/// <summary>
/// Debug knobs for the capture seam (#966 Task 3). Off by default — flip
/// <see cref="DumpCaptureFrames"/> on to have <see cref="CaptureService"/> persist
/// the color captured frame as a PNG under
/// <c>%LocalAppData%/Mithril/diagnostics/calibration/</c> right after validation
/// passes (before <c>ToGray</c>/return), so a slow or stalled refine still leaves an
/// on-disk artifact to inspect the actual pixels the solve engine was handed.
///
/// <para>Threaded through DI as a singleton (see
/// <c>CaptureServiceCollectionExtensions.AddMithrilMapCalibrationCapture</c>); a
/// future settings surface can bind the flag. Kept a plain mutable POCO so it can be
/// flipped at runtime without re-resolving the graph.</para>
/// </summary>
public sealed class CaptureDiagnosticsOptions
{
    /// <summary>
    /// When <see langword="true"/>, dump the color captured frame to PNG after a
    /// successful <c>Validate</c>. Default <see langword="false"/>.
    /// </summary>
    public bool DumpCaptureFrames { get; set; }

    /// <summary>
    /// When <see langword="true"/> (and <see cref="DumpCaptureFrames"/> is on), also
    /// dump the derived grayscale frame alongside the color one — catches a
    /// <see cref="CapturedFrame.ToGray"/> bug that a color-only dump would hide.
    /// Default <see langword="false"/>.
    /// </summary>
    public bool DumpGrayFrames { get; set; }
}
