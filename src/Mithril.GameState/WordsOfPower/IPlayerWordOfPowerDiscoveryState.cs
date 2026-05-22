namespace Mithril.GameState.WordsOfPower;

/// <summary>
/// PlayerWorld half of the post-split Words-of-Power codebook (#603). A
/// per-character <c>code → DiscoveryRecord</c> ledger folded from
/// <c>ProcessBook("You discovered a word of power!", …)</c> Player.log events.
/// Emits <see cref="PlayerWordOfPowerDiscovered"/> on the PlayerWorld bus
/// whenever a new code is observed for the first time.
///
/// <para>Discovery is monotonic — codes are random per-discovery strings, so a
/// re-observation of the same code is impossible by construction. The folder
/// still elides duplicates defensively (PG can replay <c>ProcessBook</c> lines
/// on login replay, and the persisted state covers the cold-start window).</para>
///
/// <para>Spent state lives at the view layer, not here — discovery and spent
/// are orthogonal axes per the #603 design notebook §Worked example 2. The
/// canonical Words-of-Power surface for module consumers is
/// <c>IWordOfPowerView</c>.</para>
///
/// <para><b>Naming.</b> Follows #657 — folder interfaces take the form
/// <c>I&lt;World&gt;&lt;Domain&gt;State</c>.</para>
/// </summary>
public interface IPlayerWordOfPowerDiscoveryState
{
    /// <summary>
    /// Snapshot of every discovered code for the active character. Empty when
    /// no character is active or the character has never learned a code.
    /// </summary>
    IReadOnlyCollection<DiscoveryRecord> Discoveries { get; }

    /// <summary>
    /// Resolve a code to its discovery record, if known. Returns <c>null</c>
    /// when the code has never been observed (or no character is active).
    /// </summary>
    DiscoveryRecord? TryGet(string code);
}
