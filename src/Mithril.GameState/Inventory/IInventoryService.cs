namespace Mithril.GameState.Inventory;

/// <summary>
/// Query-channel surface for the live <c>instanceId → InternalName</c> +
/// stack-size ledger composed by <see cref="InventoryView"/> (#602 split).
/// Retained post-#659 for Arwen's gift-attribution path; the same concrete
/// <see cref="InventoryView"/> implements both this interface and the broader
/// <see cref="IInventoryView"/>. New code that wants change events or the
/// bindable items collection takes <see cref="IInventoryView"/>; consumers
/// that only need point-in-time lookups can stay on this interface.
///
/// <para>The pre-#659 React-channel <c>Subscribe(Action&lt;InventoryEvent&gt;)</c>
/// shim retired with the issue once all six pre-#602 consumers migrated to
/// their post-shim destinations (PlayerWorld-direct for Samwise / Legolas /
/// Motherlode, the Bind channel for Palantir, the Tier-2
/// <c>IGiftSignalService</c> for Arwen, blueprint-only for Saruman).
/// The accompanying <c>InventoryEvent</c> union and <c>InventoryEventKind</c>
/// enum were deleted at the same time — change-event consumers subscribe to
/// the typed <see cref="InventoryItemAdded"/> / <see cref="InventoryItemRemoved"/>
/// / <see cref="InventoryStackChanged"/> frames on
/// <see cref="IInventoryView.Bus"/>.</para>
/// </summary>
public interface IInventoryService
{
    /// <summary>
    /// Resolve an instance id to its internal item name, if known. Returns
    /// true even for ids that have been deleted — the entry is retained so
    /// late lookups (e.g. Arwen's gift-attribution path) still succeed.
    /// </summary>
    bool TryResolve(long instanceId, out string internalName);

    /// <summary>
    /// Resolve an instance id to its current stack size, if and only if the
    /// size has been *confirmed* by an authoritative source — chat correlation,
    /// <c>ProcessUpdateItemCode</c>, <c>ProcessRemoveFromStorageVault</c>,
    /// export seeding, or export reconcile. Returns false when:
    /// <list type="bullet">
    ///   <item>The instance has never been observed (carryover from a prior
    ///   game session whose AddItem isn't in this log's replay buffer).</item>
    ///   <item>The instance is in the map but its size is still the
    ///   unconfirmed default of 1 (a session-replayed AddItem with no chat
    ///   status in the correlation window). A real stack of 1 is
    ///   distinguishable: the chat status confirms it, the corresponding
    ///   AddItem path flips confirmation on, and TryGetStackSize reports
    ///   <c>true, 1</c>.</item>
    /// </list>
    /// Like <see cref="TryResolve"/>, returns the last-known confirmed size
    /// for entries that have been deleted in this session — Arwen's
    /// gift-attribution path needs the pre-delete size.
    /// </summary>
    bool TryGetStackSize(long instanceId, out int stackSize);
}
