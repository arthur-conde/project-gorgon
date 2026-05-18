using System.Globalization;
using System.Text.RegularExpressions;
using Mithril.Shared.Logging;

namespace Mithril.GameState.Pins;

/// <summary>
/// Parses the local player's map-pin lifecycle from Project Gorgon's
/// <c>Player.log</c> (#468). Promoted out of Legolas's <c>PlayerLogParser</c>
/// to <see cref="Mithril.GameState"/> — the same tier-promotion pattern as
/// <c>AreaTransitionParser</c> (#456) — so a single GameState service owns the
/// authoritative pin set and consumers stop hand-rolling replay-arming.
///
/// <para><b>Real captured grammar</b> (live Player.log, 3 captures,
/// 2026-05-18):</para>
/// <code>
/// [10:10:03] LocalPlayer: ProcessMapPinAdd(1, 0, 0, (1425.06, 0.00, 2924.99), "South")
/// [10:30:15] LocalPlayer: ProcessMapPinRemove(1, 0, 0, (784.74, 0.00, 3429.94), "")
/// </code>
/// Exactly two verbs exist — <c>Add</c> and <c>Remove</c>; there is no
/// edit/move/clear verb (a rename/move is Remove+Add, a fresh login/area-entry
/// is a bulk <c>Add</c> burst — both handled by <see cref="PlayerPinTracker"/>,
/// not here). The three leading integers are <c>A</c> (opaque, invariant
/// <c>1</c>), <c>B</c> = shape and <c>C</c> = colour. The middle <c>Y</c> of
/// the coordinate triple is always <c>0.00</c> and is skipped — pins are 2-D.
/// Coordinates are <b>signed</b>. Non-pin lines fast-path to null.
/// </summary>
public sealed partial class MapPinParser : ILogParser
{
    // ProcessMapPinAdd(1, 0, 0, (1425.06, 0.00, 2924.99), "South")
    //                  A  B  C    x       (y, skipped)  z     label
    [GeneratedRegex(
        """ProcessMapPin(?<verb>Add|Remove)\(\s*(?<a>-?\d+)\s*,\s*(?<b>-?\d+)\s*,\s*(?<c>-?\d+)\s*,\s*\(\s*(?<x>-?\d+(?:\.\d+)?)\s*,\s*-?\d+(?:\.\d+)?\s*,\s*(?<z>-?\d+(?:\.\d+)?)\s*\)\s*,\s*"(?<label>[^"]*)"\s*\)""",
        RegexOptions.Compiled | RegexOptions.CultureInvariant)]
    private static partial Regex MapPinRx();

    /// <summary>
    /// Parse one <c>Player.log</c> line. Pure and allocation-light: a
    /// substring fast-path rejects non-pin lines before the regex runs, so it
    /// is safe to call on every line of the stream.
    /// </summary>
    /// <param name="line">The raw log line (timestamp prefix already stripped
    /// by the tail reader, though the regex is anchored on
    /// <c>ProcessMapPin*</c> and tolerates a prefix).</param>
    /// <param name="timestamp">The line's reconstructed UTC instant, passed
    /// straight onto <see cref="MapPinLogEvent.Timestamp"/>.</param>
    /// <returns>
    /// A <see cref="MapPinLogEvent"/> for a well-formed
    /// <c>ProcessMapPinAdd</c>/<c>ProcessMapPinRemove</c> line; <c>null</c>
    /// for any other line, an empty/blank line, or a pin line whose numeric
    /// fields fail to parse (defensive — never throws on malformed input).
    /// </returns>
    public LogEvent? TryParse(string line, DateTime timestamp)
    {
        if (string.IsNullOrEmpty(line)) return null;

        if (!line.Contains("ProcessMapPin", StringComparison.Ordinal)) return null;

        if (MapPinRx().Match(line) is not { Success: true } m) return null;
        if (!TryNum(m.Groups["x"].ValueSpan, out var x)) return null;
        if (!TryNum(m.Groups["z"].ValueSpan, out var z)) return null;
        if (!TryInt(m.Groups["a"].ValueSpan, out var a)) return null;
        if (!TryInt(m.Groups["b"].ValueSpan, out var b)) return null;
        if (!TryInt(m.Groups["c"].ValueSpan, out var c)) return null;

        var change = m.Groups["verb"].ValueSpan is "Remove"
            ? MapPinChange.Removed
            : MapPinChange.Added;

        return new MapPinLogEvent(
            timestamp,
            change,
            x,
            z,
            m.Groups["label"].Value,
            b.ToPinShape(),
            c.ToPinColor(),
            a);
    }

    private static bool TryNum(ReadOnlySpan<char> s, out double v) =>
        double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out v);

    private static bool TryInt(ReadOnlySpan<char> s, out int v) =>
        int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out v);
}
