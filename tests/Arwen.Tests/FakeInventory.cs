using Mithril.Shared.Inventory;

namespace Arwen.Tests;

/// <summary>
/// Tests seed the map via <see cref="Add"/> to mirror what the real
/// <c>InventoryService</c> would learn from <c>ProcessAddItem</c> and the
/// chat-correlation / <c>UpdateItemCode</c> paths.
/// </summary>
internal sealed class FakeInventory : IInventoryService
{
    private readonly Dictionary<long, (string Name, int StackSize)> _map = new();
    public void Add(long id, string name, int stackSize = 1) => _map[id] = (name, stackSize);
    public bool TryResolve(long instanceId, out string internalName)
    {
        if (_map.TryGetValue(instanceId, out var entry)) { internalName = entry.Name; return true; }
        internalName = "";
        return false;
    }
    public bool TryGetStackSize(long instanceId, out int stackSize)
    {
        if (_map.TryGetValue(instanceId, out var entry) && entry.StackSize > 0)
        {
            stackSize = entry.StackSize;
            return true;
        }
        stackSize = 0;
        return false;
    }
    public IDisposable Subscribe(Action<InventoryEvent> handler) => NoopSubscription.Instance;

    private sealed class NoopSubscription : IDisposable
    {
        public static readonly NoopSubscription Instance = new();
        public void Dispose() { }
    }
}
