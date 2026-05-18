using System.Globalization;

namespace Legolas.Domain;

/// <summary>
/// A point in a Project&#160;Gorgon area's local engine-unit coordinate space, as
/// stored in reference data (<c>landmarks.json</c> <c>Loc</c>, <c>npcs.json</c>
/// <c>Pos</c>). Format is <c>"x:&lt;f&gt; y:&lt;f&gt; z:&lt;f&gt;"</c>; the ground
/// plane is <see cref="X"/>/<see cref="Z"/> and <see cref="Y"/> is elevation
/// (ignored for map projection). Verified 2026-05-18 to be the same frame the game
/// positions the player in (Player.log <c>ProcessNewPosition</c>), and coordinates
/// are <b>signed</b> — negative X/Z are common (Myconian Cave, Sun Vale, Desert,
/// Gazluk). Any parse that assumes positive-only silently misprojects whole zones.
/// </summary>
public readonly record struct WorldCoord(double X, double Y, double Z)
{
    /// <summary>
    /// Parses the reference-data position string. Accepts the canonical
    /// <c>"x:1.5 y:2 z:-3.25"</c> form (tokens may appear in any order and the
    /// sign is preserved) and a bare space/comma-separated <c>"1.5 2 -3.25"</c>
    /// fallback. Returns null when fewer than three numeric components resolve.
    /// </summary>
    public static WorldCoord? TryParse(string? loc)
    {
        if (string.IsNullOrWhiteSpace(loc)) return null;

        double? x = null, y = null, z = null;
        var sawLabel = false;

        foreach (var token in loc.Split(new[] { ' ', '\t', ',' }, StringSplitOptions.RemoveEmptyEntries))
        {
            var colon = token.IndexOf(':');
            if (colon > 0)
            {
                sawLabel = true;
                var axis = token.AsSpan(0, colon).Trim();
                var valSpan = token.AsSpan(colon + 1);
                if (!double.TryParse(valSpan, NumberStyles.Float, CultureInfo.InvariantCulture, out var v))
                    continue;
                if (axis.Equals("x", StringComparison.OrdinalIgnoreCase)) x = v;
                else if (axis.Equals("y", StringComparison.OrdinalIgnoreCase)) y = v;
                else if (axis.Equals("z", StringComparison.OrdinalIgnoreCase)) z = v;
            }
            else if (!sawLabel)
            {
                // Bare "x y z" fallback — fill positionally.
                if (!double.TryParse(token, NumberStyles.Float, CultureInfo.InvariantCulture, out var v))
                    return null;
                if (x is null) x = v;
                else if (y is null) y = v;
                else if (z is null) z = v;
            }
        }

        if (x is null || y is null || z is null) return null;
        return new WorldCoord(x.Value, y.Value, z.Value);
    }
}
