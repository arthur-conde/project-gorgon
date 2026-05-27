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

    // Survey collect readout — the bare text inside ScreenTextObserved
    // (ImportantInfo category). Primary mineral + optional speed-bonus tail.
    [GeneratedRegex(
        """^(?<name>.+?)(?:\s+x\d+)?\s+collected!(?:\s+Also found\s+(?<bonus>.+?)(?:\s+x\d+)?\s+\(speed bonus!\))?\.?$""",
        RegexOptions.Compiled | RegexOptions.CultureInvariant)]
    private static partial Regex ItemCollectedTextRx();

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
    /// text field. Returns the item name and optional speed-bonus item,
    /// or null if no match.
    /// </summary>
    public static (string Name, string? SpeedBonusItem)? TryParseItemCollected(string text)
    {
        if (string.IsNullOrEmpty(text)) return null;
        var m = ItemCollectedTextRx().Match(text);
        if (!m.Success) return null;
        string? bonus = m.Groups["bonus"].Success ? m.Groups["bonus"].Value.Trim() : null;
        return (m.Groups["name"].Value.Trim(), bonus);
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
