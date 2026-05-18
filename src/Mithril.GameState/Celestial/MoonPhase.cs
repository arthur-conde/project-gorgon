using System.Text;

namespace Mithril.GameState.Celestial;

/// <summary>
/// Project Gorgon's lunar cycle — the standard eight phases. The game's
/// <c>ProcessSetCelestialInfo</c> Player.log line carries one of these as a
/// token (e.g. <c>WaxingCrescentMoon</c>); the cycle gates a handful of
/// recipes / quests (<c>MoonPhase</c> / <c>FullMoon</c> requirements) and the
/// mushroom-circle recall cooldowns.
///
/// <para><b>Vocabulary caveat.</b> Three spellings of the same phase are in
/// play: the Player.log token has a trailing <c>Moon</c>
/// (<c>WaxingCrescentMoon</c>); reference-data discriminator values do not
/// (<c>WaxingCrescent</c> / <c>FullMoon</c> / <c>NewMoon</c>);
/// <c>strings_all</c> carries prose only. The six non-quarter phases are
/// confirmed from a live capture + the strings table; the two quarter tokens
/// (<see cref="FirstQuarter"/> / <see cref="ThirdQuarter"/>) are the standard
/// astronomical names and are <b>not yet confirmed against a live Player.log</b>
/// — if PG emits a different token there it maps to <see cref="Unknown"/> and
/// the raw string still surfaces verbatim (no data loss). See the
/// <c>ProcessSetCelestialInfo</c> investigation.</para>
/// </summary>
public enum MoonPhase
{
    /// <summary>Token not recognised — the raw log string is still retained
    /// on <see cref="CelestialInfo.RawPhase"/> and surfaced verbatim.</summary>
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
    // Keyed by a normalised form: letters only, lower-cased, with a trailing
    // "moon" stripped. This collapses all three observed spellings onto one
    // key — log `WaxingCrescentMoon` → "waxingcrescent", reference
    // `WaxingCrescent` → "waxingcrescent", `FullMoon`/`NewMoon` → "full"/"new".
    // `LastQuarter` is accepted as a synonym for the third quarter.
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
    /// <see cref="MoonPhase.Unknown"/> — the caller keeps the raw string.
    /// </summary>
    public static MoonPhase ParsePhase(string? rawToken)
    {
        if (string.IsNullOrWhiteSpace(rawToken)) return MoonPhase.Unknown;
        return ByToken.TryGetValue(Normalise(rawToken), out var p) ? p : MoonPhase.Unknown;
    }

    private static string Normalise(string token)
    {
        var sb = new StringBuilder(token.Length);
        foreach (var c in token)
            if (char.IsLetter(c)) sb.Append(char.ToLowerInvariant(c));
        var s = sb.ToString();
        // "new"/"full" rather than "newmoon"/"fullmoon"; "waxingcrescent"
        // rather than "waxingcrescentmoon". "moon" is never a phase by itself.
        if (s.Length > 4 && s.EndsWith("moon", StringComparison.Ordinal))
            s = s[..^4];
        return s;
    }

    /// <summary>
    /// Human phrase for display: a fixed name for a recognised phase,
    /// otherwise a CamelCase-split of the raw token so an unconfirmed /
    /// future token still reads as a phrase ("WaxingCrescentMoon" →
    /// "Waxing Crescent Moon") rather than a blank.
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
