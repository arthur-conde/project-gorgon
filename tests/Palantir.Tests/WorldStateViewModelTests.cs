using FluentAssertions;
using Mithril.GameState.Areas;
using Mithril.GameState.Areas.Parsing;
using Mithril.GameState.Celestial;
using Mithril.GameState.Movement;
using Mithril.GameState.Pins;
using Mithril.GameState.Weather;
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
            pos, new PlayerAreaTracker(new AreaTransitionParser()),
            new FakePinTracker(), new FakeCelestialState(), new FakeWeatherTracker(), null, a => a());

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

    private static MapPin Pin(double x, double z, string label,
        PinShape shape = PinShape.Dot, PinColor color = PinColor.Red)
        => new(x, z, label, shape, color, RawList: 1);

    [Fact]
    public void Replay_on_subscribe_seeds_existing_pin_set()
    {
        var pins = new FakePinTracker();
        pins.Preload("AreaSerbule",
            Pin(10, -20, "Vendor"), Pin(-5.5, 99.25, ""));
        using var vm = new WorldStateViewModel(
            new FakePositionTracker(), new PlayerAreaTracker(new AreaTransitionParser()),
            pins, new FakeCelestialState(), new FakeWeatherTracker(), null, a => a());

        vm.HasPins.Should().BeTrue();
        vm.PinCount.Should().Be(2);
        vm.Pins.Should().HaveCount(2);
        // Snapshot replay reflects an existing set, not a fresh observation.
        vm.PinsObservedAtText.Should().Be("—");
    }

    [Fact]
    public void Added_pin_appends_row_and_stamps_observed_at()
    {
        using var vm = NewVm(out _, out _, out var pins);
        var pin = Pin(123.4, -567.8, "Camp", PinShape.Square, PinColor.Green);

        pins.Fire(new PinSetChanged(
            PinSetChange.Added, "AreaSerbule", pin, [pin], T));

        vm.HasPins.Should().BeTrue();
        vm.PinCount.Should().Be(1);
        var row = vm.Pins.Single();
        row.Label.Should().Be("Camp");
        row.Appearance.Should().Be("green square");
        row.Coords.Should().Be("X 123.40   Z -567.80");
        row.Detail.Should().Be("Green · Square");
        vm.PinsObservedAtText.Should().Be("2026-05-18 10:45:47Z");
    }

    [Fact]
    public void Removed_pin_drops_the_row()
    {
        using var vm = NewVm(out _, out _, out var pins);
        var a = Pin(1, 1, "A");
        var b = Pin(2, 2, "B");
        pins.Fire(new PinSetChanged(PinSetChange.Added, "X", b, [a, b], T));

        pins.Fire(new PinSetChanged(PinSetChange.Removed, "X", a, [b], T));

        vm.Pins.Select(r => r.Label).Should().ContainSingle().Which.Should().Be("B");
    }

    [Fact]
    public void Area_change_clears_the_pin_set()
    {
        using var vm = NewVm(out _, out _, out var pins);
        var p = Pin(1, 1, "Gone");
        pins.Fire(new PinSetChanged(PinSetChange.Added, "AreaA", p, [p], T));

        pins.Fire(new PinSetChanged(PinSetChange.AreaChanged, "AreaB", null, [], T));

        vm.HasPins.Should().BeFalse();
        vm.PinCount.Should().Be(0);
        vm.Pins.Should().BeEmpty();
    }

    [Fact]
    public void Unlabeled_pin_falls_back_to_placeholder_name()
    {
        using var vm = NewVm(out _, out _, out var pins);
        var p = Pin(-3.14, 0, "  ", PinShape.Unknown, PinColor.Unknown);

        pins.Fire(new PinSetChanged(PinSetChange.Added, "X", p, [p], T));

        var row = vm.Pins.Single();
        row.Label.Should().Be("Unnamed pin");
        row.Appearance.Should().Be("pin"); // unknown colour omitted, unknown shape → "pin"
        row.Coords.Should().Be("X -3.14   Z 0.00");
    }

    [Fact]
    public void Dispose_stops_observing_pins()
    {
        using var vm = NewVm(out _, out _, out var pins);
        vm.Dispose();

        var p = Pin(9, 9, "Late");
        pins.Fire(new PinSetChanged(PinSetChange.Added, "X", p, [p], T));

        vm.HasPins.Should().BeFalse("the disposed VM must unsubscribe from pins");
    }

    // --- Moon phase -------------------------------------------------------

    [Fact]
    public void No_celestial_info_shows_default()
    {
        using var vm = NewVm(out _, out _, out _, out _);

        vm.HasMoonPhase.Should().BeFalse();
        vm.MoonPhaseText.Should().Contain("no celestial");
        vm.MoonMeasuredAtText.Should().Be("—");
    }

    [Fact]
    public void Celestial_event_surfaces_phase_raw_token_and_instant()
    {
        using var vm = NewVm(out _, out _, out _, out var celestial);

        celestial.Fire(new CelestialInfo(MoonPhase.WaxingCrescent, "WaxingCrescentMoon", T));

        vm.HasMoonPhase.Should().BeTrue();
        vm.MoonPhaseText.Should().Be("Waxing Crescent");
        vm.MoonPhaseRawText.Should().Be("WaxingCrescentMoon");
        vm.MoonMeasuredAtText.Should().Be("2026-05-18 10:45:47Z");
    }

    [Fact]
    public void Unrecognised_phase_token_is_flagged_but_still_shown()
    {
        using var vm = NewVm(out _, out _, out _, out var celestial);

        celestial.Fire(new CelestialInfo(MoonPhase.Unknown, "BloodMoonEclipse", T));

        vm.HasMoonPhase.Should().BeTrue();
        vm.MoonPhaseText.Should().Be("Blood Moon Eclipse");
        vm.MoonPhaseRawText.Should().Be("BloodMoonEclipse (unrecognised token)");
    }

    [Fact]
    public void Replay_on_subscribe_seeds_existing_phase()
    {
        var celestial = new FakeCelestialState();
        celestial.PreloadAsCurrent(new CelestialInfo(MoonPhase.FullMoon, "FullMoon", T));
        using var vm = new WorldStateViewModel(
            new FakePositionTracker(), new PlayerAreaTracker(new AreaTransitionParser()),
            new FakePinTracker(), celestial, new FakeWeatherTracker(), null, a => a());

        vm.HasMoonPhase.Should().BeTrue();
        vm.MoonPhaseText.Should().Be("Full Moon");
    }

    [Fact]
    public void Dispose_stops_observing_celestial()
    {
        using var vm = NewVm(out _, out _, out _, out var celestial);
        vm.Dispose();

        celestial.Fire(new CelestialInfo(MoonPhase.FullMoon, "FullMoon", T));

        vm.HasMoonPhase.Should().BeFalse("the disposed VM must unsubscribe from celestial");
    }

    // --- Weather ----------------------------------------------------------

    [Fact]
    public void Replay_on_subscribe_seeds_existing_weather()
    {
        var weather = new FakeWeatherTracker();
        weather.Preload("AreaSerbule", new WeatherState("Foggy", true, T));
        using var vm = new WorldStateViewModel(
            new FakePositionTracker(), new PlayerAreaTracker(new AreaTransitionParser()),
            new FakePinTracker(), new FakeCelestialState(), weather, null, a => a());

        vm.HasWeather.Should().BeTrue();
        vm.WeatherConditionText.Should().Be("Foggy");
        vm.WeatherFlagText.Should().Be("True");
        // Snapshot replay reflects existing state, not a fresh observation.
        vm.WeatherObservedAtText.Should().Be("—");
    }

    [Fact]
    public void Changed_weather_populates_condition_flag_and_observed_at()
    {
        using var vm = NewVm(out _, out _, out _, out _, out var weather);

        weather.Fire(new WeatherChanged(
            WeatherChangeKind.Changed, "AreaSerbule",
            new WeatherState("Rainy", false, T), T));

        vm.HasWeather.Should().BeTrue();
        vm.WeatherConditionText.Should().Be("Rainy");
        vm.WeatherFlagText.Should().Be("False");
        vm.WeatherObservedAtText.Should().Be("2026-05-18 10:45:47Z");
    }

    [Fact]
    public void Map_change_resets_weather_to_unknown_not_stale()
    {
        using var vm = NewVm(out _, out _, out _, out _, out var weather);
        weather.Fire(new WeatherChanged(
            WeatherChangeKind.Changed, "AreaA", new WeatherState("Foggy", true, T), T));

        weather.Fire(new WeatherChanged(
            WeatherChangeKind.AreaChanged, "AreaB", null, T));

        vm.HasWeather.Should().BeFalse();
        vm.WeatherConditionText.Should().Be("(weather unknown for this map)");
        vm.WeatherFlagText.Should().Be("—");
        vm.WeatherObservedAtText.Should().Be("—");
    }

    [Fact]
    public void Dispose_stops_observing_weather()
    {
        using var vm = NewVm(out _, out _, out _, out _, out var weather);
        vm.Dispose();

        weather.Fire(new WeatherChanged(
            WeatherChangeKind.Changed, "X", new WeatherState("Foggy", true, T), T));

        vm.HasWeather.Should().BeFalse("the disposed VM must unsubscribe from weather");
    }

    private static WorldStateViewModel NewVm(
        out FakePositionTracker pos, out PlayerAreaTracker area, IReferenceDataService? refData = null)
        => NewVm(out pos, out area, out _, out _, out _, refData);

    private static WorldStateViewModel NewVm(
        out FakePositionTracker pos, out PlayerAreaTracker area,
        out FakePinTracker pins, IReferenceDataService? refData = null)
        => NewVm(out pos, out area, out pins, out _, out _, refData);

    // 4-out (celestial) — kept so the moon-phase tests' call sites stay
    // unchanged after the weather param was stacked on; delegates to the
    // 5-out base discarding weather.
    private static WorldStateViewModel NewVm(
        out FakePositionTracker pos, out PlayerAreaTracker area,
        out FakePinTracker pins, out FakeCelestialState celestial,
        IReferenceDataService? refData = null)
        => NewVm(out pos, out area, out pins, out celestial, out _, refData);

    private static WorldStateViewModel NewVm(
        out FakePositionTracker pos, out PlayerAreaTracker area,
        out FakePinTracker pins, out FakeCelestialState celestial,
        out FakeWeatherTracker weather,
        IReferenceDataService? refData = null)
    {
        pos = new FakePositionTracker();
        area = new PlayerAreaTracker(new AreaTransitionParser());
        pins = new FakePinTracker();
        celestial = new FakeCelestialState();
        weather = new FakeWeatherTracker();
        // Synchronous dispatcher: test thread is the WPF thread.
        return new WorldStateViewModel(pos, area, pins, celestial, weather, refData, a => a());
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

    private sealed class FakePinTracker : IPlayerPinTracker
    {
        private string? _area;
        private IReadOnlyList<MapPin> _pins = [];
        private Action<PinSetChanged>? _handler;

        public string? CurrentArea => _area;
        public IReadOnlyList<MapPin> CurrentAreaPins => _pins;

        /// <summary>Seed the set so the Subscribe replay (Snapshot) carries it.</summary>
        public void Preload(string area, params MapPin[] pins)
        {
            _area = area;
            _pins = pins;
        }

        public void Fire(PinSetChanged change)
        {
            _area = change.Area;
            _pins = change.Pins;
            _handler?.Invoke(change);
        }

        public IDisposable Subscribe(Action<PinSetChanged> handler)
        {
            // Mirror PlayerPinTracker: synchronous Snapshot replay first.
            handler(new PinSetChanged(
                PinSetChange.Snapshot, _area, null, _pins, DateTimeOffset.UnixEpoch));
            _handler = handler;
            return new Sub(this);
        }

        private sealed class Sub(FakePinTracker owner) : IDisposable
        {
            public void Dispose() => owner._handler = null;
        }
    }

    private sealed class FakeCelestialState : IPlayerCelestialState
    {
        private CelestialInfo? _current;
        private Action<CelestialInfo>? _handler;

        public CelestialInfo? Current => _current;

        public void PreloadAsCurrent(CelestialInfo c) => _current = c;

        public void Fire(CelestialInfo c)
        {
            _current = c;
            _handler?.Invoke(c);
        }

        public IDisposable Subscribe(Action<CelestialInfo> handler)
        {
            if (_current is not null) handler(_current);
            _handler = handler;
            return new Sub(this);
        }

        private sealed class Sub(FakeCelestialState owner) : IDisposable
        {
            public void Dispose() => owner._handler = null;
        }
    }

    private sealed class FakeWeatherTracker : IPlayerWeatherTracker
    {
        private string? _area;
        private WeatherState? _current;
        private Action<WeatherChanged>? _handler;

        public string? CurrentArea => _area;
        public WeatherState? Current => _current;

        /// <summary>Seed so the Subscribe replay (Snapshot) carries it.</summary>
        public void Preload(string area, WeatherState? state)
        {
            _area = area;
            _current = state;
        }

        public void Fire(WeatherChanged change)
        {
            _area = change.Area;
            _current = change.State;
            _handler?.Invoke(change);
        }

        public IDisposable Subscribe(Action<WeatherChanged> handler)
        {
            // Mirror PlayerWeatherTracker: synchronous Snapshot replay first.
            handler(new WeatherChanged(
                WeatherChangeKind.Snapshot, _area, _current, DateTimeOffset.UnixEpoch));
            _handler = handler;
            return new Sub(this);
        }

        private sealed class Sub(FakeWeatherTracker owner) : IDisposable
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
