using System.Text;

namespace Arda.World.Player;

/// <summary>
/// Canonical eight-phase lunar cycle. The game's <c>ProcessSetCelestialInfo</c>
/// carries a raw token (e.g. <c>WaxingCrescentMoon</c>); this enum provides a
/// stable discriminator for consumers. Unrecognised tokens map to
/// <see cref="Unknown"/> — the raw string is always retained on
/// <see cref="ICelestialState.CurrentPhaseRaw"/>.
/// </summary>
public enum MoonPhase
{
    Unknown = 0,
    NewMoon,
    WaxingCrescent,
    FirstQuarter,
    WaxingGibbous,
    FullMoon,
    WaningGibbous,
    ThirdQuarter,
    WaningCrescent,
}

public static class MoonPhaseExtensions
{
    private static readonly Dictionary<string, MoonPhase> ByToken = new(StringComparer.Ordinal)
    {
        ["new"] = MoonPhase.NewMoon,
        ["waxingcrescent"] = MoonPhase.WaxingCrescent,
        ["firstquarter"] = MoonPhase.FirstQuarter,
        ["waxinggibbous"] = MoonPhase.WaxingGibbous,
        ["full"] = MoonPhase.FullMoon,
        ["waninggibbous"] = MoonPhase.WaningGibbous,
        ["thirdquarter"] = MoonPhase.ThirdQuarter,
        ["lastquarter"] = MoonPhase.ThirdQuarter,
        ["waningcrescent"] = MoonPhase.WaningCrescent,
    };

    /// <summary>
    /// Map a raw celestial token (any of the three observed spellings) to a
    /// canonical <see cref="MoonPhase"/>. Unrecognised tokens yield
    /// <see cref="MoonPhase.Unknown"/>.
    /// </summary>
    public static MoonPhase ParsePhase(string? rawToken)
    {
        if (string.IsNullOrWhiteSpace(rawToken)) return MoonPhase.Unknown;
        return ByToken.TryGetValue(Normalise(rawToken), out var p) ? p : MoonPhase.Unknown;
    }

    /// <summary>
    /// Human phrase for display. Recognised phases get a fixed name;
    /// unknown tokens get a CamelCase-split fallback.
    /// </summary>
    public static string DisplayName(this MoonPhase phase, string rawFallback) => phase switch
    {
        MoonPhase.NewMoon => "New Moon",
        MoonPhase.WaxingCrescent => "Waxing Crescent",
        MoonPhase.FirstQuarter => "First Quarter",
        MoonPhase.WaxingGibbous => "Waxing Gibbous",
        MoonPhase.FullMoon => "Full Moon",
        MoonPhase.WaningGibbous => "Waning Gibbous",
        MoonPhase.ThirdQuarter => "Third Quarter",
        MoonPhase.WaningCrescent => "Waning Crescent",
        _ => SplitCamelCase(rawFallback),
    };

    private static string Normalise(string token)
    {
        var sb = new StringBuilder(token.Length);
        foreach (var c in token)
            if (char.IsLetter(c)) sb.Append(char.ToLowerInvariant(c));
        var s = sb.ToString();
        if (s.Length > 4 && s.EndsWith("moon", StringComparison.Ordinal))
            s = s[..^4];
        return s;
    }

    private static string SplitCamelCase(string raw)
    {
        if (string.IsNullOrEmpty(raw)) return "(unknown)";
        var sb = new StringBuilder(raw.Length + 8);
        for (var i = 0; i < raw.Length; i++)
        {
            var c = raw[i];
            if (i > 0 && char.IsUpper(c) && !char.IsUpper(raw[i - 1]) && sb.Length > 0 && sb[^1] != ' ')
                sb.Append(' ');
            sb.Append(c);
        }
        return sb.ToString();
    }
}
