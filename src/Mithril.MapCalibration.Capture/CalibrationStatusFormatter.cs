using System;

namespace Mithril.MapCalibration.Capture;

/// <summary>
/// Maps an <see cref="AutoCalibrationOutcome"/> / raw reject reason to the
/// user-facing status-chip string (spec §11). Pure + CI-tested; the push to the
/// overlay chip (<c>IOverlayWindow.SetStatusMessage</c>) is shell wiring (Task 28).
/// The engine's reject reasons are diagnostic ("residual 25.00 px exceeds
/// threshold…"); this turns them into an actionable instruction.
/// </summary>
public static class CalibrationStatusFormatter
{
    /// <summary>
    /// The status string for an outcome, or <see langword="null"/> when it
    /// succeeded (a persisted calibration clears the chip — happy state).
    /// </summary>
    public static string? ForOutcome(AutoCalibrationOutcome outcome)
        => outcome.Persisted ? null : ForReject(outcome.RejectReason ?? "couldn't auto-calibrate the map");

    /// <summary>Map a raw reject reason to an actionable user instruction.</summary>
    public static string ForReject(string reason)
    {
        if (string.IsNullOrWhiteSpace(reason)) return "Couldn't auto-calibrate the map.";

        // No bbox framed yet → tell them to draw it.
        if (Contains(reason, "bbox"))
            return "No map region set — use the draw-map-bbox hotkey to frame the map.";

        // PG not focused / not in-world → name the game.
        if (Contains(reason, "foreground") || Contains(reason, "in-world") || Contains(reason, "not detected"))
            return "Open Project Gorgon (focused, in an area) to calibrate the map.";

        // Assets still extracting.
        if (Contains(reason, "map assets") || Contains(reason, "preparing") || Contains(reason, "base texture"))
            return "Preparing map assets… try the capture again in a moment.";

        // Low-confidence solve (residual / inliers) → the actionable fix is to
        // zoom the in-game map all the way out and redraw the bbox.
        if (Contains(reason, "residual") || Contains(reason, "inlier")
            || Contains(reason, "fit") || Contains(reason, "locate the map") || Contains(reason, "capture"))
            return "Couldn't auto-calibrate — zoom the in-game map all the way out, then redraw the map bbox and retry.";

        return "Couldn't auto-calibrate the map.";
    }

    private static bool Contains(string haystack, string needle)
        => haystack.Contains(needle, StringComparison.OrdinalIgnoreCase);
}
