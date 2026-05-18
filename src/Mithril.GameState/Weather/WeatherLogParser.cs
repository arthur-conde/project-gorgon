using System.Text.RegularExpressions;
using Mithril.Shared.Logging;

namespace Mithril.GameState.Weather;

/// <summary>
/// Parses the local player's ambient weather from Project Gorgon's
/// <c>Player.log</c>. The same tier-promotion / single-verb shape as
/// <c>MapPinParser</c> (#468) and <c>PlayerPositionParser</c> — a single
/// GameState service owns the authoritative state and consumers stop
/// hand-rolling the parse.
///
/// <para><b>Captured grammar</b> (single sample, 2026-05-18 — <b>not yet
/// corpus-verified</b>):</para>
/// <code>
/// [19:50:42] LocalPlayer: ProcessSetWeather("Foggy", True)
/// </code>
/// One verb (<c>ProcessSetWeather</c>); the first argument is a quoted
/// condition string surfaced verbatim, the second is an opaque boolean
/// (<see cref="WeatherChangedEvent.Flag"/> — semantics Verification owed).
/// Non-weather lines fast-path to <c>null</c>; a malformed weather line never
/// throws (defensive, mirrors <c>MapPinParser</c>).
///
/// <para>Weather is <b>per-map</b> (owner-confirmed) — it does not carry from
/// one map to the next; <see cref="PlayerWeatherTracker"/> drops it on a map
/// change and the new map's <c>ProcessSetWeather</c> repopulates it (the same
/// area-scoped lifecycle as <c>PlayerPinTracker</c>).</para>
///
/// <para><b>Verification owed</b> (needs a live Player.log pass before this
/// grammar can be promoted into <c>log-patterns.json</c>): the meaning of the
/// boolean; whether a <c>False</c> form exists; whether weather is re-emitted
/// on zone entry (replay) or only on genuine transitions. The tracker's
/// per-map, idempotent, drop-on-map-change stance is correct under every one
/// of those remaining hypotheses.</para>
/// </summary>
public sealed partial class WeatherLogParser : ILogParser
{
    // ProcessSetWeather("Foggy", True)
    //                    condition  flag
    [GeneratedRegex(
        """ProcessSetWeather\(\s*"(?<condition>[^"]*)"\s*,\s*(?<flag>True|False)\s*\)""",
        RegexOptions.Compiled | RegexOptions.CultureInvariant)]
    private static partial Regex WeatherRx();

    /// <summary>
    /// Parse one <c>Player.log</c> line. Pure and allocation-light: a substring
    /// fast-path rejects non-weather lines before the regex runs, so it is safe
    /// to call on every line of the stream.
    /// </summary>
    /// <param name="line">The raw log line (the regex is anchored on
    /// <c>ProcessSetWeather</c> and tolerates the timestamp/<c>LocalPlayer:</c>
    /// prefix).</param>
    /// <param name="timestamp">The line's reconstructed UTC instant, passed
    /// straight onto <see cref="WeatherChangedEvent.Timestamp"/>.</param>
    /// <returns>
    /// A <see cref="WeatherChangedEvent"/> for a well-formed
    /// <c>ProcessSetWeather</c> line; <c>null</c> for any other line, an
    /// empty/blank line, or a weather line that fails the shape (defensive —
    /// never throws on malformed input).
    /// </returns>
    public LogEvent? TryParse(string line, DateTime timestamp)
    {
        if (string.IsNullOrEmpty(line)) return null;

        if (!line.Contains("ProcessSetWeather", StringComparison.Ordinal)) return null;

        if (WeatherRx().Match(line) is not { Success: true } m) return null;

        var flag = m.Groups["flag"].ValueSpan is "True";

        return new WeatherChangedEvent(
            timestamp,
            m.Groups["condition"].Value,
            flag);
    }
}
