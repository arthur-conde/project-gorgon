using Mithril.MapCalibration.Detection;

namespace Mithril.MapCalibration.Capture;

/// <summary>
/// <see cref="IMapRegionRefiner"/> over the Phase-1
/// <see cref="MapRectLocator.AutoDetect(GrayImage, GrayImage, double, int)"/>
/// (multi-scale NCC of the base texture against the captured frame). The seam
/// exists so the orchestrator depends on an interface rather than a static, and so
/// a Windows.Graphics.Capture-era refiner can swap in.
///
/// <para><b>#966 unblock.</b> The live path hands native-resolution inputs
/// (~1257×1049 capture vs 2048×2033 texture); the native ladder is ~1–2e12
/// multiply-adds and takes minutes. This seam runs the search at the
/// <see cref="MapRectLocator.DefaultWorkingLongEdgePx"/> working resolution
/// (~1000× fewer ops ⇒ sub-second) and unscales the resulting
/// <see cref="MapRect"/> back to full-capture coordinates. Inputs already at/under
/// the working size are not downsampled, so the synthetic-test contract is
/// preserved.</para>
/// </summary>
public sealed class TextureRegistrationRefiner : IMapRegionRefiner
{
    public MapRect? Refine(GrayImage capturedGray, GrayImage baseTexture, double minScore) =>
        MapRectLocator.AutoDetect(
            capturedGray, baseTexture, minScore, MapRectLocator.DefaultWorkingLongEdgePx);
}
