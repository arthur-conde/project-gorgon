namespace Mithril.GameState.WordsOfPower;

/// <summary>
/// View-layer effective state of a Word-of-Power code (#603). Composed from
/// the Player.log discovery record (always present if the code is visible)
/// and the chat-side last-spent timestamp (optional). Monotonic Spent — once
/// any chat utterance burns a code, it stays Spent forever for the player's
/// scope.
/// </summary>
public enum WordOfPowerKnowledge
{
    /// <summary>Code is in the codebook and has never been observed spoken.</summary>
    Known,
    /// <summary>Code has been observed spoken in chat at least once and is consumed.</summary>
    Spent,
}

/// <summary>
/// View-layer composed entry for a single Word-of-Power code (#603). Combines
/// the Player.log discovery record with the view's monotonic spent state.
/// </summary>
/// <param name="Code">The Word-of-Power code (uppercase).</param>
/// <param name="EffectName">Player-facing effect name.</param>
/// <param name="Description">Player-facing effect description.</param>
/// <param name="DiscoveredAt">UTC timestamp of the first
/// <c>ProcessBook</c> discovery line.</param>
/// <param name="LastSpentAt">UTC timestamp of the chat utterance that
/// burned this code; <c>null</c> until the first burn is observed.
/// Monotonic — once set, never cleared by the view.</param>
public sealed record WordOfPowerEntry(
    string Code,
    string EffectName,
    string Description,
    DateTime DiscoveredAt,
    DateTime? LastSpentAt)
{
    /// <summary>
    /// Effective state computation — monotonic: <c>LastSpentAt != null</c>
    /// implies Spent, else Known. No timestamp comparison, no same-second
    /// tie-break. Once Spent, forever Spent (per #603 spec).
    /// </summary>
    public WordOfPowerKnowledge State =>
        LastSpentAt is not null ? WordOfPowerKnowledge.Spent : WordOfPowerKnowledge.Known;
}
