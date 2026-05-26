using Arda.Composition;
using FluentAssertions;
using Xunit;

namespace Arda.Wpf.Tests;

public class InventoryProjectionTests : IDisposable
{
    private readonly FakeAccumulatorState _state = new();
    private readonly InventoryProjection _projection;

    public InventoryProjectionTests()
    {
        _projection = new InventoryProjection(_state);
    }

    public void Dispose() => _projection.Dispose();

    [Fact]
    public void InitialItems_ProjectedOnConstruction()
    {
        var state = new FakeAccumulatorState();
        state.SetItem(1, new AccumulatedItem("item_sword", "Iron Sword", 1, null,
            false, null, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow));

        var projection = new InventoryProjection(state);

        projection.Items.Should().ContainSingle()
            .Which.InstanceId.Should().Be(1);

        projection.Dispose();
    }

    [Fact]
    public void StateChanged_AddsNewItems()
    {
        _state.SetItem(100, new AccumulatedItem("item_sword", "Iron Sword", 1, null,
            false, null, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow));
        _state.RaiseStateChanged();

        _projection.Items.Should().ContainSingle()
            .Which.InternalName.Should().Be("item_sword");
    }

    [Fact]
    public void StateChanged_UpdatesExistingItems()
    {
        _state.SetItem(100, new AccumulatedItem("item_sword", null, 1, null,
            false, null, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow));
        _state.RaiseStateChanged();

        _state.SetItem(100, new AccumulatedItem("item_sword", "Iron Sword", 5, null,
            false, null, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow));
        _state.RaiseStateChanged();

        _projection.Items.Should().ContainSingle();
        _projection.Items[0].DisplayName.Should().Be("Iron Sword");
        _projection.Items[0].StackSize.Should().Be(5);
    }

    [Fact]
    public void StateChanged_RemovesGoneItems()
    {
        _state.SetItem(100, new AccumulatedItem("item_sword", null, 1, null,
            false, null, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow));
        _state.RaiseStateChanged();

        _state.RemoveItem(100);
        _state.RaiseStateChanged();

        _projection.Items.Should().BeEmpty();
    }

    [Fact]
    public void Dispose_UnsubscribesFromState()
    {
        _projection.Dispose();

        _state.SetItem(100, new AccumulatedItem("item_sword", null, 1, null,
            false, null, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow));
        _state.RaiseStateChanged();

        _projection.Items.Should().BeEmpty();
    }

    private sealed class FakeAccumulatorState : IInventoryAccumulatorState
    {
        private readonly Dictionary<long, AccumulatedItem> _items = new();

        public IReadOnlyDictionary<long, AccumulatedItem> Items => _items;
        public event Action? StateChanged;

        public void SetItem(long id, AccumulatedItem item) => _items[id] = item;
        public void RemoveItem(long id) => _items.Remove(id);
        public void RaiseStateChanged() => StateChanged?.Invoke();
    }
}
