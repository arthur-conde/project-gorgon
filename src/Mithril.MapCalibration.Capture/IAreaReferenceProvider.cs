using System.Collections.Generic;
using Mithril.MapCalibration.Detection;

namespace Mithril.MapCalibration.Capture;

/// <summary>
/// Supplies the landmark/NPC world-anchor reference points the solver pairs
/// detections against, for one area. Decoupled behind an interface so the
/// orchestrator depends on a seam (testable with a fake) rather than the
/// reference-data service directly.
/// </summary>
public interface IAreaReferenceProvider
{
    /// <summary>
    /// The <see cref="LandmarkReference"/> set for <paramref name="areaKey"/>
    /// (e.g. <c>"AreaSerbule"</c>). Empty when the area is unknown or carries no
    /// mappable references — which fail-soft yields no inliers → the gate rejects.
    /// </summary>
    IReadOnlyList<LandmarkReference> ForArea(string areaKey);
}
