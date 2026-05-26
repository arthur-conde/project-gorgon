using Arda.Composition;
using Arda.Wpf;
using FluentAssertions;
using Mithril.Reference.Models.Items;
using Mithril.Reference.Models.Recipes;
using Mithril.Shared.Reference;
using Palantir.ViewModels;
using Xunit;

namespace Palantir.Tests;

/// <summary>
/// Tests for the Arda-backed <see cref="LiveInventoryViewModel"/>. The VM
/// shows items from <see cref="IInventoryAccumulatorState"/> via
/// <see cref="InventoryProjection"/>.
/// </summary>
public sealed class LiveInventoryViewModelTests
{
    [Fact]
    public void Seed_from_state_populates_items_and_count()
    {
        var state = new FakeAccumulatorState(new Dictionary<long, AccumulatedItem>
        {
            [1] = MakeItem("Moonstone", 1),
            [2] = MakeItem("Guava", 4),
        });
        using var vm = NewVm(state);

        vm.Items.Should().HaveCount(2);
        vm.LiveCount.Should().Be(2);
    }

    [Fact]
    public void StateChanged_adds_new_items()
    {
        var state = new FakeAccumulatorState();
        using var vm = NewVm(state);

        state.SetItems(new Dictionary<long, AccumulatedItem>
        {
            [1] = MakeItem("Moonstone", 1)
        });

        vm.Items.Should().ContainSingle();
        vm.Items.Single().InternalName.Should().Be("Moonstone");
        vm.LiveCount.Should().Be(1);
    }

    [Fact]
    public void StateChanged_removes_evicted_items()
    {
        var state = new FakeAccumulatorState(new Dictionary<long, AccumulatedItem>
        {
            [1] = MakeItem("Moonstone", 1),
            [2] = MakeItem("Guava", 4),
        });
        using var vm = NewVm(state);
        vm.Items.Should().HaveCount(2);

        state.SetItems(new Dictionary<long, AccumulatedItem>
        {
            [2] = MakeItem("Guava", 4)
        });

        vm.Items.Should().ContainSingle();
        vm.Items.Single().InternalName.Should().Be("Guava");
        vm.LiveCount.Should().Be(1);
    }

    [Fact]
    public void StateChanged_updates_stack_size()
    {
        var state = new FakeAccumulatorState(new Dictionary<long, AccumulatedItem>
        {
            [1] = MakeItem("Guava", 4),
        });
        using var vm = NewVm(state);
        vm.Items.Single().StackSize.Should().Be(4);

        state.SetItems(new Dictionary<long, AccumulatedItem>
        {
            [1] = MakeItem("Guava", 8)
        });

        vm.Items.Single().StackSize.Should().Be(8);
    }

    [Fact]
    public void Soft_deleted_items_tracked_separately()
    {
        var state = new FakeAccumulatorState(new Dictionary<long, AccumulatedItem>
        {
            [1] = MakeItem("Moonstone", 1),
            [2] = MakeItem("Guava", 4, isRemoved: true),
        });
        using var vm = NewVm(state);

        vm.Items.Should().HaveCount(2);
        vm.LiveCount.Should().Be(1);
        vm.DeletedCount.Should().Be(1);
    }

    [Fact]
    public void RefreshCommand_reloads_from_state()
    {
        var state = new FakeAccumulatorState();
        using var vm = NewVm(state);
        vm.Items.Should().BeEmpty();

        state.MutateWithoutNotify(new Dictionary<long, AccumulatedItem>
        {
            [1] = MakeItem("Moonstone", 1)
        });
        vm.RefreshCommand.Execute(null);

        vm.Items.Should().ContainSingle();
    }

    [Fact]
    public void ReferenceData_accessor_is_surfaced()
    {
        var refData = new FakeRefData(
            new Item { Id = 0, Name = "Moonstone Crystal", InternalName = "Moonstone", MaxStackSize = 100, IconId = 4242, Keywords = [] });
        using var vm = NewVm(new FakeAccumulatorState(), refData);

        vm.ReferenceData.Should().BeSameAs(refData);
        vm.ReferenceData!.ItemsByInternalName["Moonstone"].IconId.Should().Be(4242);
    }

    [Fact]
    public void Dispose_stops_observing_events()
    {
        var state = new FakeAccumulatorState(new Dictionary<long, AccumulatedItem>
        {
            [1] = MakeItem("Moonstone", 1),
        });
        var vm = NewVm(state);
        vm.LiveCount.Should().Be(1);

        vm.Dispose();

        state.SetItems(new Dictionary<long, AccumulatedItem>
        {
            [1] = MakeItem("Moonstone", 1),
            [2] = MakeItem("Guava", 4),
        });

        vm.LiveCount.Should().Be(1, "the disposed VM must stop refreshing on events");
    }

