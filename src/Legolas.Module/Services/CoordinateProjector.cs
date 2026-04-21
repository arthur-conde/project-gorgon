using Legolas.Domain;

namespace Legolas.Services;

public sealed class CoordinateProjector : ICoordinateProjector
{
    private double _scale = 1.0;
    private double _rotation;
    private PixelPoint _origin = PixelPoint.Zero;

    public double Scale => _scale;
    public double RotationRadians => _rotation;
    public PixelPoint Origin => _origin;

    public PixelPoint Project(MetreOffset offset)
    {
        var cos = Math.Cos(_rotation);
        var sin = Math.Sin(_rotation);
        var rotE = offset.East * cos + offset.North * sin;
        var rotN = -offset.East * sin + offset.North * cos;
        return new PixelPoint(
            _origin.X + _scale * rotE,
            _origin.Y - _scale * rotN);
    }

    public void SetOrigin(PixelPoint origin) => _origin = origin;

    public void CalibrateFromClick(PixelPoint playerPixel, PixelPoint click, MetreOffset offset)
    {
        _origin = playerPixel;

        var rOffset = offset.Magnitude;
        if (rOffset < 1e-9)
        {
            return;
        }

        var dx = click.X - playerPixel.X;
        var dy = click.Y - playerPixel.Y;
        var rPixel = Math.Sqrt(dx * dx + dy * dy);
        if (rPixel < 1e-9)
        {
            return;
        }

        _scale = rPixel / rOffset;

        var phiOffset = Math.Atan2(offset.East, offset.North);
        var phiPixel = Math.Atan2(dx, -dy);
        _rotation = NormaliseAngle(phiPixel - phiOffset);
    }

    public void Refit(IReadOnlyList<(MetreOffset Offset, PixelPoint Pixel)> corrections)
    {
        if (corrections.Count < 2)
        {
            return;
        }

        double scaleNumerator = 0;
        double scaleDenominator = 0;
        double bearingSin = 0;
        double bearingCos = 0;
        var contributingCount = 0;

        foreach (var (offset, pixel) in corrections)
        {
            var rOffset = offset.Magnitude;
            if (rOffset < 1e-9)
            {
                continue;
            }

            var dx = pixel.X - _origin.X;
            var dy = pixel.Y - _origin.Y;
            var rPixel = Math.Sqrt(dx * dx + dy * dy);
            if (rPixel < 1e-9)
            {
                continue;
            }

            // Weighted least-squares scale: s = \u03a3(r_p * r_o) / \u03a3(r_o^2).
            scaleNumerator += rPixel * rOffset;
            scaleDenominator += rOffset * rOffset;

            // Circular mean of (\u03c6_p - \u03c6_o) gives rotation without angle-wrap bugs.
            var phiOffset = Math.Atan2(offset.East, offset.North);
            var phiPixel = Math.Atan2(dx, -dy);
            var dPhi = phiPixel - phiOffset;
            bearingSin += Math.Sin(dPhi);
            bearingCos += Math.Cos(dPhi);

            contributingCount++;
        }

        if (contributingCount < 2)
        {
            return;
        }

        if (scaleDenominator > 1e-9)
        {
            _scale = scaleNumerator / scaleDenominator;
        }

        _rotation = NormaliseAngle(Math.Atan2(bearingSin, bearingCos));
    }

    private static double NormaliseAngle(double radians)
    {
        var twoPi = 2 * Math.PI;
        var r = radians % twoPi;
        if (r > Math.PI) r -= twoPi;
        if (r < -Math.PI) r += twoPi;
        return r;
    }
}
