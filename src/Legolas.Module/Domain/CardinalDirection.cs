namespace Legolas.Domain;

public enum CardinalDirection
{
    N,
    NE,
    E,
    SE,
    S,
    SW,
    W,
    NW
}

public static class CardinalDirectionExtensions
{
    public static double ToBearingRadians(this CardinalDirection dir) => dir switch
    {
        CardinalDirection.N => 0,
        CardinalDirection.NE => Math.PI / 4,
        CardinalDirection.E => Math.PI / 2,
        CardinalDirection.SE => 3 * Math.PI / 4,
        CardinalDirection.S => Math.PI,
        CardinalDirection.SW => 5 * Math.PI / 4,
        CardinalDirection.W => 3 * Math.PI / 2,
        CardinalDirection.NW => 7 * Math.PI / 4,
        _ => throw new ArgumentOutOfRangeException(nameof(dir), dir, null)
    };

    public static MetreOffset ToOffset(this CardinalDirection dir, double distanceMetres)
    {
        var rad = dir.ToBearingRadians();
        return new MetreOffset(
            East: Math.Sin(rad) * distanceMetres,
            North: Math.Cos(rad) * distanceMetres);
    }

    public static bool TryParse(ReadOnlySpan<char> s, out CardinalDirection direction)
    {
        if (s.Equals("N", StringComparison.OrdinalIgnoreCase)) { direction = CardinalDirection.N; return true; }
        if (s.Equals("NE", StringComparison.OrdinalIgnoreCase)) { direction = CardinalDirection.NE; return true; }
        if (s.Equals("E", StringComparison.OrdinalIgnoreCase)) { direction = CardinalDirection.E; return true; }
        if (s.Equals("SE", StringComparison.OrdinalIgnoreCase)) { direction = CardinalDirection.SE; return true; }
        if (s.Equals("S", StringComparison.OrdinalIgnoreCase)) { direction = CardinalDirection.S; return true; }
        if (s.Equals("SW", StringComparison.OrdinalIgnoreCase)) { direction = CardinalDirection.SW; return true; }
        if (s.Equals("W", StringComparison.OrdinalIgnoreCase)) { direction = CardinalDirection.W; return true; }
        if (s.Equals("NW", StringComparison.OrdinalIgnoreCase)) { direction = CardinalDirection.NW; return true; }
        direction = default;
        return false;
    }
}
