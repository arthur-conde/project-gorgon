using Mithril.MapCalibration.Detection;

namespace Mithril.MapCalibration.Capture;

/// <summary>
/// Locates the map's true sub-rect inside a captured frame (spec §4 step 4) via
/// texture registration, so eyeball framing slop + per-zone letterboxing are
/// absorbed before the solve.
/// </summary>
public interface IMapRegionRefiner
{
    /// <summary>
    /// Find where <paramref name="baseTexture"/> sits inside
    /// <paramref name="capturedGray"/>, or <see langword="null"/> on a weak/no
    /// match (below <paramref name="minScore"/>).
    /// </summary>
    MapRect? Refine(GrayImage capturedGray, GrayImage baseTexture, double minScore);
}
