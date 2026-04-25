using System.Text.RegularExpressions;

namespace Mithril.Shared.Inventory;

/// <summary>
/// Parses the chat <c>[Status]</c> channel for inventory-addition announcements.
/// Two formats observed in <c>ChatLogs/Chat-YY-MM-DD.log</c>:
/// <list type="bullet">
///   <item><c>[Status] &lt;DisplayName&gt; added to inventory.</c> — implies count = 1</item>
///   <item><c>[Status] &lt;DisplayName&gt; x&lt;N&gt; added to inventory.</c> — count = N</item>
/// </list>
/// The display name in chat is the player-facing item name (e.g. <c>"Egg"</c>); callers must
/// resolve it to an <c>InternalName</c> via <see cref="Mithril.Shared.Reference.IReferenceDataService.ItemsByInternalName"/>
/// to correlate with <c>ProcessAddItem</c> events. This is the only signal the game emits
/// that carries stack-size information for fresh additions (loot drops, harvests, vault
/// withdrawals into an empty bag) — without it, <c>ProcessAddItem</c> alone would default
/// every new InstanceId to size 1.
/// </summary>
public static partial class InventoryStatusChatParser
{
    // [Status] <name> x<N> added to inventory.
    [GeneratedRegex(@"\[Status\]\s+(?<name>.+?)\s+x(?<count>\d+)\s+added to inventory\.", RegexOptions.CultureInvariant)]
    private static partial Regex CountedRx();

    // [Status] <name> added to inventory.  (count implicitly 1)
    [GeneratedRegex(@"\[Status\]\s+(?<name>.+?)\s+added to inventory\.", RegexOptions.CultureInvariant)]
    private static partial Regex SingleRx();

    /// <summary>
    /// Returns <c>(DisplayName, Count)</c> if <paramref name="line"/> is a Status-channel
    /// inventory addition; <c>null</c> otherwise. The counted form is tried first so a
    /// line like <c>"[Status] Guava x42 added to inventory."</c> doesn't accidentally
    /// match the single-form regex with <c>name = "Guava x42"</c>.
    /// </summary>
    public static (string DisplayName, int Count)? TryParse(string line)
    {
        var m = CountedRx().Match(line);
        if (m.Success && int.TryParse(m.Groups["count"].ValueSpan, out var count))
            return (m.Groups["name"].Value, count);

        m = SingleRx().Match(line);
        if (m.Success)
            return (m.Groups["name"].Value, 1);

        return null;
    }
}
