namespace Arda.Composition;

/// <summary>
/// A single Word of Power entry tracked by the composer.
/// </summary>
public readonly record struct WordOfPowerEntry(
    string Code,
    string Effect,
    string? Description,
    DateTimeOffset DiscoveredAt,
    bool IsSpent);

/// <summary>
/// L4 cross-source composer that fuses
/// <see cref="Arda.World.Player.Events.WordOfPowerDiscovered"/> (player log) +
/// <see cref="Arda.World.Chat.Events.PlayerChatLine"/> (chat log utterance scanning)
/// to maintain a per-character word-of-power codebook.
/// </summary>
public interface IWordOfPowerComposer
{
    /// <summary>All discovered words of power for the active character.</summary>
    IReadOnlyDictionary<string, WordOfPowerEntry> Words { get; }

    /// <summary>Raised when the codebook changes (word discovered or spent-state flipped).</summary>
    event EventHandler? CodebookChanged;
}
