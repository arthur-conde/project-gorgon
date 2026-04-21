namespace Legolas.Domain;

public readonly record struct PixelPoint(double X, double Y)
{
    public static PixelPoint Zero => new(0, 0);

    public double DistanceTo(PixelPoint other)
    {
        var dx = X - other.X;
        var dy = Y - other.Y;
        return Math.Sqrt(dx * dx + dy * dy);
    }

    public double DistanceSquaredTo(PixelPoint other)
    {
        var dx = X - other.X;
        var dy = Y - other.Y;
        return dx * dx + dy * dy;
    }
}
