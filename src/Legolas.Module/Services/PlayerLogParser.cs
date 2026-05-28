using System.Globalization;
using System.Text.RegularExpressions;
using Legolas.Domain;

namespace Legolas.Services;

/// <summary>
/// Module-side text helpers for Arda domain events that carry free-text
/// payloads requiring secondary pattern extraction. The primary log-line
/// parsing (ProcessMapFx, ProcessDoDelayLoop, ProcessScreenText verb
/// envelopes) is now handled by the Arda world driver; these helpers
/// operate on the already-extracted text fields.
///
/// <list type="bullet">
///   <item><see cref="TryParseMapFxRelativeOffset"/> — directional offset
///   embedded in <c>MapFxObserved.Message</c>.</item>
///   <item><see cref="TryParseMotherlodeDistance"/> — distance readout from
///   <c>ScreenTextObserved.Text</c> (ImportantInfo category).</item>
///   <item><see cref="TryParseItemCollected"/> — survey collect readout from
///   <c>ScreenTextObserved.Text</c> (ImportantInfo category).</item>
///   <item><see cref="IsMotherlodeMapText"/> — motherlode-map use gesture
///   discriminator for <c>DelayLoopStarted.Text</c>.</item>
/// </list>
/// </summary>
public static partial class PlayerLogParser
{
    // Directional offset embedded in the MapFx trailing string: "The X is
    // Nm DIR and Mm DIR." — unchanged from pre-Arda; applied to
    // MapFxObserved.Message.ToString().
    [GeneratedRegex(
        """The (?<name>.+?) is (?<a>\d+)m (?<aDir>north|south|east|west) and (?<b>\d+)m (?<bDir>north|south|east|west)\b""",
        RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex MapFxRelativeOffsetRx();

    // Motherlode distance readout — the bare text inside ScreenTextObserved
    // (ImportantInfo category). Accepts US and British spelling.
    [GeneratedRegex(
        """^The treasure is (?<dist>\d+) met(?:er|re)s from here\.?$""",
        RegexOptions.Compiled | RegexOptions.CultureInvariant)]
    private static partial Regex MotherlodeDistanceTextRx();

    /// <summary>
    /// Extract the relative-offset readout embedded in a <c>MapFxObserved</c>
    /// message string ("The X is Nm DIR and Mm DIR."). Returns null when
    /// the message doesn't match (uncalibrated areas, atypical banners).
    /// </summary>
    public static MetreOffset? TryParseMapFxRelativeOffset(string message)
    {
        if (string.IsNullOrEmpty(message)) return null;
        var m = MapFxRelativeOffsetRx().Match(message);
        if (!m.Success
            || !int.TryParse(m.Groups["a"].ValueSpan, out var aValue)
            || !int.TryParse(m.Groups["b"].ValueSpan, out var bValue))
        {
            return null;
        }
        double east = 0, north = 0;
        ApplyComponent(m.Groups["aDir"].Value, aValue, ref east, ref north);
        ApplyComponent(m.Groups["bDir"].Value, bValue, ref east, ref north);
        return new MetreOffset(east, north);
    }

    /// <summary>
    /// Try to parse a motherlode distance readout from a ScreenTextObserved
    /// text field. Returns the distance in metres, or null if no match.
    /// </summary>
    public static int? TryParseMotherlodeDistance(string text)
    {
        if (string.IsNullOrEmpty(text)) return null;
        var m = MotherlodeDistanceTextRx().Match(text);
        if (!m.Success || !int.TryParse(m.Groups["dist"].ValueSpan, out var metres))
            return null;
        return metres;
    }

    /// <summary>
    /// Try to parse an item-collected readout from a ScreenTextObserved
    /// text field. Returns the primary item (name + count) and optional
    /// speed-bonus item (name + count), or null if the text isn't a
    /// collect line. Implicit count is 1 when the <c>xN</c> tail is absent.
    /// Span-based; allocates only on the match path (one or two
    /// <c>.ToString()</c> calls on the matched name slices).
    /// </summary>
    public static (string Name, int Count, string? BonusName, int BonusCount)? TryParseItemCollected(
        ReadOnlySpan<char> text)
    {
        if (text.IsEmpty) return null;

        // Trim an optional trailing '.' (e.g. some clients append a period).
        if (text[^1] == '.') text = text[..^1];

        const string CollectedMarker = " collected!";
        var collectedIdx = text.IndexOf(CollectedMarker.AsSpan());
        if (collectedIdx <= 0) return null;

        var primaryName = StripTrailingXn(text[..collectedIdx], out var primaryCount);
        if (primaryName.IsEmpty) return null;

        var tail = text[(collectedIdx + CollectedMarker.Length)..];
        if (tail.IsEmpty)
        {
            return (primaryName.ToString(), primaryCount, null, 0);
        }

        const string BonusPrefix = " Also found ";
        const string BonusSuffix = " (speed bonus!)";
        if (!tail.StartsWith(BonusPrefix.AsSpan()) || !tail.EndsWith(BonusSuffix.AsSpan()))
            return null;

        var bonusName = StripTrailingXn(tail[BonusPrefix.Length..^BonusSuffix.Length], out var bonusCount);
        if (bonusName.IsEmpty) return null;

        return (primaryName.ToString(), primaryCount, bonusName.ToString(), bonusCount);
    }

    // Strips a trailing " xN" suffix (where N is a positive integer) and
    // returns the leading name slice plus the count via out (defaulting
    // to 1 when absent). Same shape as ChatInventory's [Status] parser.
    // Item names containing an internal 'x' (e.g. "Wax Sculpture") are
    // unaffected because the check requires the literal " x" + digits
    // pattern at the end of the slice.
    private static ReadOnlySpan<char> StripTrailingXn(ReadOnlySpan<char> middle, out int count)
    {
        var lastSpace = middle.LastIndexOf(' ');
        if (lastSpace > 0)
        {
            var candidate = middle[(lastSpace + 1)..];
            if (candidate.Length > 1
                && candidate[0] == 'x'
                && int.TryParse(candidate[1..], NumberStyles.None, CultureInfo.InvariantCulture, out var parsed))
            {
                count = parsed;
                return middle[..lastSpace];
            }
        }
        count = 1;
        return middle;
    }

    /// <summary>
    /// True when the <c>DelayLoopStarted.Text</c> mentions a Motherlode Map
    /// — the use gesture that begins a motherlode measurement.
    /// </summary>
    public static bool IsMotherlodeMapText(ReadOnlySpan<char> text) =>
        text.Contains("Motherlode Map".AsSpan(), StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Normalise a map use action text: strips the "Using " verb prefix
    /// the PG action text carries. Display label only.
    /// </summary>
    public static string? NormalizeMapName(string raw)
    {
        var s = raw.Trim();
        if (s.StartsWith("Using ", StringComparison.OrdinalIgnoreCase))
            s = s["Using ".Length..].Trim();
        return s.Length == 0 ? null : s;
    }

    private static void ApplyComponent(string direction, int value, ref double east, ref double north)
    {
        switch (direction.ToLowerInvariant())
        {
            case "east":  east  = value;  break;
            case "west":  east  = -value; break;
            case "north": north = value;  break;
            case "south": north = -value; break;
        }
    }
}
