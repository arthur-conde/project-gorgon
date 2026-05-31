using Mithril.MapCalibration.Detection;

namespace Mithril.MapCalibration.Capture;

/// <summary>
/// <see cref="IMapRegionRefiner"/> over the Phase-1
/// <see cref="MapRectLocator.AutoDetect"/> (multi-scale NCC of the base texture
/// against the captured frame). The seam exists so the orchestrator depends on
/// an interface rather than a static, and so a Windows.Graphics.Capture-era
/// refiner can swap in.
/// </summary>
public sealed class TextureRegistrationRefiner : IMapRegionRefiner
{
    public MapRect? Refine(GrayImage capturedGray, GrayImage baseTexture, double minScore) =>
        MapRectLocator.AutoDetect(capturedGray, baseTexture, minScore);
}
