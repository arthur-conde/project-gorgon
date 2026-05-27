using System.IO;
using Arda.Abstractions.Logs;
using Arda.World.Player.Events;
using FluentAssertions;
using Gandalf.Domain;
using Gandalf.Parsing;
using Gandalf.Services;
using Mithril.Shared.Character;
using Mithril.Shared.Settings;
using Xunit;

namespace Gandalf.Tests.Services;

/// <summary>
/// Regression tests for the Arda-native <see cref="LootIngestionService"/>.
/// The service subscribes to domain events via
/// <see cref="Arda.Contracts.IDomainEventSubscriber"/> and routes them into
/// <see cref="LootBracketTracker"/> (chest discrimination) and
/// <see cref="LootSource"/> (boss-kill auto-discovery via retained parsers).
/// </summary>
[Trait("Category", "FileIO")]
[Collection("FileIO")]
public sealed class LootIngestionServiceTests : IDisposable
{
    private readonly string _dir;
    private readonly string _charactersDir;
    private readonly string _cachePath;
    private readonly FakeActiveCharacterService _active;

    public LootIngestionServiceTests()
    {
        _dir = Mithril.TestSupport.TestPaths.CreateTempDir("gandalf-loot-arda");
        _charactersDir = Path.Combine(_dir, "characters");
        _cachePath = Path.Combine(_dir, "loot-catalog.json");
        Directory.CreateDirectory(_charactersDir);
        _active = new FakeActiveCharacterService();
        _active.SetActiveCharacter("Arthur", "Kwatoxi");
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best-effort */ }
    }

    private static LogLineMetadata Meta(DateTimeOffset at, bool isReplay = false) =>
        new(at, DateTimeOffset.UtcNow, isReplay);

    [Fact]
    public async Task LiveBossKill_AutoLearnsDefeatAndStampsRow()
    {
        using var harness = new Harness(_dir, _charactersDir, _cachePath, _active);
        var killAt = new DateTimeOffset(2026, 5, 20, 12, 0, 0, TimeSpan.Zero);

        await harness.StartServiceAsync();

        harness.Bus.Publish(new ScreenTextObserved(
            "CombatInfo".AsMemory(),
            "You earned 12 Combat Wisdom: Killed Olugax the Ever-Pudding".AsMemory(),
            Meta(killAt)));

        harness.Source.Catalog.Should().Contain(c => c.Key == LootSource.DefeatKey("Olugax the Ever-Pudding"));
        harness.Source.Progress.Should().ContainKey(LootSource.DefeatKey("Olugax the Ever-Pudding"));
        harness.Source.Progress[LootSource.DefeatKey("Olugax the Ever-Pudding")].StartedAt
            .Should().Be(new DateTimeOffset(killAt.UtcDateTime, TimeSpan.Zero));

        await harness.StopAsync();
    }

    [Fact]
    public async Task LiveChestBracket_AutoLearnsChestAndStampsRow()
    {
        using var harness = new Harness(_dir, _charactersDir, _cachePath, _active);
        var at = new DateTimeOffset(2026, 5, 20, 12, 0, 0, TimeSpan.Zero);

        await harness.StartServiceAsync();

        harness.Bus.Publish(new InteractionStarted(-147, "EltibuleSecretChest", 5, false, Meta(at)));
        harness.Bus.Publish(new InventoryItemAdded(113863546, "PowerPotion2", Meta(at + TimeSpan.FromMilliseconds(10))));
        harness.Bus.Publish(new EnableInteractorsFrame(-147, Meta(at + TimeSpan.FromMilliseconds(20))));

        var key = LootSource.ChestKey("EltibuleSecretChest");
        harness.Source.Catalog.Should().Contain(c => c.Key == key,
            because: "the AddItem inside the bracket commits the chest as loot");
        harness.Source.Progress.Should().ContainKey(key);
        harness.Source.Progress[key].StartedAt
            .Should().Be(new DateTimeOffset(at.UtcDateTime, TimeSpan.Zero));

        await harness.StopAsync();
    }

    /// <summary>
    /// Feed two chest brackets and two boss kills as replay + live events;
    /// assert the merged state matches an all-live reference.
    /// </summary>
    [Fact]
    public async Task ReplayThenLive_MatchesAllLiveReference()
    {
        var t0 = new DateTimeOffset(2026, 5, 20, 12, 0, 0, TimeSpan.Zero);

        Action<TestDomainEventBus, bool>[] script =
        [
            (bus, isReplay) => bus.Publish(new ScreenTextObserved(
                "CombatInfo".AsMemory(),
                "You earned 12 Combat Wisdom: Killed Olugax the Ever-Pudding".AsMemory(),
                Meta(t0, isReplay))),
            (bus, isReplay) => bus.Publish(new InteractionStarted(-147, "EltibuleSecretChest", 5, false,
                Meta(t0 + TimeSpan.FromSeconds(1), isReplay))),
            (bus, isReplay) => bus.Publish(new InventoryItemAdded(113863546, "PowerPotion2",
                Meta(t0 + TimeSpan.FromSeconds(1) + TimeSpan.FromMilliseconds(10), isReplay))),
            (bus, isReplay) => bus.Publish(new EnableInteractorsFrame(-147,
                Meta(t0 + TimeSpan.FromSeconds(1) + TimeSpan.FromMilliseconds(20), isReplay))),
            (bus, isReplay) => bus.Publish(new ScreenTextObserved(
                "CombatInfo".AsMemory(),
                "You earned 12 Combat Wisdom: Killed a Mega-Spider".AsMemory(),
                Meta(t0 + TimeSpan.FromSeconds(2), isReplay))),
            (bus, isReplay) => bus.Publish(new InteractionStarted(-162, "GoblinStaticChest1", 7, false,
                Meta(t0 + TimeSpan.FromSeconds(3), isReplay))),
            (bus, isReplay) => bus.Publish(new InventoryItemAdded(1, "Apple",
                Meta(t0 + TimeSpan.FromSeconds(3) + TimeSpan.FromMilliseconds(10), isReplay))),
            (bus, isReplay) => bus.Publish(new EnableInteractorsFrame(-162,
                Meta(t0 + TimeSpan.FromSeconds(3) + TimeSpan.FromMilliseconds(20), isReplay))),
        ];

        // === Reference: all-live ===
        var (refCatalog, refProgress) = await RunOnceAndSnapshot(
            script.Select(a => (Action: a, IsReplay: false)).ToArray());

        // === Subject: half replay + half live ===
        var mid = script.Length / 2;
        var split = script
            .Select((a, i) => (Action: a, IsReplay: i < mid))
            .ToArray();
        var (splitCatalog, splitProgress) = await RunOnceAndSnapshot(split);

        splitCatalog.Should().BeEquivalentTo(refCatalog,
            because: "replay + live must yield the same catalog as all-live (idempotent upsert)");
        splitProgress.Should().BeEquivalentTo(refProgress,
            because: "replay + live must yield the same progress as all-live");
    }

    private async Task<(IReadOnlyDictionary<string, (TimeSpan Duration, bool Verified)> catalog,
                       IReadOnlyDictionary<string, DateTimeOffset> progress)>
        RunOnceAndSnapshot((Action<TestDomainEventBus, bool> Action, bool IsReplay)[] events)
    {
        var subDir = Path.Combine(_dir, $"run-{Guid.NewGuid():N}");
        var charsDir = Path.Combine(subDir, "characters");
        var cachePath = Path.Combine(subDir, "loot-catalog.json");
        Directory.CreateDirectory(charsDir);

        using var harness = new Harness(subDir, charsDir, cachePath, _active);
        await harness.StartServiceAsync();

        foreach (var e in events)
            e.Action(harness.Bus, e.IsReplay);

        var catalog = harness.Source.Catalog.ToDictionary(
            c => c.Key,
            c => (c.Duration, ((LootCatalogPayload)c.SourceMetadata!).IsDurationVerified));
        var progress = harness.Source.Progress.ToDictionary(
            kv => kv.Key,
            kv => kv.Value.StartedAt);

        await harness.StopAsync();
        return (catalog, progress);
    }

    /// <summary>
    /// Wires up the full Gandalf ingestion stack against a
    /// <see cref="TestDomainEventBus"/> instead of a real log driver.
    /// </summary>
    private sealed class Harness : IDisposable
    {
        public LootSource Source { get; }
        public TestDomainEventBus Bus { get; } = new();
        public DerivedTimerProgressService Derived { get; }
        public LootBracketTracker Bracket { get; }
        public BossKillCreditParser BossKill { get; } = new();
        public DefeatCooldownParser DefeatCooldown { get; } = new();

        private LootIngestionService? _svc;

        public Harness(string dir, string charsDir, string cachePath, FakeActiveCharacterService active)
        {
            var time = TimeProvider.System;

            var derivedStore = new PerCharacterStore<DerivedProgress>(charsDir, "gandalf-derived.json",
                DerivedProgressJsonContext.Default.DerivedProgress);
            var derivedView = new PerCharacterView<DerivedProgress>(active, derivedStore);
            Derived = new DerivedTimerProgressService(derivedView, time);

            var cacheStore = new JsonSettingsStore<LootCatalogCache>(cachePath,
                LootCatalogCacheJsonContext.Default.LootCatalogCache);
            var cache = cacheStore.Load();
            Source = new LootSource(Derived, cacheStore, cache,
                areaState: null, refData: null, time: time);
            Bracket = new LootBracketTracker(Source);
        }

        public async Task StartServiceAsync()
        {
            _svc = new LootIngestionService(Bus, Bracket, BossKill, DefeatCooldown, Source);
            await _svc.StartAsync(CancellationToken.None);
        }

        public async Task StopAsync()
        {
            if (_svc is null) return;
            await _svc.StopAsync(CancellationToken.None);
        }

        public void Dispose()
        {
            _svc?.Dispose();
            Source.Dispose();
            Derived.Dispose();
        }
    }
}
