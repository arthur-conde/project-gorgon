using System.Text.RegularExpressions;

namespace Arwen.Parsing;

public abstract record FavorEvent(DateTime Timestamp);

/// <summary>Exact absolute favor captured when the player talks to an NPC.</summary>
public sealed record FavorUpdate(DateTime Timestamp, string NpcKey, double AbsoluteFavor) : FavorEvent(Timestamp);

/// <summary>Favor delta after a gift, quest, or hang-out.</summary>
public sealed record FavorDelta(DateTime Timestamp, string NpcKey, double Delta) : FavorEvent(Timestamp);

/// <summary>
/// Parses Player.log lines for NPC favor events. The gift-detection FSM is
/// lifted into <c>Mithril.GameState.Gifting.IGiftSignalService</c> (Tier-2
/// signal service in <c>Mithril.GameState.Gifting</c>); the signal service
/// owns its own <c>LocalPlayerLogLine</c> subscription and parses
/// <c>ProcessDeleteItem</c> there. This parser handles only the two
/// favor-side verbs that drive Arwen's <c>ArwenFavorState</c> snapshot
/// (<see cref="FavorUpdate"/>, <see cref="FavorDelta"/>).
///
/// <para>Active-character tracking lives in <c>ActiveCharacterLogSynchronizer</c> —
/// this parser does not handle <c>ProcessAddPlayer</c>.</para>
/// </summary>
public sealed partial class FavorLogParser
{
    // ProcessStartInteraction(entityId, ?, absoluteFavor, bool, "NPC_Key")
    [GeneratedRegex(@"ProcessStartInteraction\((\d+),\s*\d+,\s*([\d.-]+),\s*\w+,\s*""(NPC_\w+)""\)", RegexOptions.CultureInvariant)]
    private static partial Regex StartInteractionRx();

    // ProcessDeltaFavor(0, "NPC_Key", delta, True)
    [GeneratedRegex(@"ProcessDeltaFavor\(\d+,\s*""(NPC_\w+)"",\s*([\d.-]+),\s*\w+\)", RegexOptions.CultureInvariant)]
    private static partial Regex DeltaFavorRx();

    public FavorEvent? TryParse(string line, DateTime timestamp)
    {
        var m = StartInteractionRx().Match(line);
        if (m.Success && double.TryParse(m.Groups[2].ValueSpan, out var favor))
            return new FavorUpdate(timestamp, m.Groups[3].Value, favor);

        m = DeltaFavorRx().Match(line);
        if (m.Success && double.TryParse(m.Groups[2].ValueSpan, out var delta))
            return new FavorDelta(timestamp, m.Groups[1].Value, delta);

        return null;
    }
}
