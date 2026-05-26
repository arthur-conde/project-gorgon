using Arda.Composition;

namespace Arwen.Tests;

/// <summary>
/// Tests seed the map via <see cref="Add"/> to mirror what the real accumulator
/// would learn from inventory events. Implements
/// <see cref="IInventoryAccumulatorState"/> that <c>CalibrationService</c> consumes
/// for item resolution and stack-size queries.
/// </summary>
internal sealed class FakeInventory : IInventoryAccumulatorState
{
    private readonly Dictionary<long, AccumulatedItem> _map = new();

    public void Add(long id, string name, int stackSize = 1) =>
        _map[id] = new AccumulatedItem(
            name,
            DisplayName: null,
            stackSize,
            TypeId: null,
            IsRemoved: false,
            RemovedAt: null,
            FirstSeenAt: DateTimeOffset.UtcNow,
            LastUpdatedAt: DateTimeOffset.UtcNow);

    public IReadOnlyDictionary<long, AccumulatedItem> Items => _map;
#pragma warning disable CS0067 // interface-required event not fired in test fake
    public event Action? StateChanged;
#pragma warning restore CS0067
}
