using System.Text.RegularExpressions;

namespace Arwen.Parsing;

public abstract record FavorEvent(DateTime Timestamp);

/// <summary>Exact absolute favor captured when the player talks to an NPC.</summary>
public sealed record FavorUpdate(DateTime Timestamp, string NpcKey, double AbsoluteFavor) : FavorEvent(Timestamp);

/// <summary>
/// Parses Player.log lines for the <c>ProcessStartInteraction</c>
/// (→ <see cref="FavorUpdate"/>) verb that drives Arwen's
/// <c>ArwenFavorState</c> snapshot. Gift detection is handled by the Arda
/// Npc handler's internal FSM, which correlates <c>ProcessDeleteItem</c> +
/// <c>ProcessDeltaFavor</c> at L3 dispatch and emits
/// <see cref="Arda.World.Player.Events.GiftAccepted"/>.
///
/// <para>Active-character tracking lives in <c>ActiveCharacterLogSynchronizer</c> —
/// this parser does not handle <c>ProcessAddPlayer</c>.</para>
/// </summary>
public sealed partial class FavorLogParser
{
    // ProcessStartInteraction(entityId, ?, absoluteFavor, bool, "NPC_Key")
    [GeneratedRegex(@"ProcessStartInteraction\((\d+),\s*\d+,\s*([\d.-]+),\s*\w+,\s*""(NPC_\w+)""\)", RegexOptions.CultureInvariant)]
    private static partial Regex StartInteractionRx();

    public FavorEvent? TryParse(string line, DateTime timestamp)
    {
        var m = StartInteractionRx().Match(line);
        if (m.Success && double.TryParse(m.Groups[2].ValueSpan, out var favor))
            return new FavorUpdate(timestamp, m.Groups[3].Value, favor);

        return null;
    }
}
