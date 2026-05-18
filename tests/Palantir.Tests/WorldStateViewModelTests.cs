using FluentAssertions;
using Mithril.GameState.Areas;
using Mithril.GameState.Areas.Parsing;
using Mithril.GameState.Movement;
using Mithril.Reference.Models.Items;
using Mithril.Reference.Models.Recipes;
using Mithril.Shared.Reference;
using Palantir.ViewModels;
using Xunit;

namespace Palantir.Tests;

public sealed class WorldStateViewModelTests
{
    private static readonly DateTimeOffset T =
        new(2026, 5, 18, 10, 45, 47, TimeSpan.Zero);

    [Fact]
    public void No_area_no_position_shows_defaults()
    {
        using var vm = NewVm(out _, out _);

        vm.AreaKey.Should().Be("(none)");
        vm.AreaResolved.Should().BeFalse();
        vm.HasPosition.Should().BeFalse();
        vm.PositionText.Should().Contain("no position");
    }

    [Fact]
    public void Refresh_resolves_area_through_areas_json()
    {
        var refData = new FakeRefData(("AreaSerbule", new AreaEntry("AreaSerbule", "Serbule", "Serb")));
        using var vm = NewVm(out _, out var area, refData);

        area.Observe("LOADING LEVEL AreaSerbule", DateTime.UtcNow);
        vm.RefreshCommand.Execute(null); // PlayerAreaTracker has no change event — explicit re-read.

        vm.AreaKey.Should().Be("AreaSerbule");
        vm.AreaFriendlyName.Should().Be("Serbule");
        vm.AreaShortName.Should().Be("Serb");
        vm.AreaResolved.Should().BeTrue();
    }

    [Fact]
    public void Short_name_suppressed_when_equal_to_friendly_name()
    {
        var refData = new FakeRefData(("AreaTomb1", new AreaEntry("AreaTomb1", "The Tomb", "The Tomb")));
        using var vm = NewVm(out _, out var area, refData);

        area.Observe("LOADING LEVEL AreaTomb1", DateTime.UtcNow);
        vm.RefreshCommand.Execute(null);

        vm.AreaFriendlyName.Should().Be("The Tomb");
        vm.AreaShortName.Should().BeEmpty();
    }

    [Fact]
    public void Area_key_known_but_absent_from_areas_json_falls_back_to_key()
    {
        using var vm = NewVm(out _, out var area, new FakeRefData());

        area.Observe("LOADING LEVEL AreaMysteryZone", DateTime.UtcNow);
        vm.RefreshCommand.Execute(null);

        vm.AreaKey.Should().Be("AreaMysteryZone");
        vm.AreaFriendlyName.Should().Be("AreaMysteryZone");
        vm.AreaResolved.Should().BeFalse();
    }

    [Fact]
    public void Position_event_populates_coords_instant_and_reresolves_area()
    {
        var refData = new FakeRefData(("AreaSerbule", new AreaEntry("AreaSerbule", "Serbule", "Serb")));
        using var vm = NewVm(out var pos, out var area, refData);

        area.Observe("LOADING LEVEL AreaSerbule", DateTime.UtcNow);
        pos.Fire(new PlayerPosition(834.09, 290.24, 3480.81, T, PlayerPositionSource.Movement));

        vm.HasPosition.Should().BeTrue();
        vm.PositionText.Should().Be("X 834.09   Y 290.24   Z 3480.81");
        vm.MeasuredAtText.Should().Be("2026-05-18 10:45:47Z");
        vm.PositionSourceText.Should().Contain("ProcessNewPosition");
        // The position update also re-resolved the area (zone-change coincidence).
        vm.AreaFriendlyName.Should().Be("Serbule");
    }

    [Fact]
    public void Replay_on_subscribe_seeds_existing_position()
    {
        var pos = new FakePositionTracker();
        pos.PreloadAsCurrent(new PlayerPosition(1, 2, 3, T, PlayerPositionSource.Spawn));
        using var vm = new WorldStateViewModel(
            pos, new PlayerAreaTracker(new AreaTransitionParser()), null, a => a());

        vm.HasPosition.Should().BeTrue();
        vm.PositionText.Should().Be("X 1.00   Y 2.00   Z 3.00");
        vm.PositionSourceText.Should().Contain("Spawn");
    }

    [Fact]
    public void Dispose_stops_observing_position()
    {
        using var vm = NewVm(out var pos, out _);
        vm.Dispose();

        pos.Fire(new PlayerPosition(9, 9, 9, T, PlayerPositionSource.Movement));

        vm.HasPosition.Should().BeFalse("the disposed VM must unsubscribe");
    }

    private static WorldStateViewModel NewVm(
        out FakePositionTracker pos, out PlayerAreaTracker area, IReferenceDataService? refData = null)
    {
        pos = new FakePositionTracker();
        area = new PlayerAreaTracker(new AreaTransitionParser());
        // Synchronous dispatcher: test thread is the WPF thread.
        return new WorldStateViewModel(pos, area, refData, a => a());
    }

    private sealed class FakePositionTracker : IPlayerPositionTracker
    {
        private PlayerPosition? _current;
        private Action<PlayerPosition>? _handler;

        public PlayerPosition? Current => _current;

        public void PreloadAsCurrent(PlayerPosition p) => _current = p;

        public void Fire(PlayerPosition p)
        {
            _current = p;
            _handler?.Invoke(p);
        }

        public IDisposable Subscribe(Action<PlayerPosition> handler)
        {
            if (_current is not null) handler(_current);
            _handler = handler;
            return new Sub(this);
        }

        private sealed class Sub(FakePositionTracker owner) : IDisposable
        {
            public void Dispose() => owner._handler = null;
        }
    }

    private sealed class FakeRefData : IReferenceDataService
    {
        private readonly Dictionary<string, AreaEntry> _areas;

        public FakeRefData(params (string Key, AreaEntry Entry)[] areas)
        {
            _areas = areas.ToDictionary(a => a.Key, a => a.Entry, StringComparer.Ordinal);
        }

        public IReadOnlyDictionary<string, AreaEntry> Areas => _areas;

        public IReadOnlyList<string> Keys { get; } = ["areas"];
        public IReadOnlyDictionary<long, Item> Items { get; } = new Dictionary<long, Item>();
        public IReadOnlyDictionary<string, Item> ItemsByInternalName { get; } = new Dictionary<string, Item>();
        public ItemKeywordIndex KeywordIndex { get; } = ItemKeywordIndex.Empty;
        public IReadOnlyDictionary<string, Recipe> Recipes { get; } = new Dictionary<string, Recipe>();
        public IReadOnlyDictionary<string, Recipe> RecipesByInternalName { get; } = new Dictionary<string, Recipe>();
        public IReadOnlyDictionary<string, SkillEntry> Skills { get; } = new Dictionary<string, SkillEntry>();
        public IReadOnlyDictionary<string, XpTableEntry> XpTables { get; } = new Dictionary<string, XpTableEntry>();
        public IReadOnlyDictionary<string, NpcEntry> Npcs { get; } = new Dictionary<string, NpcEntry>();
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
