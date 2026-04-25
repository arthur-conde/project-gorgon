using System.Text.RegularExpressions;

namespace Arwen.Parsing;

public abstract record FavorEvent(DateTime Timestamp);

/// <summary>Exact absolute favor captured when the player talks to an NPC.</summary>
public sealed record FavorUpdate(DateTime Timestamp, string NpcKey, double AbsoluteFavor) : FavorEvent(Timestamp);

/// <summary>Favor delta after a gift, quest, or hang-out.</summary>
public sealed record FavorDelta(DateTime Timestamp, string NpcKey, double Delta) : FavorEvent(Timestamp);

/// <summary>An item was removed from inventory (potential gift).</summary>
public sealed record ItemDeleted(DateTime Timestamp, long InstanceId) : FavorEvent(Timestamp);

/// <summary>
/// Parses Player.log lines for NPC favor events and gift-related item tracking.
/// Active-character tracking lives in <c>ActiveCharacterLogSynchronizer</c> — this
/// parser does not handle <c>ProcessAddPlayer</c>.
/// </summary>
public sealed partial class FavorLogParser
{
    // ProcessStartInteraction(entityId, ?, absoluteFavor, bool, "NPC_Key")
    [GeneratedRegex(@"ProcessStartInteraction\((\d+),\s*\d+,\s*([\d.-]+),\s*\w+,\s*""(NPC_\w+)""\)", RegexOptions.CultureInvariant)]
    private static partial Regex StartInteractionRx();

    // ProcessDeltaFavor(0, "NPC_Key", delta, True)
    [GeneratedRegex(@"ProcessDeltaFavor\(\d+,\s*""(NPC_\w+)"",\s*([\d.-]+),\s*\w+\)", RegexOptions.CultureInvariant)]
    private static partial Regex DeltaFavorRx();

    // ProcessDeleteItem(instanceId) — InstanceId → InternalName resolution lives in IInventoryService.
    [GeneratedRegex(@"ProcessDeleteItem\((\d+)\)", RegexOptions.CultureInvariant)]
    private static partial Regex DeleteItemRx();

    public FavorEvent? TryParse(string line, DateTime timestamp)
    {
        var m = StartInteractionRx().Match(line);
        if (m.Success && double.TryParse(m.Groups[2].ValueSpan, out var favor))
            return new FavorUpdate(timestamp, m.Groups[3].Value, favor);

        m = DeltaFavorRx().Match(line);
        if (m.Success && double.TryParse(m.Groups[2].ValueSpan, out var delta))
            return new FavorDelta(timestamp, m.Groups[1].Value, delta);

        m = DeleteItemRx().Match(line);
        if (m.Success && long.TryParse(m.Groups[1].ValueSpan, out var delId))
            return new ItemDeleted(timestamp, delId);

        return null;
    }
}
