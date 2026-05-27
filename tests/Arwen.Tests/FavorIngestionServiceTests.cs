using System.IO;
using Arda.Abstractions.Logs;
using Arda.Contracts;
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
/// Tests for <see cref="FavorIngestionService"/> after the NpcStateComposer migration.
/// The service now only subscribes to <see cref="ArdaGiftAccepted"/> — favor accumulation
/// is handled by the Arda L4 NpcStateComposer.
/// </summary>
[Trait("Category", "FileIO")]
[Collection("FileIO")]
public sealed class FavorIngestionServiceTests : IDisposable
{
    private readonly string _root;
    private readonly string _arwenDir;

    public FavorIngestionServiceTests()
    {
        _root = Mithril.TestSupport.TestPaths.CreateTempDir("mithril-arwen-l1");
        _arwenDir = Path.Combine(_root, "Arwen");
        Directory.CreateDirectory(_arwenDir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* best-effort */ }
    }

    [Fact]
    public async Task Gift_event_routes_to_calibration_service()
    {
        var giftedAt = new DateTimeOffset(2026, 5, 19, 10, 30, 00, TimeSpan.Zero);
        var meta = new LogLineMetadata(giftedAt, giftedAt, IsReplay: false);

        using var fixture = NewFixture("GiftRoute");
        await fixture.StartAsync();

        fixture.Inventory.Add(7001, "Moonstone");
        fixture.Bus.Publish(new ArdaGiftAccepted(42, "NPC_Sanja", 7001, 30.0, meta));

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
    public async Task Sink_layer_dedup_collapses_replay_on_relaunch()
    {
        var giftedAt = new DateTimeOffset(2026, 5, 19, 10, 30, 00, TimeSpan.Zero);
        var meta = new LogLineMetadata(giftedAt, giftedAt, IsReplay: false);

        using var fixture = NewFixture("DedupRelaunch");
        await fixture.StartAsync();

        fixture.Inventory.Add(7001, "Moonstone");
        fixture.Bus.Publish(new ArdaGiftAccepted(42, "NPC_Sanja", 7001, 30.0, meta));

        fixture.Calibration.Data.Observations.Should().HaveCount(1);

        // Same event, same timestamp — dedup must collapse.
        fixture.Bus.Publish(new ArdaGiftAccepted(42, "NPC_Sanja", 7001, 30.0, meta));

        fixture.Calibration.Data.Observations.Should().HaveCount(1);

        await fixture.StopAsync();
    }

    [Fact]
    public async Task Subscription_attaches_in_constructor_before_StartAsync()
    {
        var giftedAt = new DateTimeOffset(2026, 5, 19, 10, 30, 00, TimeSpan.Zero);
        var meta = new LogLineMetadata(giftedAt, giftedAt, IsReplay: false);

        using var fixture = NewFixture("EagerStartAsync");

        await fixture.Service.StartAsync(CancellationToken.None);

        fixture.Inventory.Add(7001, "Moonstone");
        fixture.Bus.Publish(new ArdaGiftAccepted(42, "NPC_Sanja", 7001, 30.0, meta));

        await fixture.Service.StopAsync(CancellationToken.None);

        fixture.Calibration.Data.Observations.Should().HaveCount(1,
            "the bus subscription processes events while StartAsync is simply activating the hosted service");
    }

    [Fact]
    public async Task Replay_events_produce_same_result_as_live()
    {
        var giftedAt = new DateTimeOffset(2026, 5, 19, 10, 30, 00, TimeSpan.Zero);
        var liveMeta = new LogLineMetadata(giftedAt, giftedAt, IsReplay: false);
        var replayMeta = new LogLineMetadata(giftedAt, giftedAt, IsReplay: true);

        using var passLive = NewFixture("PassLive");
        await passLive.StartAsync();
        passLive.Inventory.Add(7001, "Moonstone");
        passLive.Bus.Publish(new ArdaGiftAccepted(42, "NPC_Sanja", 7001, 30.0, liveMeta));
        await passLive.StopAsync();

        using var passReplay = NewFixture("PassReplay");
        await passReplay.StartAsync();
        passReplay.Inventory.Add(7001, "Moonstone");
        passReplay.Bus.Publish(new ArdaGiftAccepted(42, "NPC_Sanja", 7001, 30.0, replayMeta));
        await passReplay.StopAsync();

        passLive.Calibration.Data.Observations.Should().HaveCount(1);
        passReplay.Calibration.Data.Observations.Should().HaveCount(1);

        var live = passLive.Calibration.Data.Observations[0];
        var replay = passReplay.Calibration.Data.Observations[0];

        replay.NpcKey.Should().Be(live.NpcKey);
        replay.ItemInternalName.Should().Be(live.ItemInternalName);
        replay.FavorDelta.Should().Be(live.FavorDelta);
        replay.InstanceId.Should().Be(live.InstanceId);
        replay.Timestamp.Should().Be(live.Timestamp);
    }

    // ── Helpers ───────────────────────────────────────────────────────

    private FavorIngestionFixture NewFixture(string passName) =>
        new(_arwenDir, passName);

    private sealed class FavorIngestionFixture : IDisposable
    {
        public TestDomainEventBus Bus { get; }
        public FakeInventory Inventory { get; }
        public CalibrationService Calibration { get; }
        public FavorIngestionService Service { get; }

        private readonly SettingsAutoSaver<ArwenSettings> _saver;

        public FavorIngestionFixture(string arwenDir, string passName)
        {
            var refData = BuildRefData();
            var index = new GiftIndex();
            index.Build(refData.Items, refData.Npcs);
            Inventory = new FakeInventory();
            var dataDir = Path.Combine(arwenDir, passName);
            Directory.CreateDirectory(dataDir);
            Calibration = new CalibrationService(refData, index, Inventory, dataDir);

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

            var settings = new ArwenSettings();
            _saver = new SettingsAutoSaver<ArwenSettings>(
                new InMemorySettingsStore(),
                settings);

            Bus = new TestDomainEventBus();
            Service = new FavorIngestionService(
                Bus,
                Calibration,
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
