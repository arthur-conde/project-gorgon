using FluentAssertions;
using Mithril.Shared.Inventory;
using Mithril.Shared.Reference;
using Palantir.ViewModels;
using Xunit;

namespace Palantir.Tests;

public sealed class LiveInventoryViewModelTests
{
    [Fact]
    public void Subscribe_ReplaysCurrentMap_AsRows()
    {
        var inv = new FakeInventoryService();
        inv.PreloadAsCurrent(
            new InventoryEvent(InventoryEventKind.Added, 1, "Moonstone", T(1), 1),
            new InventoryEvent(InventoryEventKind.Added, 2, "Guava", T(2), 4));

        using var vm = NewVm(inv);

        vm.Rows.Should().HaveCount(2);
        vm.LiveCount.Should().Be(2);
        vm.DeletedCount.Should().Be(0);
        vm.Rows.Single(r => r.InstanceId == 2).StackSize.Should().Be(4);
    }

    [Fact]
    public void Added_AppendsRow_WithDisplayMetadata()
    {
        var refData = new FakeRefData(
            new ItemEntry(0, "Moonstone Crystal", "Moonstone", MaxStackSize: 100, IconId: 4242, Keywords: []));
        var inv = new FakeInventoryService();
        using var vm = NewVm(inv, refData);

        inv.Fire(new InventoryEvent(InventoryEventKind.Added, 1, "Moonstone", T(1), 1));

        var row = vm.Rows.Single();
        row.Name.Should().Be("Moonstone Crystal");
        row.IconId.Should().Be(4242);
        row.IsDeleted.Should().BeFalse();
        vm.LiveCount.Should().Be(1);
    }

    [Fact]
    public void StackChanged_UpdatesExistingRowInPlace()
    {
        var inv = new FakeInventoryService();
        using var vm = NewVm(inv);

        inv.Fire(new InventoryEvent(InventoryEventKind.Added, 1, "Guava", T(1), 1));
        inv.Fire(new InventoryEvent(InventoryEventKind.StackChanged, 1, "Guava", T(2), 4));

        vm.Rows.Should().ContainSingle();
        vm.Rows[0].StackSize.Should().Be(4);
        vm.Rows[0].LastUpdated.Should().Be(T(2));
    }

    [Fact]
    public void Deleted_FlipsRowToDeleted_RetainingLastSize()
    {
        var inv = new FakeInventoryService();
        using var vm = NewVm(inv);

        inv.Fire(new InventoryEvent(InventoryEventKind.Added, 1, "GiantSkull", T(1), 1));
        inv.Fire(new InventoryEvent(InventoryEventKind.StackChanged, 1, "GiantSkull", T(2), 50));
        inv.Fire(new InventoryEvent(InventoryEventKind.Deleted, 1, "GiantSkull", T(3), 50));

        var row = vm.Rows.Single();
        row.IsDeleted.Should().BeTrue();
        row.StackSize.Should().Be(50);
        vm.LiveCount.Should().Be(0);
        vm.DeletedCount.Should().Be(1);
    }

    [Fact]
    public void DefaultView_HidesDeletedRows()
    {
        var inv = new FakeInventoryService();
        using var vm = NewVm(inv);
        inv.Fire(new InventoryEvent(InventoryEventKind.Added, 1, "Moonstone", T(1), 1));
        inv.Fire(new InventoryEvent(InventoryEventKind.Added, 2, "Guava", T(2), 4));
        inv.Fire(new InventoryEvent(InventoryEventKind.Deleted, 1, "Moonstone", T(3), 1));

        VisibleRows(vm).Select(r => r.InstanceId).Should().BeEquivalentTo([2L]);
    }

    [Fact]
    public void ShowDeletedToggle_RevealsDeletedRows()
    {
        var inv = new FakeInventoryService();
        using var vm = NewVm(inv);
        inv.Fire(new InventoryEvent(InventoryEventKind.Added, 1, "Moonstone", T(1), 1));
        inv.Fire(new InventoryEvent(InventoryEventKind.Deleted, 1, "Moonstone", T(2), 1));

        VisibleRows(vm).Should().BeEmpty();
        vm.ShowDeleted = true;
        VisibleRows(vm).Should().ContainSingle().Which.InstanceId.Should().Be(1);
    }

