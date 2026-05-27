using System.IO;
using Arda.Abstractions.Logs;
using Arda.Contracts;
using Arda.World.Player.Events;
using FluentAssertions;
using Mithril.Shared.Audio;
using Mithril.Shared.Character;
using Mithril.Shared.Diagnostics;
using Mithril.Shared.Settings;
using Mithril.GameReports;
using Samwise.Alarms;
using Samwise.Calibration;
using Samwise.Parsing;
using Samwise.State;
using Xunit;

namespace Samwise.Tests;

/// <summary>
/// End-to-end delivery guard for <see cref="GardenIngestionService"/>'s
/// Arda domain-event subscriptions. Constructs the real service against a
/// <see cref="TestDomainEventBus"/> and publishes synthetic Arda events
/// through the bus. The state machine is asserted on the real
/// GardenStateMachine instance the service holds — proving the bus →
/// handler → state-machine path is intact end-to-end.
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
    public async Task InventoryItemAdded_published_before_plant_resolves_crop_via_bus_subscription()
    {
        var bus = new TestDomainEventBus();
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

        var sm = new GardenStateMachine(cfg, referenceData: refData, activeChar: active);

        var store = new PerCharacterStore<GardenCharacterState>(
            _charactersRoot,
            "samwise.json",
            GardenCharacterStateJsonContext.Default.GardenCharacterState);
        using var stateService = new GardenStateService(sm, store, active);

        var settings = new SamwiseSettings();
        var settingsStore = new InMemorySettingsStore<SamwiseSettings>(
            Path.Combine(_root, "settings.json"));
        using var autoSaver = new SettingsAutoSaver<SamwiseSettings>(settingsStore, settings);

        var alarms = new AlarmService(sm, settings, new NoopAudioSink(), bus: bus);
        var calibration = new GrowthCalibrationService(sm, cfg, _root);

        var service = new GardenIngestionService(
            bus: bus,
            state: sm,
            stateService: stateService,
            alarms: alarms,
            calibration: calibration,
            autoSaver: autoSaver,
            logger: null);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await service.StartAsync(cts.Token);
        try
        {
            bus.SubscriberCountFor<InventoryItemAdded>().Should().BeGreaterThan(0,
                "StartAsync must attach the IDomainEventSubscriber<InventoryItemAdded> subscription before returning " +
                "(eager-attach contract); without this the bus-delivery path is silently dead.");

            // Publish the seed Add event through the Arda bus.
            var seedTs = new DateTime(2026, 4, 15, 20, 48, 30, DateTimeKind.Utc);
            bus.Publish(new InventoryItemAdded(
                InstanceId: 86940428L,
                InternalName: "BarleySeeds",
                Metadata: new LogLineMetadata(
                    new DateTimeOffset(seedTs, TimeSpan.Zero),
                    DateTimeOffset.UtcNow,
                    IsReplay: false)));

            // Drive the plant verbs through the Arda bus directly.
            var plantTs = new DateTime(2026, 4, 15, 20, 50, 22, DateTimeKind.Utc);
            bus.Publish(new SetPetOwnerFrame(
                EntityId: 590342,
                Metadata: new LogLineMetadata(
                    new DateTimeOffset(plantTs, TimeSpan.Zero),
                    DateTimeOffset.UtcNow,
                    IsReplay: false)));

            bus.Publish(new InventoryItemUpdated(
                InstanceId: 86940428L,
                NewStackSize: 1,
                PreviousStackSize: 2,
                Metadata: new LogLineMetadata(
                    new DateTimeOffset(plantTs, TimeSpan.Zero),
                    DateTimeOffset.UtcNow,
                    IsReplay: false)));

            // The bus-delivered Add reached the state machine and the plant resolved.
            sm.Snapshot()["Hits"]["590342"].CropType.Should().Be(
                "Barley",
                "the InventoryItemAdded event published on IDomainEventSubscriber must reach the state machine, " +
                "populating the id→crop ledger so the plant resolves.");
        }
        finally
        {
            await service.StopAsync(CancellationToken.None);
            service.Dispose();
        }
    }

    [Fact]
    public async Task InventoryItemRemoved_published_after_plant_completes_plant_resolution()
    {
        var bus = new TestDomainEventBus();
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

        var sm = new GardenStateMachine(cfg, referenceData: refData, activeChar: active);

        var store = new PerCharacterStore<GardenCharacterState>(
            _charactersRoot,
            "samwise.json",
            GardenCharacterStateJsonContext.Default.GardenCharacterState);
        using var stateService = new GardenStateService(sm, store, active);

        var settings = new SamwiseSettings();
        var settingsStore = new InMemorySettingsStore<SamwiseSettings>(
            Path.Combine(_root, "settings.json"));
        using var autoSaver = new SettingsAutoSaver<SamwiseSettings>(settingsStore, settings);

        var alarms = new AlarmService(sm, settings, new NoopAudioSink(), bus: bus);
        var calibration = new GrowthCalibrationService(sm, cfg, _root);

        var service = new GardenIngestionService(
            bus, sm, stateService, alarms, calibration, autoSaver, logger: null);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await service.StartAsync(cts.Token);
        try
        {
            bus.SubscriberCountFor<InventoryItemRemoved>().Should().BeGreaterThan(0,
                "StartAsync must attach the IDomainEventSubscriber<InventoryItemRemoved> subscription");

            // Seed the inventory ledger via the bus, then the plant.
            var seedTs = new DateTime(2026, 4, 15, 20, 48, 30, DateTimeKind.Utc);
            bus.Publish(new InventoryItemAdded(
                InstanceId: 93102594L,
                InternalName: "BarleySeeds",
                Metadata: new LogLineMetadata(
                    new DateTimeOffset(seedTs, TimeSpan.Zero),
                    DateTimeOffset.UtcNow,
                    IsReplay: false)));

            var plantTs = new DateTime(2026, 4, 15, 20, 49, 44, DateTimeKind.Utc);
            bus.Publish(new SetPetOwnerFrame(
                EntityId: 803506,
                Metadata: new LogLineMetadata(
                    new DateTimeOffset(plantTs, TimeSpan.Zero),
                    DateTimeOffset.UtcNow,
                    IsReplay: false)));

            bus.Publish(new InventoryItemRemoved(
                InstanceId: 93102594L,
                InternalName: "BarleySeeds",
                Metadata: new LogLineMetadata(
                    new DateTimeOffset(plantTs, TimeSpan.Zero),
                    DateTimeOffset.UtcNow,
                    IsReplay: false)));

            sm.Snapshot()["Emraell"]["803506"].CropType.Should().Be(
                "Barley",
                "the InventoryItemRemoved event must reach the state machine via OnInventoryRemoved " +
                "→ _state.Apply(DeleteItem), driving plant-resolve from the prior id→crop ledger entry.");
        }
        finally
        {
            await service.StopAsync(CancellationToken.None);
            service.Dispose();
        }
    }

    // ── test infrastructure ─────────────────────────────────────────────

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
