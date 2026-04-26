namespace Mithril.Shared.Inventory;

/// <summary>
/// A live inventory transition. <paramref name="InstanceId"/> is the per-item
/// unique id emitted by <c>ProcessAddItem</c> / <c>ProcessDeleteItem</c>.
/// <paramref name="InternalName"/> maps to
/// <see cref="Mithril.Shared.Reference.ItemEntry.InternalName"/>.
/// <paramref name="Timestamp"/> is the source log line's timestamp (UTC), not
/// wall-clock — consumers with time-window logic (e.g. Samwise's plant-resolve
/// window) need the in-game timeline.
/// <paramref name="StackSize"/> is the stack size at the moment of the event:
/// for <see cref="InventoryEventKind.Added"/> it's the size known when the
/// item entered the map (1 if no chat correlation has landed yet);
/// for <see cref="InventoryEventKind.Deleted"/> it's the last-known size
/// before removal; for <see cref="InventoryEventKind.StackChanged"/> it's
/// the new size.
/// <paramref name="SizeConfirmed"/> is the same bit
/// <see cref="IInventoryService.TryGetStackSize"/> consults — false when the
/// <paramref name="StackSize"/> is the unconfirmed default-1 (a session-replayed
/// AddItem with no chat correlation), true when an authoritative source
/// (chat, UpdateItemCode, vault, export seed/reconcile) has spoken.
/// <see cref="InventoryEventKind.StackChanged"/> events always carry
/// <c>SizeConfirmed = true</c>.
/// </summary>
public readonly record struct InventoryEvent(
    InventoryEventKind Kind,
    long InstanceId,
    string InternalName,
    DateTime Timestamp,
    int StackSize,
    bool SizeConfirmed = false);

public enum InventoryEventKind
{
    Added,
    Deleted,
    /// <summary>
    /// Fired when the stack size of an existing entry changes in place
    /// (split, merge, plant-consume, vault transfer, chat back-fill of a
    /// previously-defaulted size). Not fired on Add or Delete — those carry
    /// their own size on the event.
    /// </summary>
    StackChanged,
}

/// <summary>
/// Canonical <c>instanceId → InternalName</c> lookup maintained by tailing
/// <c>ProcessAddItem</c> / <c>ProcessDeleteItem</c> / <c>ProcessUpdateItemCode</c>
/// / <c>ProcessRemoveFromStorageVault</c> on <c>IPlayerLogStream</c>, plus the
/// chat <c>[Status]</c> channel via <c>IChatLogStream</c> for stack-size
/// correlation on fresh additions.
///
/// Owning this centrally (rather than having each module rebuild its own map
/// from the stream) avoids the late-subscribe race: modules either query the
/// live map at use-time via <see cref="TryResolve"/> or
/// <see cref="TryGetStackSize"/>, or subscribe via <see cref="Subscribe"/> which
/// atomically replays the current map state to the new handler before delivering
/// live events.
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
    ///   unconfirmed default of 1 (a session-replayed AddItem with no
    ///   chat status in the correlation window). A real stack of 1 is
    ///   distinguishable: the chat status confirms it, the corresponding
    ///   AddItem path flips confirmation on, and TryGetStackSize reports
    ///   <c>true, 1</c>.</item>
    /// </list>
    /// Like <see cref="TryResolve"/>, returns the last-known confirmed size
    /// for entries that have been deleted in this session — Arwen's
    /// gift-attribution path needs the pre-delete size.
    /// </summary>
    bool TryGetStackSize(long instanceId, out int stackSize);

    /// <summary>
    /// Attach a handler that receives every live (non-deleted) item currently
    /// in the map (as synthesized <see cref="InventoryEventKind.Added"/>
    /// events) followed by every live add/delete. Replay and live-attach are
    /// atomic — no event is lost, duplicated, or reordered relative to the
    /// canonical map.
    ///
    /// The handler is invoked synchronously under an internal lock both during
    /// replay (on the subscribing thread) and during live dispatch (on the
    /// ingestion-loop thread). Subscribers that do non-trivial work should
    /// dispatch off-thread immediately to avoid blocking ingestion.
    ///
    /// Stack-size mutations (split, merge, plant-consume, vault transfer,
    /// chat back-fill of a previously-defaulted Add) are surfaced as
    /// <see cref="InventoryEventKind.StackChanged"/> events. The replayed
    /// <see cref="InventoryEventKind.Added"/> events carry the current size,
    /// so a fresh subscriber doesn't need to poll <see cref="TryGetStackSize"/>
    /// for steady-state rendering.
    ///
    /// Dispose the returned subscription to stop receiving further events.
    /// </summary>
    IDisposable Subscribe(Action<InventoryEvent> handler);
}
