using Arda.Abstractions.Logs;
using Arda.World.Player;
using Arda.World.Player.Events;
using FluentAssertions;
using Mithril.Reference.Models.Items;
using Mithril.Reference.Models.Recipes;
using Mithril.Shared.Reference;
using Palantir.ViewModels;
using Xunit;

namespace Palantir.Tests;

/// <summary>
/// Tests for the Arda-backed <see cref="LiveInventoryViewModel"/>. The VM
/// shows a snapshot from <see cref="IInventoryState.Items"/> and refreshes on
/// inventory domain events via <see cref="WorldStateViewModelTests.FakeBus"/>.
/// </summary>
public sealed class LiveInventoryViewModelTests
{
    private static LogLineMetadata Meta()
        => new(DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, IsReplay: false);

    [Fact]
    public void Seed_from_state_populates_items_and_count()
    {
        var state = new FakeInventoryState(new Dictionary<long, InventoryEntry>
        {
            [1] = new("Moonstone", 1),
            [2] = new("Guava", 4),
        });
        using var vm = NewVm(state, out _);

        vm.Items.Should().HaveCount(2);
        vm.LiveCount.Should().Be(2);
    }

    [Fact]
    public void InventoryItemAdded_event_refreshes_items()
    {
        var state = new FakeInventoryState();
        using var vm = NewVm(state, out var bus);

        state.SetItems(new Dictionary<long, InventoryEntry> { [1] = new("Moonstone", 1) });
        bus.Fire(new InventoryItemAdded(1, "Moonstone", Meta()));

        vm.Items.Should().ContainSingle();
        vm.Items.Single().InternalName.Should().Be("Moonstone");
        vm.LiveCount.Should().Be(1);
    }

    [Fact]
    public void InventoryItemRemoved_event_refreshes_items()
    {
        var state = new FakeInventoryState(new Dictionary<long, InventoryEntry>
        {
            [1] = new("Moonstone", 1),
            [2] = new("Guava", 4),
        });
        using var vm = NewVm(state, out var bus);
        vm.Items.Should().HaveCount(2);

        state.SetItems(new Dictionary<long, InventoryEntry> { [2] = new("Guava", 4) });
        bus.Fire(new InventoryItemRemoved(1, "Moonstone", Meta()));

        vm.Items.Should().ContainSingle();
        vm.Items.Single().InternalName.Should().Be("Guava");
        vm.LiveCount.Should().Be(1);
    }

    [Fact]
    public void InventoryItemUpdated_event_refreshes_items_with_new_stack()
    {
        var state = new FakeInventoryState(new Dictionary<long, InventoryEntry>
        {
            [1] = new("Guava", 4),
        });
        using var vm = NewVm(state, out var bus);
        vm.Items.Single().StackSize.Should().Be(4);

        state.SetItems(new Dictionary<long, InventoryEntry> { [1] = new("Guava", 8) });
        bus.Fire(new InventoryItemUpdated(1, 8, 4, Meta()));

        vm.Items.Single().StackSize.Should().Be(8);
    }

    [Fact]
    public void RefreshCommand_reloads_from_state()
    {
        var state = new FakeInventoryState();
        using var vm = NewVm(state, out _);
        vm.Items.Should().BeEmpty();

        state.SetItems(new Dictionary<long, InventoryEntry> { [1] = new("Moonstone", 1) });
        vm.RefreshCommand.Execute(null);

        vm.Items.Should().ContainSingle();
    }

    [Fact]
    public void ReferenceData_accessor_is_surfaced()
    {
        var refData = new FakeRefData(
            new Item { Id = 0, Name = "Moonstone Crystal", InternalName = "Moonstone", MaxStackSize = 100, IconId = 4242, Keywords = [] });
        using var vm = NewVm(new FakeInventoryState(), out _, refData);

        vm.ReferenceData.Should().BeSameAs(refData);
        vm.ReferenceData!.ItemsByInternalName["Moonstone"].IconId.Should().Be(4242);
    }

    [Fact]
    public void Dispose_stops_observing_events()
    {
        var state = new FakeInventoryState(new Dictionary<long, InventoryEntry>
        {
            [1] = new("Moonstone", 1),
        });
        var vm = NewVm(state, out var bus);
        vm.LiveCount.Should().Be(1);

        vm.Dispose();
        state.SetItems(new Dictionary<long, InventoryEntry>
        {
            [1] = new("Moonstone", 1),
            [2] = new("Guava", 4),
        });
        bus.Fire(new InventoryItemAdded(2, "Guava", Meta()));

        vm.LiveCount.Should().Be(1, "the disposed VM must stop refreshing on events");
    }

    private static LiveInventoryViewModel NewVm(
        FakeInventoryState state, out WorldStateViewModelTests.FakeBus bus,
        IReferenceDataService? refData = null)
    {
        bus = new WorldStateViewModelTests.FakeBus();
        return new LiveInventoryViewModel(state, bus, refData, dispatch: a => a());
    }

    // --- Fakes --------------------------------------------------------------

    private sealed class FakeInventoryState : IInventoryState
    {
        public IReadOnlyDictionary<long, InventoryEntry> Items { get; private set; }

        public FakeInventoryState(Dictionary<long, InventoryEntry>? items = null)
            => Items = items ?? new Dictionary<long, InventoryEntry>();

        public void SetItems(Dictionary<long, InventoryEntry> items) => Items = items;
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
