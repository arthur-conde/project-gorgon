namespace Mithril.Tools.MapCalibrationStudy;

/// <summary>
/// (H1) Classifies a solved rotation against the discrete axis-aligned set the
/// renderer is hypothesised to use: {0°, 180°}. A π rotation is a real
/// orientation flip, not drift; an in-between angle (large deviation) would
/// falsify the hypothesis.
/// </summary>
public static class OrientationClass
{
    /// <summary>Default tolerance for membership, in degrees (per the §6 H1 gate).</summary>
    public const double DefaultToleranceDeg = 0.1;

    public readonly record struct Result(int NearestDeg, double DeviationDeg, bool InSet);

    public static Result Classify(double radians, double toleranceDeg = DefaultToleranceDeg)
    {
        var deg = radians * 180.0 / Math.PI;
        // Normalise to (-180, 180].
        deg %= 360.0;
        if (deg > 180.0) deg -= 360.0;
        if (deg <= -180.0) deg += 360.0;

        // Distance to 0 vs ±180 (same magnitude either sign).
        var dTo0 = Math.Abs(deg);
        var dTo180 = Math.Abs(Math.Abs(deg) - 180.0);
        var (nearest, dev) = dTo0 <= dTo180 ? (0, dTo0) : (180, dTo180);
        return new Result(nearest, dev, dev <= toleranceDeg);
    }
}
