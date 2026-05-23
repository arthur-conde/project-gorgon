namespace Mithril.GameState.Inventory;

/// <summary>
/// PlayerWorld half of the post-split inventory (#602). An instance-id-keyed
/// ledger of <c>InternalName</c>s folded from Player.log's
/// <c>ProcessAddItem</c> / <c>ProcessDeleteItem</c> stream. Carries no
/// stack-size column — composition with chat-side <c>[Status]</c> observations
/// happens in <see cref="IInventoryView"/>; authoritative Player.log
/// stack-size signals (<c>ProcessUpdateItemCode</c>,
/// <c>ProcessRemoveFromStorageVault</c>) are emitted as their own
/// <see cref="PlayerInventoryStackUpdated"/> change events on the PlayerWorld
/// bus.
///
/// <para>Deleted entries are retained so late lookups (e.g. Arwen's
/// gift-attribution path) still resolve the <c>InternalName</c> of an
/// already-deleted instance — mirrors the legacy pre-split inventory
/// service's <c>TryResolve</c> contract.</para>
///
/// <para><b>Naming.</b> Follows #657 — folder interfaces take the form
/// <c>I&lt;World&gt;&lt;Domain&gt;State</c>.</para>
/// </summary>
public interface IPlayerInventoryState
{
    /// <summary>
    /// Resolve an instance id to its <c>InternalName</c>, if known. Returns
    /// true even for ids that have been deleted — the entry is retained so
    /// late lookups still succeed.
    /// </summary>
    bool TryResolve(long instanceId, out string internalName);
}