    [Fact]
    public void Dispose_StopsObservingEvents()
    {
        var inv = new FakeInventoryService();
        var vm = NewVm(inv);
        inv.Fire(new InventoryEvent(InventoryEventKind.Added, 1, "Moonstone", T(1), 1));
        vm.Rows.Should().HaveCount(1);

        vm.Dispose();
        inv.Fire(new InventoryEvent(InventoryEventKind.Added, 2, "Guava", T(2), 4));

        vm.Rows.Should().HaveCount(1, "the disposed VM must not append new rows");
    }

    private static LiveInventoryViewModel NewVm(IInventoryService inv, IReferenceDataService? refData = null)
        // Synchronous dispatcher: test thread is the WPF thread.
        => new(inv, refData, dispatch: action => action());

    private static IEnumerable<LiveInventoryRow> VisibleRows(LiveInventoryViewModel vm)
        => vm.View.Cast<LiveInventoryRow>();

    private static DateTime T(int seconds) => new(2026, 4, 25, 14, 0, seconds, DateTimeKind.Utc);

    /// <summary>
    /// Fake <see cref="IInventoryService"/> that records the live handler so tests
    /// can replay events synchronously and inspect VM state.
    /// </summary>
    private sealed class FakeInventoryService : IInventoryService
    {
        private readonly List<InventoryEvent> _initial = new();
        private Action<InventoryEvent>? _handler;

        public void PreloadAsCurrent(params InventoryEvent[] events) => _initial.AddRange(events);
        public void Fire(InventoryEvent e) => _handler?.Invoke(e);

        public bool TryResolve(long instanceId, out string internalName) { internalName = ""; return false; }
        public bool TryGetStackSize(long instanceId, out int stackSize) { stackSize = 0; return false; }

        public IDisposable Subscribe(Action<InventoryEvent> handler)
        {
            foreach (var e in _initial) handler(e);
            _handler = handler;
            return new Sub(this);
        }

        private sealed class Sub(FakeInventoryService owner) : IDisposable
        {
            public void Dispose() => owner._handler = null;
        }
    }

    private sealed class FakeRefData : IReferenceDataService
    {
        private readonly Dictionary<string, ItemEntry> _byName;

        public FakeRefData(params ItemEntry[] items)
        {
            _byName = items.ToDictionary(i => i.InternalName, i => i, StringComparer.Ordinal);
        }

        public IReadOnlyList<string> Keys { get; } = ["items"];
        public IReadOnlyDictionary<long, ItemEntry> Items { get; } = new Dictionary<long, ItemEntry>();
        public IReadOnlyDictionary<string, ItemEntry> ItemsByInternalName => _byName;
        public IReadOnlyDictionary<string, RecipeEntry> Recipes { get; } = new Dictionary<string, RecipeEntry>();
        public IReadOnlyDictionary<string, RecipeEntry> RecipesByInternalName { get; } = new Dictionary<string, RecipeEntry>();
        public IReadOnlyDictionary<string, SkillEntry> Skills { get; } = new Dictionary<string, SkillEntry>();
        public IReadOnlyDictionary<string, XpTableEntry> XpTables { get; } = new Dictionary<string, XpTableEntry>();
        public IReadOnlyDictionary<string, NpcEntry> Npcs { get; } = new Dictionary<string, NpcEntry>();
        public IReadOnlyDictionary<string, IReadOnlyList<ItemSource>> ItemSources { get; } = new Dictionary<string, IReadOnlyList<ItemSource>>();
        public IReadOnlyDictionary<string, AttributeEntry> Attributes { get; } = new Dictionary<string, AttributeEntry>();
        public IReadOnlyDictionary<string, PowerEntry> Powers { get; } = new Dictionary<string, PowerEntry>();
        public IReadOnlyDictionary<string, IReadOnlyList<string>> Profiles { get; } = new Dictionary<string, IReadOnlyList<string>>();
        public ReferenceFileSnapshot GetSnapshot(string key) => new(key, ReferenceFileSource.Bundled, "test", null, 0);
        public Task RefreshAsync(string key, CancellationToken ct = default) => Task.CompletedTask;
        public Task RefreshAllAsync(CancellationToken ct = default) => Task.CompletedTask;
        public void BeginBackgroundRefresh() { }
        public event EventHandler<string>? FileUpdated { add { } remove { } }
    }
}
