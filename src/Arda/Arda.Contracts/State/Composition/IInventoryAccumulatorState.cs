namespace Arda.Composition;

/// <summary>
/// A single accumulated inventory entry. Retains item identity even after removal
/// (soft delete) so downstream consumers can resolve instance IDs to item details
/// regardless of whether the item is still in the player's bag.
/// </summary>
public readonly record struct AccumulatedItem(
    string InternalName,
    string? DisplayName,
    int StackSize,
    int? TypeId,
    bool IsRemoved,
    DateTimeOffset? RemovedAt,
    DateTimeOffset FirstSeenAt,
    DateTimeOffset LastUpdatedAt);

/// <summary>
/// Read-only view of the persistent inventory accumulator maintained by the L4
/// <c>InventoryComposer</c>. Unlike the volatile L3 <c>IInventoryState</c>, this
/// retains soft-deleted entries so that post-removal lookups (e.g. gift correlation)
/// succeed.
/// </summary>
public interface IInventoryAccumulatorState
{
    /// <summary>
    /// Instance-keyed dictionary of all items ever observed. Soft-deleted entries
    /// have <see cref="AccumulatedItem.IsRemoved"/> = <c>true</c> and a non-null
    /// <see cref="AccumulatedItem.RemovedAt"/>.
    /// </summary>
    IReadOnlyDictionary<long, AccumulatedItem> Items { get; }

    /// <summary>Fires after any mutation (add, update, remove, character switch).</summary>
    event Action? StateChanged;
}
