using Legolas.Domain;

namespace Legolas.Services;

public interface ICoordinateProjector
{
    double Scale { get; }
    double RotationRadians { get; }
    PixelPoint Origin { get; }

    /// <summary>
    /// Projects a metre offset (east, north) to a pixel position using the current
    /// scale, rotation, and origin. Screen-y is assumed to increase downward;
    /// positive north therefore projects to decreasing y.
    /// </summary>
    PixelPoint Project(MetreOffset offset);

    /// <summary>
    /// Sets the player/origin pixel position without touching scale or rotation.
    /// </summary>
    void SetOrigin(PixelPoint origin);

    /// <summary>
    /// Derives scale and rotation from a single click on a reported survey dot.
    /// Sets origin to the supplied player pixel.
    /// </summary>
    void CalibrateFromClick(PixelPoint playerPixel, PixelPoint click, MetreOffset offset);

    /// <summary>
    /// Refits scale and rotation from k&#8805;2 user corrections. Origin is held fixed
    /// at the last value supplied through <see cref="SetOrigin"/> or <see cref="CalibrateFromClick"/>.
    /// A closed-form Procrustes-style solution (scale: weighted least squares on
    /// magnitudes; rotation: circular mean of bearing differences) \u2014 no iteration,
    /// no dependency, numerically stable.
    /// </summary>
    void Refit(IReadOnlyList<(MetreOffset Offset, PixelPoint Pixel)> corrections);
}
