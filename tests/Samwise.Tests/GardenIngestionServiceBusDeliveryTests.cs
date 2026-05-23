using System.IO;
using FluentAssertions;
using Mithril.GameState.Inventory;
using Mithril.GameState.Skills;
using Mithril.Shared.Audio;
using Mithril.Shared.Character;
using Mithril.Shared.Diagnostics;
using Mithril.Shared.Logging;
using Mithril.Shared.Settings;
using Mithril.GameReports;
using Mithril.WorldSim;
using Samwise.Alarms;
using Samwise.Calibration;
using Samwise.Parsing;
using Samwise.State;
using Xunit;

namespace Samwise.Tests;

/// <summary>
/// End-to-end bus-delivery guard for <see cref="GardenIngestionService"/>'s
/// post-#725 migration onto <see cref="IPlayerWorld.Bus"/>'s
/// <see cref="PlayerInventoryAdded"/> / <see cref="PlayerInventoryRemoved"/>
/// channels. Constructs the real service against a <see cref="FakePlayerWorld"/>
/// whose bus is backed by an in-process <see cref="TestEventBus"/>, calls
/// <c>StartAsync</c> to wire the subscriptions, and publishes synthetic
/// inventory frames through the bus. The state machine is asserted on the
/// real GardenStateMachine instance the service holds — proving the bus →
/// handler → state-machine path is intact end-to-end.
///
/// <para>Pairs with <c>TwoBarleyRegressionTest.SeedAddItemBeforePlant_StateMachine_ResolvesPlant</c>,
/// which pins the state-machine ledger property in isolation. The two
/// together cover both halves of the seed-resolution contract: the input
/// property (the ledger) and the delivery property (the bus subscription).
/// A regression that broke <c>Subscribe&lt;PlayerInventoryAdded&gt;</c>,
/// swapped Added↔Removed by accident, or skipped <c>_state.Apply</c> inside
/// either handler would only flag this file — the state-machine test would
/// remain green.</para>
/// </summary>
[Trait("Category", "FileIO")]
[Collection("FileIO")]
public sealed class GardenIngestionServiceBusDeliveryTests : IDisposable
{
    private readonly string _root;
    private readonly string _charactersRoot;

