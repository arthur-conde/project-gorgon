using Mithril.WorldSim;

namespace Mithril.GameState.WordsOfPower;

/// <summary>
/// Canonical Words-of-Power surface for modules (#603). Composes the
/// PlayerWorld discovery state (via
/// <see cref="IPlayerWordOfPowerDiscoveryState"/>) with chat-side burn
/// observations from the ChatWorld bus, keyed by code. The join is purely by
/// code — no TTL, no time-window: discovery and spent may be hours/days apart.
///
/// <para>Spent state is <b>monotonic</b>: once any chat utterance burns a
/// code, it stays <see cref="WordOfPowerKnowledge.Spent"/> forever. The
/// chat-utterance recognition runs an uppercase-token regex (length ≥ 4)
/// + codebook validation against the player's discovery state — only tokens
/// that match a known code flip the state. Codes the player has never
/// discovered are invisible to the view.</para>
///
/// <para><b>Why a view, not a service.</b> The pre-#603
/// <c>SarumanCodebookService</c> mutated from both Player.log and chat
/// ingestion paths. That violates the world-sim principle 3 — no service spans
/// both sources. The split puts each source in its own world; cross-source
/// composition lives in this view layer (per principle 4 + design notebook
/// §Worked example 2).</para>
/// </summary>
[Obsolete("Use Arda.Composition.IWordOfPowerComposer instead.")]
public interface IWordOfPowerView
{
    /// <summary>
    /// The view's typed-frame bus. Subscribe via
    /// <c>Bus.Subscribe&lt;WordOfPowerKnowledgeChanged&gt;(...)</c> for the
    /// canonical post-migration surface.
    /// </summary>
    IWorldEventBus Bus { get; }

    /// <summary>
    /// Snapshot of every composed entry for the active character. Empty when
    /// no character is active or the character has never discovered a code.
    /// </summary>
    IReadOnlyCollection<WordOfPowerEntry> Entries { get; }

    /// <summary>
    /// Resolve a code to its composed entry. Returns <c>null</c> when the
    /// code has not been discovered (or no character is active). Spent vs
    /// Known is read off the returned entry's
    /// <see cref="WordOfPowerEntry.State"/>.
    /// </summary>
    WordOfPowerEntry? TryGet(string code);

    /// <summary>
    /// <c>true</c> if the code is in the view's codebook and has ever been
    /// observed spoken in chat. Equivalent to
    /// <c>TryGet(code)?.State == Spent</c>.
    /// </summary>
    bool IsSpent(string code);

    /// <summary>
    /// Subscribe to codebook changes via a coarse event (a typed-bus-free
    /// surface for VMs that want a single "refresh me" pulse). Fires for both
    /// state flips and fresh discoveries. The view raises this on the same
    /// thread the triggering frame was applied — subscribers must not block.
    /// </summary>
    event EventHandler? CodebookChanged;
}
