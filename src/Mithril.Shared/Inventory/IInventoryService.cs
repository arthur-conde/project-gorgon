namespace Mithril.Shared.Inventory;

/// <summary>
/// A live inventory transition. <paramref name="InstanceId"/> is the per-item
/// unique id emitted by <c>ProcessAddItem</c> / <c>ProcessDeleteItem</c>.
/// <paramref name="InternalName"/> maps to
/// <see cref="Mithril.Shared.Reference.ItemEntry.InternalName"/>.
/// <paramref name="Timestamp"/> is the source log line's timestamp (UTC), not
/// wall-clock — consumers with time-window logic (e.g. Samwise's plant-resolve
/// window) need the in-game timeline.
/// </summary>
public readonly record struct InventoryEvent(
    InventoryEventKind Kind,
    long InstanceId,
    string InternalName,
    DateTime Timestamp);

public enum InventoryEventKind
{
    Added,
    Deleted,
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
    /// Resolve an instance id to its current stack size, if known. Like
    /// <see cref="TryResolve"/>, returns the last-known size for entries that
    /// have been deleted in this session — Arwen's gift-attribution path needs
    /// the pre-delete size. Returns false for instances the service has never
    /// observed an event for (e.g. items carried over from a prior PG game
    /// session that haven't been touched).
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
    /// Stack-size mutations (split, merge, plant-consume, vault transfer) are
    /// not surfaced as events on this subscription — consumers that need
    /// up-to-date sizes should poll <see cref="TryGetStackSize"/>.
    ///
    /// Dispose the returned subscription to stop receiving further events.
    /// </summary>
    IDisposable Subscribe(Action<InventoryEvent> handler);
}
