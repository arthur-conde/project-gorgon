namespace Mithril.Shared.Inventory;

/// <summary>
/// An item instance that was added to the local player's inventory.
/// <paramref name="InstanceId"/> is the per-item unique id emitted by
/// <c>ProcessAddItem</c>; <paramref name="InternalName"/> maps to
/// <c>Mithril.Shared.Reference.ItemEntry.InternalName</c>.
/// </summary>
public readonly record struct InventoryItem(long InstanceId, string InternalName);

/// <summary>
/// Canonical <c>instanceId → InternalName</c> lookup maintained by tailing
/// <c>ProcessAddItem</c> / <c>ProcessDeleteItem</c> on <c>IPlayerLogStream</c>.
///
/// Owning this centrally (rather than having each module rebuild its own
/// map from the stream) avoids the late-subscribe race: modules query the
/// live map at use-time instead of relying on event replay they may have
/// missed while waiting on a gate.
/// </summary>
public interface IInventoryService
{
    /// <summary>Resolve an instance id to its internal item name, if known.</summary>
    bool TryResolve(long instanceId, out string internalName);

    /// <summary>
    /// Fired after an item instance is added to the map. Runs on the log-ingestion
    /// thread; handlers should be fast or dispatch.
    /// </summary>
    event EventHandler<InventoryItem>? ItemAdded;

    /// <summary>
    /// Fired after an item instance is removed from the map. The event carries the
    /// resolved <see cref="InventoryItem"/> so handlers can react without a prior
    /// <see cref="TryResolve"/> (which would already have missed).
    /// </summary>
    event EventHandler<InventoryItem>? ItemDeleted;
}
