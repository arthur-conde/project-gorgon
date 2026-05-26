using Arda.World.Player;

namespace Arwen.Tests;

/// <summary>
/// Tests seed the map via <see cref="Add"/> to mirror what the real inventory
/// handler would learn from <c>ProcessAddItem</c>. Implements
/// <see cref="IInventoryState"/> that <c>CalibrationService</c> consumes for
/// item resolution and stack-size queries.
/// </summary>
internal sealed class FakeInventory : IInventoryState
{
    private readonly Dictionary<long, InventoryEntry> _map = new();

    public void Add(long id, string name, int stackSize = 1) =>
        _map[id] = new InventoryEntry(name, stackSize);

    public IReadOnlyDictionary<long, InventoryEntry> Items => _map;
}
