using System.Collections.Generic;

namespace Mithril.MapCalibration.Detection;

/// <summary>
/// Turns a captured map frame (already cropped to its <see cref="MapRect"/>) plus
/// the aligned base texture into typed icon detections in screenshot-pixel space,
/// grouped by landmark type — the input the <see cref="TypeAwareRansacSolver"/>
/// consumes. Two implementations ship: the deviation-blob detector (the proven
/// sparse-area front-end) and a whole-image template-NCC fallback.
/// </summary>
public interface ICalibrationDetector
{
    /// <summary>
    /// Captured map (already cropped to MapRect) + base texture (aligned) →
    /// typed detections in screenshot-pixel space, grouped by landmark type.
    /// </summary>
    IReadOnlyDictionary<string, IReadOnlyList<TypedDetection>> Detect(DetectionRequest request);
}

/// <summary>Everything a detector needs for one detect pass. BCL-only inputs (no decoder).</summary>
public sealed record DetectionRequest(
    GrayImage Screenshot,
    GrayImage BaseTexture,
    MapRect MapRect,
    IconTemplateSet Templates,
    RimMaskMode RimMask,
    double LowNcc,
    double TypeFloor,           // per-blob template-NCC acceptance gate (§8: ~0.65, not deviation-rim alone)
    BlobOptions BlobOptions);
