using System.Globalization;
using System.Text.RegularExpressions;

namespace Mithril.GameState.Sessions;

/// <summary>
/// Parses PG's per-session login banner:
/// <code>
/// [12:25:04] Logged in as character Emraell. Time UTC=05/11/2026 12:25:04. Timezone Offset 01:00:00
/// </code>
/// The bracketed prefix is tolerated but not required (any leading whitespace
/// or other text before <c>Logged in as character</c> is ignored).
///
/// Extracts the character name, the absolute UTC datetime (the only authoritative
/// in-file source of a calendar date for any subsequent <c>[HH:MM:SS]</c>-only
/// line), and the timezone offset (which closes the Player.log-UTC vs
/// ChatLogs-local asymmetry documented in <c>pg_log_timezones</c>).
/// </summary>
internal static partial class LoginBannerParser
{
    // PG emits dates as M/D/YYYY (US client formatting). Live captures
    // observed both `05/11/2026 12:25:04` (zero-padded throughout) and
    // single-digit variants on month / day / hour / minute / second when
    // the value is < 10. TryParseExact accepts each variant separately, so
    // we list every combination of `M` vs `MM`, `d` vs `dd`, `H` vs `HH`,
    // `m` vs `mm`, `s` vs `ss` we expect to see.
    private static readonly string[] UtcDateFormats =
    [
        "M/d/yyyy H:m:s",
        "M/d/yyyy H:m:ss",
        "M/d/yyyy H:mm:s",
        "M/d/yyyy H:mm:ss",
        "M/d/yyyy HH:m:s",
        "M/d/yyyy HH:m:ss",
        "M/d/yyyy HH:mm:s",
        "M/d/yyyy HH:mm:ss",
        "MM/dd/yyyy H:m:s",
        "MM/dd/yyyy H:m:ss",
        "MM/dd/yyyy H:mm:s",
        "MM/dd/yyyy H:mm:ss",
        "MM/dd/yyyy HH:m:s",
        "MM/dd/yyyy HH:m:ss",
        "MM/dd/yyyy HH:mm:s",
        "MM/dd/yyyy HH:mm:ss",
    ];

    // Name allows everything up to the period that delimits the next field.
    // UTC capture is the literal <date> <time> after "Time UTC=" up to the next period.
    // Offset is signed HH:MM:SS — PG uses unsigned in the East-of-UTC case (no '+').
    [GeneratedRegex(
        @"Logged in as character (?<name>[^.]+)\.\s*Time UTC=(?<utc>[^.]+)\.\s*Timezone Offset (?<off>-?\d{1,2}:\d{2}:\d{2})",
        RegexOptions.CultureInvariant)]
    private static partial Regex BannerRx();

    /// <summary>
    /// Try to extract a session banner from <paramref name="line"/>. Returns
    /// <c>true</c> only when every field parses successfully — a malformed
    /// banner is treated as "not a banner" and lets the rest of the pipeline
    /// fall through normally.
    /// </summary>
    public static bool TryParse(string line, out GameSession session)
    {
        session = null!;
        if (string.IsNullOrEmpty(line)) return false;

        var m = BannerRx().Match(line);
        if (!m.Success) return false;

        var name = m.Groups["name"].Value.Trim();
        if (name.Length == 0) return false;

        var utcRaw = m.Groups["utc"].Value.Trim();
        if (!DateTime.TryParseExact(utcRaw, UtcDateFormats, CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var utc))
            return false;
        // AdjustToUniversal yields a DateTime with Kind=Utc.
        utc = DateTime.SpecifyKind(utc, DateTimeKind.Utc);

        var offRaw = m.Groups["off"].Value.Trim();
        if (!TimeSpan.TryParse(offRaw, CultureInfo.InvariantCulture, out var offset))
            return false;

        // SessionId is a stable, parser-derived natural key — two replays of
        // the same banner collapse to the same id even across Mithril restarts.
        var sessionId = $"{name}|{utc:O}";
        session = new GameSession(sessionId, name, utc, offset);
        return true;
    }
}