    // --- Helpers ---------------------------------------------------------------

    private static AccumulatedItem MakeItem(string name, int stack, bool isRemoved = false) =>
        new(name, null, stack, null, isRemoved,
            isRemoved ? DateTimeOffset.UtcNow : null,
            DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);

    private static LiveInventoryViewModel NewVm(
        FakeAccumulatorState state, IReferenceDataService? refData = null)
    {
        return new LiveInventoryViewModel(state, refData);
    }

    // --- Fakes -----------------------------------------------------------------

    private sealed class FakeAccumulatorState : IInventoryAccumulatorState
    {
        private Dictionary<long, AccumulatedItem> _items;

        public IReadOnlyDictionary<long, AccumulatedItem> Items => _items;
        public event Action? StateChanged;

        public FakeAccumulatorState(Dictionary<long, AccumulatedItem>? items = null)
            => _items = items ?? new Dictionary<long, AccumulatedItem>();

        public void SetItems(Dictionary<long, AccumulatedItem> items)
        {
            _items = items;
            StateChanged?.Invoke();
        }

        public void MutateWithoutNotify(Dictionary<long, AccumulatedItem> items)
            => _items = items;
    }

    private sealed class FakeRefData : IReferenceDataService
    {
        private readonly Dictionary<string, Item> _byName;

        public FakeRefData(params Item[] items)
        {
            _byName = items
                .Where(i => !string.IsNullOrEmpty(i.InternalName))
                .ToDictionary(i => i.InternalName!, i => i, StringComparer.Ordinal);
        }

        public IReadOnlyList<string> Keys { get; } = ["items"];
        public IReadOnlyDictionary<long, Item> Items { get; } = new Dictionary<long, Item>();
        public IReadOnlyDictionary<string, Item> ItemsByInternalName => _byName;
        public ItemKeywordIndex KeywordIndex { get; } = ItemKeywordIndex.Empty;
        public IReadOnlyDictionary<string, Recipe> Recipes { get; } = new Dictionary<string, Recipe>();
        public IReadOnlyDictionary<string, Recipe> RecipesByInternalName { get; } = new Dictionary<string, Recipe>();
        public IReadOnlyDictionary<string, Mithril.Shared.Reference.SkillEntry> Skills { get; } = new Dictionary<string, Mithril.Shared.Reference.SkillEntry>();
        public IReadOnlyDictionary<string, XpTableEntry> XpTables { get; } = new Dictionary<string, XpTableEntry>();
        public IReadOnlyDictionary<string, NpcEntry> Npcs { get; } = new Dictionary<string, NpcEntry>();
        public IReadOnlyDictionary<string, AreaEntry> Areas { get; } = new Dictionary<string, AreaEntry>(StringComparer.Ordinal);
        public IReadOnlyDictionary<string, IReadOnlyList<ItemSource>> ItemSources { get; } = new Dictionary<string, IReadOnlyList<ItemSource>>();
        public IReadOnlyDictionary<string, AttributeEntry> Attributes { get; } = new Dictionary<string, AttributeEntry>();
        public IReadOnlyDictionary<string, PowerEntry> Powers { get; } = new Dictionary<string, PowerEntry>();
        public IReadOnlyDictionary<string, IReadOnlyList<string>> Profiles { get; } = new Dictionary<string, IReadOnlyList<string>>();
        public IReadOnlyDictionary<string, Mithril.Reference.Models.Quests.Quest> Quests { get; } = new Dictionary<string, Mithril.Reference.Models.Quests.Quest>();
        public IReadOnlyDictionary<string, Mithril.Reference.Models.Quests.Quest> QuestsByInternalName { get; } = new Dictionary<string, Mithril.Reference.Models.Quests.Quest>();
        public IReadOnlyDictionary<string, string> Strings { get; } = new Dictionary<string, string>(StringComparer.Ordinal);
        public ReferenceFileSnapshot GetSnapshot(string key) => new(key, ReferenceFileSource.Bundled, "test", null, 0);
        public Task RefreshAsync(string key, CancellationToken ct = default) => Task.CompletedTask;
        public Task RefreshAllAsync(CancellationToken ct = default) => Task.CompletedTask;
        public void BeginBackgroundRefresh() { }
        public event EventHandler<string>? FileUpdated { add { } remove { } }
    }
}
