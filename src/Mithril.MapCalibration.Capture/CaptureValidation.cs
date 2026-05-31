namespace Mithril.MapCalibration.Capture;

/// <summary>
/// The spec §6 "verify your inputs" guard, isolated from the P/Invoke capture so
/// it is CI-testable. A captured frame is valid only when its dimensions match
/// the requested rect AND it is not a (near-)black frame — the latter catches a
/// capture that grabbed our own blanked overlay, an occluded window, or a
/// device-lost surface. A frame that slips past here is still caught downstream
/// by the Phase-1 confidence gate.
/// </summary>
public sealed class CaptureValidation
{
    /// <summary>
    /// Mean-luma floor below which a frame is treated as black. Reuses the
    /// gate-study probe's <c>shotMean &lt; 8</c> black-frame guard verbatim.
    /// </summary>
    public const double BlackFrameLumaFloor = 8.0;

    /// <summary>
    /// Returns <see langword="true"/> when <paramref name="frame"/> matches the
    /// expected size and is not black; otherwise <see langword="false"/> with a
    /// human-readable <paramref name="reason"/>.
    /// </summary>
    public bool Validate(CapturedFrame frame, CaptureRect expected, out string? reason)
    {
        if (frame.Width != expected.Width || frame.Height != expected.Height)
        {
            reason = $"size mismatch: captured {frame.Width}x{frame.Height}, expected {expected.Width}x{expected.Height}";
            return false;
        }

        double mean = frame.MeanLuma();
        if (mean < BlackFrameLumaFloor)
        {
            reason = $"black frame: mean luma {mean:F2} < {BlackFrameLumaFloor}";
            return false;
        }

        reason = null;
        return true;
    }
}
