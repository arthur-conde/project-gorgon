using System.IO;
using Arda.Abstractions.Logs;
using Arda.Dispatch;
using Arda.World.Player.Events;
using Arwen.Domain;
using Arwen.State;
using FluentAssertions;
using Mithril.Reference.Models.Items;
using Mithril.Shared.Character;
using Mithril.Shared.Modules;
using Mithril.Shared.Reference;
using Mithril.Shared.Settings;
using Mithril.GameReports;
using Xunit;
using ArdaGiftAccepted = Arda.World.Player.Events.GiftAccepted;

namespace Arwen.Tests;

/// <summary>
/// Regression test for <see cref="FavorIngestionService"/> after the Arda
/// migration. The service subscribes to <see cref="IDomainEventSubscriber"/> for
/// <see cref="InteractionStarted"/> (favor snapshots) and
/// <see cref="ArdaGiftAccepted"/> (resolved gifts for calibration).
///
/// <para>The test feeds events through a <see cref="TestDomainEventBus"/>
/// and asserts identical final state across replay shapes. The calibration
/// sink-layer dedup (<c>_observationKeys</c> HashSet) keeps state
/// byte-equivalent across replays.</para>
/// </summary>
[Trait("Category", "FileIO")]
[Collection("FileIO")]
public sealed class FavorIngestionServiceTests : IDisposable
{
    private readonly string _root;
    private readonly string _charactersRoot;
    private readonly string _arwenDir;

