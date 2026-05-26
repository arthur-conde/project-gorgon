namespace Arda.World.Player;

/// <summary>
/// A single inventory entry tracked by the Inventory handler.
/// </summary>
public readonly record struct InventoryEntry(string InternalName, int StackSize);

/// <summary>
/// Read-only view of the player's bag inventory. Inject this to query current
/// item state after replay completes — avoids subscribing to inventory events
/// just to get a point-in-time snapshot.
/// </summary>
public interface IInventoryState
{
    /// <summary>
    /// Instance-keyed dictionary of all items currently in the player's bag.
    /// Keys are PG's per-instance unique IDs; values carry the interned internal
    /// name and current stack size.
    /// </summary>
    IReadOnlyDictionary<long, InventoryEntry> Items { get; }
}
