using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Extensions.Logging;

namespace Mithril.MapCalibration.Detection;

/// <summary>
/// Headless detect → solve → gate engine. Ties an <see cref="ICalibrationDetector"/>
/// to the <see cref="TypeAwareRansacSolver"/>, enumerating the discrete {0, π}
/// map orientation (run with the base texture aligned at 0° and at 180°, keep the
/// better gated solve — spec §3/§4 step 6), and applies the
/// <see cref="ICalibrationConfidenceGate"/>. <b>No capture, no I/O</b> — fully
/// unit-testable. BCL-only (logging-abstractions optional).
/// </summary>
public sealed class MapCalibrationSolveEngine
{
    private readonly ICalibrationDetector _detector;
    private readonly ICalibrationConfidenceGate _gate;
    private readonly ILogger? _logger;

    public MapCalibrationSolveEngine(
        ICalibrationDetector detector,
        ICalibrationConfidenceGate gate,
        ILogger? logger = null)
    {
        _detector = detector;
        _gate = gate;
        _logger = logger;
    }

    /// <summary>
    /// Solve a calibration from a detection request + the area's landmark/NPC
    /// references. Tries both orientations; returns the gate-accepted result, or
    /// (null calibration + reject reason) when neither clears the gate.
    /// </summary>
    public CalibrationSolveResult Solve(DetectionRequest request, IReadOnlyList<LandmarkReference> references)
    {
        CalibrationSolveResult? bestAccepted = null;
        CalibrationSolveResult? bestRejected = null;

        foreach (var rotate180 in new[] { false, true })
        {
            var texture = rotate180 ? ImageOps.Rotate180(request.BaseTexture) : request.BaseTexture;
            var req = request with { BaseTexture = texture };

            var detections = _detector.Detect(req);
            LogDetectSummary(rotate180, detections, references);
            var (cal, inliers) = TypeAwareRansacSolver.Solve(ToMutable(detections), references, request.MapRect);

            if (cal is null)
            {
                bestRejected ??= new CalibrationSolveResult(null, inliers.Count, "no geometrically-consistent fit");
                continue;
            }

            if (_gate.Accept(cal, inliers.Count, out var reason))
            {
                var accepted = new CalibrationSolveResult(cal, inliers.Count, null);
                // Prefer the lower-residual accepted orientation.
                if (bestAccepted is null || cal.ResidualPixels < bestAccepted.Calibration!.ResidualPixels)
                {
                    bestAccepted = accepted;
                }
            }
            else
            {
                // Track the closest rejection for a useful reason if nothing passes.
                if (bestRejected is null || cal.ResidualPixels < (bestRejected.Calibration?.ResidualPixels ?? double.PositiveInfinity))
                {
                    bestRejected = new CalibrationSolveResult(null, inliers.Count, reason);
                }
            }
        }

        if (bestAccepted is not null)
        {
            _logger?.LogInformation(
                "Auto-calibration accepted: residual {Residual:0.00} px, {Inliers} inliers.",
                bestAccepted.Calibration!.ResidualPixels, bestAccepted.InlierCount);
            return bestAccepted;
        }

        var rejected = bestRejected ?? new CalibrationSolveResult(null, 0, "no detections");
        _logger?.LogInformation("Auto-calibration rejected: {Reason}.", rejected.RejectReason);
        return rejected;
    }

    /// <summary>
    /// Per-orientation detect summary: typed detection total + per-type breakdown
    /// and the reference per-type breakdown, plus a targeted Warning when the two
    /// type-key sets are disjoint (the mithril#974 failure mode: a detection-side
    /// IconTemplate.LandmarkType vocabulary that doesn't overlap the reference-side
    /// LandmarkReference.Type vocabulary → 0 correspondences possible). Cheap: at
    /// most one Information line + (rarely) one Warning per orientation, ≤ 2 each
    /// per solve attempt.
    /// </summary>
    private void LogDetectSummary(
        bool rotate180,
        IReadOnlyDictionary<string, IReadOnlyList<TypedDetection>> detections,
        IReadOnlyList<LandmarkReference> references)
    {
        if (_logger is null) return;

        var detTotal = detections.Sum(kv => kv.Value.Count);
        var detBreakdown = string.Join(" ", detections
            .OrderBy(kv => kv.Key, StringComparer.Ordinal)
            .Select(kv => $"{kv.Key}={kv.Value.Count}"));
        var refBreakdown = string.Join(" ", references
            .GroupBy(r => r.Type, StringComparer.Ordinal)
            .OrderBy(g => g.Key, StringComparer.Ordinal)
            .Select(g => $"{g.Key}={g.Count()}"));

        _logger.LogInformation(
            "Detect (rotate180={Rotate180}): {DetTotal} typed detections [{DetBreakdown}]; references [{RefBreakdown}].",
            rotate180, detTotal, detBreakdown, refBreakdown);

        var detKeys = new HashSet<string>(detections.Keys, StringComparer.Ordinal);
        var refKeys = new HashSet<string>(references.Select(r => r.Type), StringComparer.Ordinal);
        if (detKeys.Count > 0 && refKeys.Count > 0 && !detKeys.Overlaps(refKeys))
        {
            _logger.LogWarning(
                "Detection type-keys [{DetKeys}] and reference type-keys [{RefKeys}] are disjoint — "
                + "0 correspondences possible; likely an icon-template ↔ reference type-vocabulary mismatch.",
                string.Join(",", detKeys.OrderBy(k => k, StringComparer.Ordinal)),
                string.Join(",", refKeys.OrderBy(k => k, StringComparer.Ordinal)));
        }
    }

    private static IReadOnlyDictionary<string, List<TypedDetection>> ToMutable(
        IReadOnlyDictionary<string, IReadOnlyList<TypedDetection>> byType)
    {
        var result = new Dictionary<string, List<TypedDetection>>(byType.Count, StringComparer.Ordinal);
        foreach (var kv in byType) result[kv.Key] = new List<TypedDetection>(kv.Value);
        return result;
    }
}

/// <summary>Outcome of a headless solve: the gated calibration (or null), the inlier count, and a reject reason when null.</summary>
public sealed record CalibrationSolveResult(
    AreaCalibration? Calibration,
    int InlierCount,
    string? RejectReason);
