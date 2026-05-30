namespace Mithril.Tools.MapCalibration.Harness;

/// <summary>
/// WPF-free image abstraction for the loaded in-game-map screenshot. Refines
/// the design spec's <c>BitmapSource</c> so the harness core stays headless and
/// trivially testable: the WPF shell (issue B) adapts its loaded
/// <c>BitmapSource</c> to this; the test project supplies a synthetic one.
/// Pixel access exists for automated methods (e.g. green-pixel detection) that
/// scan the image.
/// </summary>
public interface ICalibrationImage
{
    int Width { get; }

    int Height { get; }

    /// <summary>
    /// Returns the RGBA components of the pixel at (<paramref name="x"/>,
    /// <paramref name="y"/>). Bounds are the caller's responsibility.
    /// </summary>
    (byte R, byte G, byte B, byte A) GetPixel(int x, int y);
}