    public GardenIngestionServiceBusDeliveryTests()
    {
        _root = Mithril.TestSupport.TestPaths.CreateTempDir("mithril-samwise-busdelivery");
        _charactersRoot = Path.Combine(_root, "characters");
        Directory.CreateDirectory(_charactersRoot);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    [Fact]
    public async Task PlayerInventoryAdded_published_before_plant_resolves_crop_via_bus_subscription()
    {
        // ── Arrange: build the real GardenIngestionService against a
        // FakePlayerWorld whose Bus is the synthetic TestEventBus.
        var world = new FakePlayerWorld();
        var driver = new NoopLogStreamDriver();
        var skillState = new NoopSkillState();
        var parser = new GardenLogParser();
        var cfg = new InMemoryCropConfig();
        var refData = new BarleyOnlyRefData();
        var active = new FakeActiveCharacterService();
        active.Characters =
        [
            new CharacterSnapshot(
                Name: "Hits",
                Server: "live",
                ExportedAt: DateTimeOffset.UtcNow,
                Skills: new Dictionary<string, CharacterSkill>(),
                RecipeCompletions: new Dictionary<string, int>(),
                NpcFavor: new Dictionary<string, string>()),
        ];
        active.SetActiveCharacter("Hits", "live");

        var sm = new GardenStateMachine(cfg, referenceData: refData, activeChar: active, playerWorld: world);

        var store = new PerCharacterStore<GardenCharacterState>(
            _charactersRoot,
            "samwise.json",
            GardenCharacterStateJsonContext.Default.GardenCharacterState);
        using var stateService = new GardenStateService(sm, store, active);

        var settings = new SamwiseSettings();
        var settingsStore = new InMemorySettingsStore<SamwiseSettings>(
            Path.Combine(_root, "settings.json"));
        using var autoSaver = new SettingsAutoSaver<SamwiseSettings>(settingsStore, settings);

        var alarms = new AlarmService(sm, settings, new NoopAudioSink(), playerWorld: world);
        var calibration = new GrowthCalibrationService(sm, cfg, _root);

        var service = new GardenIngestionService(
            driver: driver,
            playerWorld: world,
            skillState: skillState,
            parser: parser,
            state: sm,
            stateService: stateService,
            alarms: alarms,
            calibration: calibration,
            autoSaver: autoSaver,
            diag: null);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await service.StartAsync(cts.Token);
        try
        {
            world.TestBus.SubscriberCountFor(typeof(PlayerInventoryAdded)).Should().BeGreaterThan(0,
                "StartAsync must attach the IPlayerWorld.Bus<PlayerInventoryAdded> subscription before returning " +
                "(Call 1 eager-attach contract); without this the bus-delivery path is silently dead.");

            // ── Act 1: publish the seed Add frame through the bus.
            var seedTs = new DateTime(2026, 4, 15, 20, 48, 30, DateTimeKind.Utc);
            world.TestBus.Publish(
                new DateTimeOffset(seedTs, TimeSpan.Zero),
                new PlayerInventoryAdded(InstanceId: 86940428L, InternalName: "BarleySeeds", Timestamp: seedTs));

            // ── Act 2: drive the plant verbs through the state machine
            // directly. The L1 driver path isn't under test here — the
            // assertion is that the bus-delivered Add already populated the
            // id→crop ledger, so when ProcessUpdateItemCode fires its plant-
            // resolve step the crop type maps via 86940428 → "Barley".
            var plantTs = new DateTime(2026, 4, 15, 20, 50, 22, DateTimeKind.Utc);
            ApplyParserLine(sm, parser, "LocalPlayer: ProcessSetPetOwner(590342, 588755, PassiveFollow)", plantTs);
            ApplyParserLine(sm, parser, "LocalPlayer: ProcessUpdateItemCode(86940428, 796683, True)", plantTs);

            // ── Assert: the bus-delivered Add reached the state machine and
            // the plant resolved to Barley.
            sm.Snapshot()["Hits"]["590342"].CropType.Should().Be(
                "Barley",
                "the PlayerInventoryAdded frame published on IPlayerWorld.Bus must reach the state machine via " +
                "GardenIngestionService.OnPlayerInventoryAdded → DispatchInventory → _state.Apply(AddItem), " +
                "populating the id→crop ledger so the plant resolves.");
        }
        finally
        {
            await service.StopAsync(CancellationToken.None);
            service.Dispose();
            driver.Dispose();
        }
    }

    [Fact]
    public async Task PlayerInventoryRemoved_published_after_plant_completes_plant_resolution()
    {
        // The Squash mis-identification path (TwoBarleyRegressionTest.LastSquashSeed_*):
        // PG emits ProcessDeleteItem instead of ProcessUpdateItemCode when the
        // last stack is consumed. The bus channel that carries this is
        // PlayerInventoryRemoved; the handler projects to DeleteItem (same
        // InstanceId), which triggers the state machine's plant-resolve via
        // HandleItemIdentified. Without the Removed subscription wiring, the
        // last-seed plant would land as Unknown. This test pins the
        // Removed channel end-to-end.
        var world = new FakePlayerWorld();
        var driver = new NoopLogStreamDriver();
        var skillState = new NoopSkillState();
        var parser = new GardenLogParser();
        var cfg = new InMemoryCropConfig();
        var refData = new BarleyOnlyRefData();
        var active = new FakeActiveCharacterService();
        active.Characters =
        [
            new CharacterSnapshot("Emraell", "live", DateTimeOffset.UtcNow,
                new Dictionary<string, CharacterSkill>(),
                new Dictionary<string, int>(),
                new Dictionary<string, string>()),
        ];
        active.SetActiveCharacter("Emraell", "live");

        var sm = new GardenStateMachine(cfg, referenceData: refData, activeChar: active, playerWorld: world);

        var store = new PerCharacterStore<GardenCharacterState>(
            _charactersRoot,
            "samwise.json",
            GardenCharacterStateJsonContext.Default.GardenCharacterState);
        using var stateService = new GardenStateService(sm, store, active);

        var settings = new SamwiseSettings();
        var settingsStore = new InMemorySettingsStore<SamwiseSettings>(
            Path.Combine(_root, "settings.json"));
        using var autoSaver = new SettingsAutoSaver<SamwiseSettings>(settingsStore, settings);

        var alarms = new AlarmService(sm, settings, new NoopAudioSink(), playerWorld: world);
        var calibration = new GrowthCalibrationService(sm, cfg, _root);

        var service = new GardenIngestionService(
            driver, world, skillState, parser, sm, stateService,
            alarms, calibration, autoSaver, diag: null);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await service.StartAsync(cts.Token);
        try
        {
            world.TestBus.SubscriberCountFor(typeof(PlayerInventoryRemoved)).Should().BeGreaterThan(0,
                "StartAsync must attach the IPlayerWorld.Bus<PlayerInventoryRemoved> subscription");

            // Seed the inventory ledger via the bus, then the plant.
            var seedTs = new DateTime(2026, 4, 15, 20, 48, 30, DateTimeKind.Utc);
            world.TestBus.Publish(
                new DateTimeOffset(seedTs, TimeSpan.Zero),
                new PlayerInventoryAdded(InstanceId: 93102594L, InternalName: "BarleySeeds", Timestamp: seedTs));

            // SetPetOwner fires plant_at = plantTs. Then the last-seed
            // ProcessDeleteItem arrives via PlayerInventoryRemoved — the
            // service's handler projects to DeleteItem and HandleItemIdentified
            // resolves the plant via the prior _itemIdToCrop map.
            var plantTs = new DateTime(2026, 4, 15, 20, 49, 44, DateTimeKind.Utc);
            ApplyParserLine(sm, parser, "LocalPlayer: ProcessSetPetOwner(803506, 791931, PassiveFollow)", plantTs);

            world.TestBus.Publish(
                new DateTimeOffset(plantTs, TimeSpan.Zero),
                new PlayerInventoryRemoved(InstanceId: 93102594L, InternalName: "BarleySeeds", Timestamp: plantTs));

            sm.Snapshot()["Emraell"]["803506"].CropType.Should().Be(
                "Barley",
                "the PlayerInventoryRemoved frame must reach the state machine via OnPlayerInventoryRemoved " +
                "→ _state.Apply(DeleteItem), driving plant-resolve from the prior id→crop ledger entry.");
        }
        finally
        {
            await service.StopAsync(CancellationToken.None);
            service.Dispose();
            driver.Dispose();
        }
    }

    // ── helpers ─────────────────────────────────────────────────────────

    private static void ApplyParserLine(GardenStateMachine sm, GardenLogParser parser, string line, DateTime ts)
    {
        var evt = parser.TryParse(line, ts);
        if (evt is GardenEvent ge) sm.Apply(ge);
    }

    /// <summary>
    /// Minimal <see cref="ILogStreamDriver"/> that accepts the service's
    /// <c>LocalPlayerLogLine</c> subscription but never delivers any
    /// envelopes. The L1 path isn't under test here — the bus path is.
    /// </summary>
    private sealed class NoopLogStreamDriver : ILogStreamDriver, IDisposable
    {
        public ILogSubscription Subscribe<T>(
            Func<LogEnvelope<T>, ValueTask> handler,
            LogSubscriptionOptions? options = null) where T : class
            => new NoopSubscription();

        public void Dispose() { }

        private sealed class NoopSubscription : ILogSubscription, IDisposable
        {
            public string Id { get; } = $"noop#{Guid.NewGuid():N}";
            public LogSubscriptionDiagnostics Diagnostics =>
                new(0, 0, 0, 0, 0, LogSubscriptionState.Healthy);
            public event EventHandler? StateChanged { add { } remove { } }
            public void Dispose() { }
        }
    }

    /// <summary>
    /// Minimal <see cref="IPlayerSkillState"/> — never invokes the
    /// gardening-XP callback. The bridge is unit-tested in
    /// <c>GardeningXpSkillStateBridgeTests</c>; this fixture just satisfies
    /// the constructor.
    /// </summary>
    private sealed class NoopSkillState : IPlayerSkillState
    {
        public PlayerSkillSnapshot Current => PlayerSkillSnapshot.Empty;
        public IDisposable Subscribe(Action<PlayerSkillSnapshot> handler) => new Noop();
        public IDisposable SubscribeChanges(Action<SkillChange> handler) => new Noop();
        private sealed class Noop : IDisposable { public void Dispose() { } }
    }

    private sealed class NoopAudioSink : IAudioPlaybackSink
    {
        public IPlaybackHandle Play(string? path, float volume = 1.0f, string? callerId = null, bool loop = false)
            => Handle.Instance;
        public void Stop() { }
        public void Stop(string callerId) { }

        private sealed class Handle : IPlaybackHandle
        {
            public static readonly Handle Instance = new();
            public bool IsPlaying => false;
            public void Stop() { }
            public void Dispose() { }
        }
    }

    private sealed class InMemorySettingsStore<T> : ISettingsStore<T> where T : class, new()
    {
        private T _value = new();
        public InMemorySettingsStore(string filePath) { FilePath = filePath; }
        public string FilePath { get; }
        public T Load() => _value;
        public Task<T> LoadAsync(CancellationToken ct = default) => Task.FromResult(_value);
        public void Save(T value) { _value = value; }
        public Task SaveAsync(T value, CancellationToken ct = default) { _value = value; return Task.CompletedTask; }
    }

    private sealed class BarleyOnlyRefData : Mithril.Shared.Reference.IReferenceDataService
    {
        private static readonly Mithril.Reference.Models.Items.Item _barley = new()
        {
            Id = 10251, Name = "Barley Seeds", InternalName = "BarleySeeds",
            MaxStackSize = 100, IconId = 0, Keywords = [],
        };
        public IReadOnlyList<string> Keys { get; } = ["items"];
        public IReadOnlyDictionary<long, Mithril.Reference.Models.Items.Item> Items { get; }
            = new Dictionary<long, Mithril.Reference.Models.Items.Item> { [10251L] = _barley };
        public IReadOnlyDictionary<string, Mithril.Reference.Models.Items.Item> ItemsByInternalName { get; }
            = new Dictionary<string, Mithril.Reference.Models.Items.Item>(StringComparer.Ordinal)
            { ["BarleySeeds"] = _barley };
        public Mithril.Shared.Reference.ItemKeywordIndex KeywordIndex => new(Items);
        public IReadOnlyDictionary<string, Mithril.Reference.Models.Recipes.Recipe> Recipes { get; }
            = new Dictionary<string, Mithril.Reference.Models.Recipes.Recipe>();
        public IReadOnlyDictionary<string, Mithril.Reference.Models.Recipes.Recipe> RecipesByInternalName { get; }
            = new Dictionary<string, Mithril.Reference.Models.Recipes.Recipe>();
        public IReadOnlyDictionary<string, Mithril.Shared.Reference.SkillEntry> Skills { get; }
            = new Dictionary<string, Mithril.Shared.Reference.SkillEntry>();
        public IReadOnlyDictionary<string, Mithril.Shared.Reference.XpTableEntry> XpTables { get; }
            = new Dictionary<string, Mithril.Shared.Reference.XpTableEntry>();
        public IReadOnlyDictionary<string, Mithril.Shared.Reference.NpcEntry> Npcs { get; }
            = new Dictionary<string, Mithril.Shared.Reference.NpcEntry>();
        public IReadOnlyDictionary<string, Mithril.Shared.Reference.AreaEntry> Areas { get; }
            = new Dictionary<string, Mithril.Shared.Reference.AreaEntry>(StringComparer.Ordinal);
        public IReadOnlyDictionary<string, IReadOnlyList<Mithril.Shared.Reference.ItemSource>> ItemSources { get; }
            = new Dictionary<string, IReadOnlyList<Mithril.Shared.Reference.ItemSource>>();
        public IReadOnlyDictionary<string, Mithril.Shared.Reference.AttributeEntry> Attributes { get; }
            = new Dictionary<string, Mithril.Shared.Reference.AttributeEntry>();
        public IReadOnlyDictionary<string, Mithril.Shared.Reference.PowerEntry> Powers { get; }
            = new Dictionary<string, Mithril.Shared.Reference.PowerEntry>();
        public IReadOnlyDictionary<string, IReadOnlyList<string>> Profiles { get; }
            = new Dictionary<string, IReadOnlyList<string>>();
        public IReadOnlyDictionary<string, Mithril.Reference.Models.Quests.Quest> Quests { get; }
            = new Dictionary<string, Mithril.Reference.Models.Quests.Quest>();
        public IReadOnlyDictionary<string, Mithril.Reference.Models.Quests.Quest> QuestsByInternalName { get; }
            = new Dictionary<string, Mithril.Reference.Models.Quests.Quest>();
        public IReadOnlyDictionary<string, string> Strings { get; }
            = new Dictionary<string, string>(StringComparer.Ordinal);
        public Mithril.Shared.Reference.ReferenceFileSnapshot GetSnapshot(string key)
            => new(key, Mithril.Shared.Reference.ReferenceFileSource.Bundled, "test", null, 1);
        public Task RefreshAsync(string key, CancellationToken ct = default) => Task.CompletedTask;
        public Task RefreshAllAsync(CancellationToken ct = default) => Task.CompletedTask;
        public void BeginBackgroundRefresh() { }
        public event EventHandler<string>? FileUpdated { add { } remove { } }
    }
}