    public FavorIngestionServiceTests()
    {
        _root = Mithril.TestSupport.TestPaths.CreateTempDir("mithril-arwen-l1");
        _arwenDir = Path.Combine(_root, "Arwen");
        _charactersRoot = Path.Combine(_root, "characters");
        Directory.CreateDirectory(_arwenDir);
        Directory.CreateDirectory(_charactersRoot);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* best-effort */ }
    }

    [Fact]
    public async Task Arda_favor_snapshot_replays_identically_across_run_shapes()
    {
        var giftedAt = new DateTimeOffset(2026, 5, 19, 10, 30, 00, TimeSpan.Zero);
        var meta = new LogLineMetadata(giftedAt, giftedAt, IsReplay: false);

        // ── Pass 1: all live (no replay).
        FavorIngestionFixture passLive;
        using (passLive = NewFixture("Pass1"))
        {
            await passLive.StartAsync();
            passLive.Bus.Publish(new InteractionStarted(42, "NPC_Sanja", 100.0, IsNpc: true, meta));
            passLive.Bus.Publish(new ArdaGiftAccepted(42, "NPC_Sanja", 7001, "Moonstone", 30.0, meta));
            await passLive.StopAsync();
        }

        // ── Pass 2: same events, same timestamps (simulates replay).
        FavorIngestionFixture passSplit;
        using (passSplit = NewFixture("Pass2"))
        {
            await passSplit.StartAsync();
            var replayMeta = new LogLineMetadata(giftedAt, giftedAt, IsReplay: true);
            passSplit.Bus.Publish(new InteractionStarted(42, "NPC_Sanja", 100.0, IsNpc: true, replayMeta));
            passSplit.Bus.Publish(new ArdaGiftAccepted(42, "NPC_Sanja", 7001, "Moonstone", 30.0, replayMeta));
            await passSplit.StopAsync();
        }

        // Both passes recorded the same gift.
        passLive.Calibration.Data.Observations.Should().HaveCount(1);
        passSplit.Calibration.Data.Observations.Should().HaveCount(1);

        var live = passLive.Calibration.Data.Observations[0];
        var split = passSplit.Calibration.Data.Observations[0];

        split.NpcKey.Should().Be(live.NpcKey);
        split.ItemInternalName.Should().Be(live.ItemInternalName);
        split.FavorDelta.Should().Be(live.FavorDelta);
        split.Quantity.Should().Be(live.Quantity);
        split.InstanceId.Should().Be(live.InstanceId);
        split.Timestamp.Should().Be(live.Timestamp);

        // Exact-favor snapshot from the InteractionStarted also matches.
        var liveFavor = passLive.FavorState.GetExactFavor("NPC_Sanja");
        var splitFavor = passSplit.FavorState.GetExactFavor("NPC_Sanja");
        splitFavor.Should().NotBeNull();
        liveFavor.Should().NotBeNull();
        splitFavor!.ExactFavor.Should().Be(liveFavor!.ExactFavor);
    }

    [Fact]
    public async Task Sink_layer_dedup_collapses_replay_on_relaunch()
    {
        var giftedAt = new DateTimeOffset(2026, 5, 19, 10, 30, 00, TimeSpan.Zero);
        var meta = new LogLineMetadata(giftedAt, giftedAt, IsReplay: false);

        using var fixture = NewFixture("DedupRelaunch");
        await fixture.StartAsync();

        // First pass — observation lands.
        fixture.Bus.Publish(new InteractionStarted(42, "NPC_Sanja", 100.0, IsNpc: true, meta));
        fixture.Bus.Publish(new ArdaGiftAccepted(42, "NPC_Sanja", 7001, "Moonstone", 30.0, meta));

        fixture.Calibration.Data.Observations.Should().HaveCount(1);
        var afterFirst = fixture.Calibration.Data.Observations[0];

        // Second pass — same events, same log-line timestamps. Calibration's
        // ObservationKey dedup must drop the replay.
        fixture.Bus.Publish(new InteractionStarted(42, "NPC_Sanja", 100.0, IsNpc: true, meta));
        fixture.Bus.Publish(new ArdaGiftAccepted(42, "NPC_Sanja", 7001, "Moonstone", 30.0, meta));

        fixture.Calibration.Data.Observations.Should().HaveCount(1);
        var afterSecond = fixture.Calibration.Data.Observations[0];

        afterSecond.NpcKey.Should().Be(afterFirst.NpcKey);
        afterSecond.ItemInternalName.Should().Be(afterFirst.ItemInternalName);
        afterSecond.FavorDelta.Should().Be(afterFirst.FavorDelta);
        afterSecond.Quantity.Should().Be(afterFirst.Quantity);
        afterSecond.InstanceId.Should().Be(afterFirst.InstanceId);
        afterSecond.Timestamp.Should().Be(afterFirst.Timestamp);

        await fixture.StopAsync();
    }

    [Fact]
    public async Task Favor_snapshot_timestamp_uses_log_line_instant_not_wall_clock()
    {
        var giftedAt = new DateTimeOffset(2020, 1, 2, 3, 4, 5, TimeSpan.Zero);
        var meta = new LogLineMetadata(giftedAt, giftedAt, IsReplay: false);

        using var passOne = NewFixture("PassOne");
        await passOne.StartAsync();
        passOne.Bus.Publish(new InteractionStarted(42, "NPC_Sanja", 73.5, IsNpc: true, meta));
        await passOne.StopAsync();

        await Task.Delay(TimeSpan.FromMilliseconds(50));

        using var passTwo = NewFixture("PassTwo");
        await passTwo.StartAsync();
        passTwo.Bus.Publish(new InteractionStarted(42, "NPC_Sanja", 73.5, IsNpc: true, meta));
        await passTwo.StopAsync();

        var snapOne = passOne.FavorState.GetExactFavor("NPC_Sanja");
        var snapTwo = passTwo.FavorState.GetExactFavor("NPC_Sanja");

        snapOne.Should().NotBeNull();
        snapTwo.Should().NotBeNull();
        snapOne!.Timestamp.Should().Be(giftedAt,
            "the persisted timestamp must come from the log envelope, not the host's real wall clock");
        snapTwo!.Timestamp.Should().Be(snapOne.Timestamp,
            "replaying the same log line yields a byte-identical snapshot regardless of real-elapsed time");
    }

    [Fact]
    public async Task Subscription_attaches_in_constructor_before_StartAsync()
    {
        // The Arda bus subscriptions wire in the constructor so they are in
        // place before any driver starts pumping. Verify that publishing
        // an event AFTER StartAsync still reaches the handler (the
        // subscription was already live).

        var giftedAt = new DateTimeOffset(2026, 5, 19, 10, 30, 00, TimeSpan.Zero);
        var meta = new LogLineMetadata(giftedAt, giftedAt, IsReplay: true);

        using var fixture = NewFixture("EagerStartAsync");

        fixture.Gates.For("arwen").IsOpen.Should().BeFalse(
            "the gate-retirement audit — this test must not touch ModuleGate.Open to validate the eager attach");

        await fixture.Service.StartAsync(CancellationToken.None);

        fixture.Bus.Publish(new InteractionStarted(42, "NPC_Sanja", 100.0, IsNpc: true, meta));

        await fixture.Service.StopAsync(CancellationToken.None);

        var favor = fixture.FavorState.GetExactFavor("NPC_Sanja");
        favor.Should().NotBeNull(
            "the bus subscription processes the InteractionStarted while the gate stayed closed");
        favor!.ExactFavor.Should().Be(100.0);
    }

    [Fact]
    public async Task Gift_event_routes_to_calibration_service()
    {
        // Verify the Arda GiftAccepted → legacy GiftAccepted adapter
        // correctly routes to CalibrationService.OnGiftAccepted.

        var giftedAt = new DateTimeOffset(2026, 5, 19, 10, 30, 00, TimeSpan.Zero);
        var meta = new LogLineMetadata(giftedAt, giftedAt, IsReplay: false);

        using var fixture = NewFixture("GiftRoute");
        await fixture.StartAsync();

        fixture.Bus.Publish(new ArdaGiftAccepted(42, "NPC_Sanja", 7001, "Moonstone", 30.0, meta));

        fixture.Calibration.Data.Observations.Should().HaveCount(1,
            "the Arda GiftAccepted event should route through to CalibrationService");
        var observation = fixture.Calibration.Data.Observations[0];
        observation.NpcKey.Should().Be("NPC_Sanja");
        observation.ItemInternalName.Should().Be("Moonstone");
        observation.FavorDelta.Should().Be(30.0);
        observation.InstanceId.Should().Be(7001);

        await fixture.StopAsync();
    }

    [Fact]
    public async Task Non_npc_interaction_is_ignored()
    {
        var ts = new DateTimeOffset(2026, 5, 19, 10, 30, 00, TimeSpan.Zero);
        var meta = new LogLineMetadata(ts, ts, IsReplay: false);

        using var fixture = NewFixture("NonNpc");
        await fixture.StartAsync();

        fixture.Bus.Publish(new InteractionStarted(99, "Chest_Serbule", 0, IsNpc: false, meta));

        await fixture.StopAsync();

        fixture.FavorState.GetExactFavor("Chest_Serbule").Should().BeNull(
            "non-NPC interactions should be filtered out");
    }

    // ── Helpers ───────────────────────────────────────────────────────

    private FavorIngestionFixture NewFixture(string passName) =>
        new(_charactersRoot, _arwenDir, passName);

    /// <summary>
    /// Composes a fresh <see cref="FavorIngestionService"/> end-to-end with
    /// a <see cref="TestDomainEventBus"/>, seeded reference data, and a
    /// per-test data directory for calibration persistence.
    /// </summary>
    private sealed class FavorIngestionFixture : IDisposable
    {
        public TestDomainEventBus Bus { get; }
        public CalibrationService Calibration { get; }
        public ArwenFavorState FavorState { get; }
        public FavorIngestionService Service { get; }
        public ModuleGates Gates { get; }

        private readonly PerCharacterView<ArwenFavorState> _view;
        private readonly SettingsAutoSaver<ArwenSettings> _saver;

        public FavorIngestionFixture(string charactersRoot, string arwenDir, string passName)
        {
            var refData = BuildRefData();
            var index = new GiftIndex();
            index.Build(refData.Items, refData.Npcs);
            var inv = new FakeInventory();
            var dataDir = Path.Combine(arwenDir, passName);
            Directory.CreateDirectory(dataDir);
            Calibration = new CalibrationService(refData, index, inv, dataDir);

            var active = new FakeActiveCharacterService
            {
                Characters =
                [
                    new CharacterSnapshot("Arthur", "Kwatoxi", default,
                        new Dictionary<string, CharacterSkill>(), new Dictionary<string, int>(),
                        new Dictionary<string, string>()),
                ],
            };
            active.SetActiveCharacter("Arthur", "Kwatoxi");

            var store = new PerCharacterStore<ArwenFavorState>(charactersRoot, "arwen.json",
                ArwenFavorStateJsonContext.Default.ArwenFavorState);
            _view = new PerCharacterView<ArwenFavorState>(active, store);
            FavorState = _view.Current!;

            var stateService = new FavorStateService(refData, active, _view);

            var settings = new ArwenSettings();
            _saver = new SettingsAutoSaver<ArwenSettings>(
                new InMemorySettingsStore(),
                settings);

            Gates = new ModuleGates();

            Bus = new TestDomainEventBus();
            Service = new FavorIngestionService(
                Bus,
                stateService,
                Calibration,
                _view,
                active,
                _saver);
        }

        public async Task StartAsync()
        {
            await Service.StartAsync(CancellationToken.None);
        }

        public async Task StopAsync()
        {
            try { await Service.StopAsync(CancellationToken.None); }
            catch (OperationCanceledException) { }
        }

        public void Dispose()
        {
            Service.Dispose();
            _saver.Dispose();
            _view.Dispose();
        }

        private static IReferenceDataService BuildRefData()
        {
            var items = new Dictionary<long, Item>
            {
                [1] = new Item
                {
                    Id = 1, Name = "Moonstone", InternalName = "Moonstone",
                    MaxStackSize = 1, IconId = 0,
                    Keywords = [new ItemKeyword("Moonstone", 500)],
                    Value = 100m,
                },
            };
            var npcs = new Dictionary<string, NpcEntry>(StringComparer.Ordinal)
            {
                ["NPC_Sanja"] = new("NPC_Sanja", "Sanja", "Serbule",
                    [new NpcPreference("Love", ["Moonstone"], "Moonstones", 1.5, null)],
                    ["Friends"], []),
            };
            return new FakeRefData(items, npcs);
        }

        private sealed class InMemorySettingsStore : ISettingsStore<ArwenSettings>
        {
            private ArwenSettings _v = new();
            public string FilePath => "(memory)";
            public ArwenSettings Load() => _v;
            public Task<ArwenSettings> LoadAsync(CancellationToken ct = default) => Task.FromResult(_v);
            public Task SaveAsync(ArwenSettings value, CancellationToken ct = default) { _v = value; return Task.CompletedTask; }
            public void Save(ArwenSettings value) => _v = value;
        }
    }

    /// <summary>
    /// Minimal in-memory event bus for testing. Implements both
    /// <see cref="IDomainEventSubscriber"/> and <see cref="IDomainEventPublisher"/>
    /// and dispatches synchronously, matching production Arda bus semantics.
    /// </summary>
    private sealed class TestDomainEventBus : IDomainEventSubscriber, IDomainEventPublisher
    {
        private readonly Dictionary<Type, List<Delegate>> _handlers = new();

        public IDisposable Subscribe<T>(Action<T> handler) where T : struct
        {
            var type = typeof(T);
            if (!_handlers.TryGetValue(type, out var list))
            {
                list = new List<Delegate>();
                _handlers[type] = list;
            }
            list.Add(handler);
            return new Subscription(this, type, handler);
        }

        public void Publish<T>(T domainEvent) where T : struct
        {
            if (!_handlers.TryGetValue(typeof(T), out var list)) return;
            foreach (var h in list.ToArray())
                ((Action<T>)h)(domainEvent);
        }

        private sealed class Subscription(TestDomainEventBus bus, Type type, Delegate handler) : IDisposable
        {
            public void Dispose()
            {
                if (bus._handlers.TryGetValue(type, out var list))
                    list.Remove(handler);
            }
        }
    }
}
