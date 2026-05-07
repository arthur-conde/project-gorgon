namespace Legolas.Domain;

/// <summary>
/// Shared ARGB-hex normalisation for the colour-bearing settings classes
/// (<see cref="LegolasColors"/>, <see cref="LegolasPinShapeStyle"/>,
/// <see cref="LegolasActivePinStyle"/>). All callers funnel through
/// <see cref="Normalize"/> so user-typed hex strings end up canonical
/// regardless of which class persists them.
/// </summary>
public static class HexColor
{
    /// <summary>
    /// Coerces input to a canonical 8-digit ARGB hex string. Accepts either
    /// 6-digit (RGB; alpha defaults to FF) or 8-digit (ARGB) forms with or
    /// without a leading '#'. Invalid strings fall back to opaque magenta so
    /// the bug is visible rather than silently transparent.
    /// </summary>
    public static string Normalize(string? input)
    {
        if (string.IsNullOrWhiteSpace(input)) return "#FFFF00FF";
        var s = input.Trim();
        if (s.StartsWith("#")) s = s.Substring(1);
        return s.Length switch
        {
            6 when IsHex(s) => "#FF" + s.ToUpperInvariant(),
            8 when IsHex(s) => "#" + s.ToUpperInvariant(),
            _ => "#FFFF00FF",
        };
    }

    private static bool IsHex(string s)
    {
        foreach (var c in s)
        {
            if (!Uri.IsHexDigit(c)) return false;
        }
        return true;
    }
}
