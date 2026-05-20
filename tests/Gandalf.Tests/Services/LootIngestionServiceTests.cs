using System.IO;
using FluentAssertions;
using Gandalf.Domain;
using Gandalf.Parsing;
using Gandalf.Services;
using Mithril.GameState.Areas;
using Mithril.GameState.Areas.Parsing;
using Mithril.Shared.Character;
using Mithril.Shared.Logging;
using Mithril.Shared.Settings;
using Xunit;

namespace Gandalf.Tests.Services;

/// <summary>
/// Regression tests for the L1 migration of <see cref="LootIngestionService"/>
/// (#550 PR 3 archetype-B). The service subscribes to the L1 driver's
/// <see cref="LocalPlayerLogLine"/> pipe with <see cref="ReplayMode.FromSessionStart"/>
/// + <see cref="DeliveryContext.Inline"/>; the test driver below captures
/// the handler so the test can synthesise envelopes directly. Mirrors the
/// <c>SarumanDiscoveryIngestionServiceTests</c> stub pattern (#563), which
/// itself follows <c>SarumanChatIngestionServiceTests</c> (#557) — that's
/// the established archetype-B test shape.
///
/// <para>Two shapes covered:</para>
/// <list type="bullet">
///   <item><b>Subscription-options shape</b> — assert the three #549
///   disposition knobs (ReplayMode, DeliveryContext, DiagnosticCategory)
///   land verbatim at the L1 driver, with <c>SkipProcessedHighWater</c>
///   explicitly null (Gandalf declines an L1 high-water filter per the
///   #549 row — idempotent upsert at the sink).</item>
///   <item><b>Byte-equivalence under replay + live split</b> — feed N
///   envelopes split as half-replay / half-live to the SAME subscription
///   in ONE drain; assert the merged loot-catalog + derived-progress state
///   matches the all-live reference. Pins the idempotent-upsert claim from
///   the #549 audit row: re-replaying the same Sequence is a no-op.</item>
/// </list>
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
        _dir = Mithril.TestSupport.TestPaths.CreateTempDir("gandalf-loot-l1");
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

    // Real captures (LootBracketTrackerTests sources). The L0.5 router strips
    // [ts] + LocalPlayer: before the driver sees these — we synthesise the
    // bare Data string the LocalPlayer pipe delivers post-classification.
    private const string ChestStart_Eltibule = "ProcessStartInteraction(-147, 5, 0, False, \"EltibuleSecretChest\")";
    private const string ChestAdd_PowerPotion = "ProcessAddItem(PowerPotion2(113863546), -1, True)";
    private const string ChestEnable_Eltibule = "ProcessEnableInteractors([], [-147,])";

    private const string ChestStart_Goblin = "ProcessStartInteraction(-162, 7, 0, False, \"GoblinStaticChest1\")";
    private const string ChestAdd_Apple = "ProcessAddItem(Apple(1), -1, True)";
    private const string ChestEnable_Goblin = "ProcessEnableInteractors([], [-162,])";

    private const string BossKill_Olugax =
        "ProcessScreenText(CombatInfo, \"You earned 12 Combat Wisdom: Killed Olugax the Ever-Pudding\")";
    private const string BossKill_Megaspider =
        "ProcessScreenText(CombatInfo, \"You earned 12 Combat Wisdom: Killed a Mega-Spider\")";

    [Fact]
    public async Task ConfiguresSubscriptionWithExpectedOptions()
    {
        // Asserts the three #549-disposition knobs land verbatim at the L1
        // driver: FromSessionStart + Inline + DiagnosticCategory "Gandalf.Loot"
        // + no SkipProcessedHighWater (Gandalf declines a high-water — per-key
        // StartedAt-equality short-circuit at the sink covers replay).
        using var harness = new Harness(_dir, _charactersDir, _cachePath, _active);
        var driver = new FakeLogStreamDriver();
        var (svc, exec) = await harness.StartServiceAsync(driver);

        var opts = driver.LastOptions!;
        opts.ReplayMode.Should().Be(ReplayMode.FromSessionStart,
            because: "eager module — chest/defeat catalog rebuilds from session-start (#549 row)");
        opts.DeliveryContext.Should().Be(DeliveryContext.Inline,
            because: "handler writes to dictionaries + raises events; no ObservableCollection mutation in the ingestion path");
        opts.DiagnosticCategory.Should().Be("Gandalf.Loot",
            because: "preserves the pre-L1 ThrottledWarn/_diag.Warn bucket so log consumers see no churn");
        opts.SkipProcessedHighWater.Should().BeNull(
            because: "Gandalf declines an L1 high-water — idempotent upsert at the sink (StartedAt-equality short-circuit) covers replay (#549 row)");

        await harness.StopAsync(svc, exec);
    }

    [Fact]
    public async Task LiveBossKill_AutoLearnsDefeatAndStampsRow()
    {
        using var harness = new Harness(_dir, _charactersDir, _cachePath, _active);
        var driver = new FakeLogStreamDriver();
        var (svc, exec) = await harness.StartServiceAsync(driver);

        var killAt = new DateTimeOffset(2026, 5, 20, 12, 0, 0, TimeSpan.Zero);
        await driver.Deliver(MakeEnvelope(BossKill_Olugax, killAt, sequence: 100, isReplay: false));

        harness.Source.Catalog.Should().Contain(c => c.Key == LootSource.DefeatKey("Olugax the Ever-Pudding"));
        harness.Source.Progress.Should().ContainKey(LootSource.DefeatKey("Olugax the Ever-Pudding"));
        harness.Source.Progress[LootSource.DefeatKey("Olugax the Ever-Pudding")].StartedAt
            .Should().Be(new DateTimeOffset(killAt.UtcDateTime, TimeSpan.Zero));

        await harness.StopAsync(svc, exec);
    }

    [Fact]
    public async Task LiveChestBracket_AutoLearnsChestAndStampsRow()
    {
        using var harness = new Harness(_dir, _charactersDir, _cachePath, _active);
        var driver = new FakeLogStreamDriver();
        var (svc, exec) = await harness.StartServiceAsync(driver);

        var at = new DateTimeOffset(2026, 5, 20, 12, 0, 0, TimeSpan.Zero);
        await driver.Deliver(MakeEnvelope(ChestStart_Eltibule, at, sequence: 200, isReplay: false));
        await driver.Deliver(MakeEnvelope(ChestAdd_PowerPotion, at.AddMilliseconds(10), sequence: 201, isReplay: false));
        await driver.Deliver(MakeEnvelope(ChestEnable_Eltibule, at.AddMilliseconds(20), sequence: 202, isReplay: false));

        var key = LootSource.ChestKey("EltibuleSecretChest");
        harness.Source.Catalog.Should().Contain(c => c.Key == key,
            because: "the AddItem inside the bracket commits the chest as loot (LootBracketTracker discrimination)");
        harness.Source.Progress.Should().ContainKey(key);
        harness.Source.Progress[key].StartedAt
            .Should().Be(new DateTimeOffset(at.UtcDateTime, TimeSpan.Zero));

        await harness.StopAsync(svc, exec);
    }

    /// <summary>
    /// Byte-equivalence regression: feed N envelopes split as
    /// REPLAY-half + LIVE-half through one subscription; assert the merged
    /// state matches an all-live reference run. Pins the #549 audit row's
    /// idempotent-upsert claim — re-replaying the same Sequence into the
    /// per-key <c>(chest|defeat):*</c> entries is a no-op at the LootSource
    /// sink (the <c>StartedAt</c> equality short-circuit at
    /// <c>OnChestInteraction:160</c> / <c>OnBossKillCredit:341</c>).
    /// </summary>
    [Fact]
    public async Task ReplayThenLive_MatchesAllLiveReference()
    {
        var t0 = new DateTimeOffset(2026, 5, 20, 12, 0, 0, TimeSpan.Zero);

        // Mixed envelope script: two boss kills + two chest brackets,
        // interleaved. Sequences strictly increasing.
        var script = new (string data, DateTimeOffset at, long seq)[]
        {
            (BossKill_Olugax,         t0,                              100),
            (ChestStart_Eltibule,     t0.AddSeconds(1),                101),
            (ChestAdd_PowerPotion,    t0.AddSeconds(1).AddMilliseconds(10), 102),
            (ChestEnable_Eltibule,    t0.AddSeconds(1).AddMilliseconds(20), 103),
            (BossKill_Megaspider,     t0.AddSeconds(2),                104),
            (ChestStart_Goblin,       t0.AddSeconds(3),                105),
            (ChestAdd_Apple,          t0.AddSeconds(3).AddMilliseconds(10), 106),
            (ChestEnable_Goblin,      t0.AddSeconds(3).AddMilliseconds(20), 107),
        };

        // === Reference: all-live ===
        var (refCatalog, refProgress) = await RunOnceAndSnapshot(
            script.Select(s => (s.data, s.at, s.seq, isReplay: false)).ToArray());

        // === Subject: half replay + half live, SAME order, SAME subscription ===
        var mid = script.Length / 2;
        var split = script
            .Select((s, i) => (s.data, s.at, s.seq, isReplay: i < mid))
            .ToArray();
        var (splitCatalog, splitProgress) = await RunOnceAndSnapshot(split);

        // The replay/live split is invisible to LootSource — same per-key
        // entries, same StartedAt anchors, same Duration. The Catalog order
        // is deterministic from the cache iteration order; assert as sets
        // keyed by Key + StartedAt for stability.
        splitCatalog.Should().BeEquivalentTo(refCatalog,
            because: "replay-half + live-half + ONE subscription must yield the same catalog as all-live (idempotent upsert per #549 row)");
        splitProgress.Should().BeEquivalentTo(refProgress,
            because: "replay-half + live-half + ONE subscription must yield the same per-key progress as all-live");
    }

    private async Task<(IReadOnlyDictionary<string, (TimeSpan Duration, bool Verified)> catalog,
                       IReadOnlyDictionary<string, DateTimeOffset> progress)>
        RunOnceAndSnapshot((string data, DateTimeOffset at, long seq, bool isReplay)[] envelopes)
    {
        // Each run uses an ISOLATED on-disk dir so the two runs don't
        // contaminate each other through the loot-catalog.json cache.
        var subDir = Path.Combine(_dir, $"run-{Guid.NewGuid():N}");
        var charsDir = Path.Combine(subDir, "characters");
        var cachePath = Path.Combine(subDir, "loot-catalog.json");
        Directory.CreateDirectory(charsDir);

        using var harness = new Harness(subDir, charsDir, cachePath, _active);
        var driver = new FakeLogStreamDriver();
        var (svc, exec) = await harness.StartServiceAsync(driver);

        foreach (var e in envelopes)
        {
            await driver.Deliver(MakeEnvelope(e.data, e.at, e.seq, e.isReplay));
        }

        // Snapshot catalog as (key → (duration, verified)) and progress as
        // (key → StartedAt) — those are the dimensions LootIngestionService
        // is responsible for. The Catalog object holds references to the
        // shared cache so we materialise into a stable comparison shape
        // before the harness teardown.
        var catalog = harness.Source.Catalog.ToDictionary(
            c => c.Key,
            c => (c.Duration, ((LootCatalogPayload)c.SourceMetadata!).IsDurationVerified));
        var progress = harness.Source.Progress.ToDictionary(
            kv => kv.Key,
            kv => kv.Value.StartedAt);

        await harness.StopAsync(svc, exec);
        return (catalog, progress);
    }

    // === Helpers ===

    private static LogEnvelope<LocalPlayerLogLine> MakeEnvelope(
        string data, DateTimeOffset at, long sequence, bool isReplay) =>
        new(new LocalPlayerLogLine(
                Timestamp: at,
                Data: data,
                Sequence: sequence,
                ReadMonotonicTicks: 0),
            IsReplay: isReplay);

    private static async Task WaitUntilAsync(Func<bool> predicate, TimeSpan? timeout = null)
    {
        var budget = timeout ?? TimeSpan.FromSeconds(5);
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (!predicate())
        {
            if (sw.Elapsed > budget) throw new TimeoutException("WaitUntilAsync gave up");
            await Task.Delay(10);
        }
    }

    /// <summary>
    /// Wires up the full Gandalf ingestion stack so the test exercises the
    /// real parsers + bracket tracker + LootSource against synthesised
    /// envelopes. Mirrors <see cref="LootBracketTrackerTests"/>'s Build()
    /// but with the L1 driver shim instead of a direct
    /// <see cref="LootBracketTracker.Observe(string, DateTime)"/> drive.
    /// </summary>
    private sealed class Harness : IDisposable
    {
        public LootSource Source { get; }
        public DerivedTimerProgressService Derived { get; }
        public LootBracketTracker Bracket { get; }
        public PlayerAreaTracker AreaTracker { get; }
        public BossKillCreditParser BossKill { get; } = new();
        public DefeatCooldownParser DefeatCooldown { get; } = new();

        public Harness(string dir, string charsDir, string cachePath, FakeActiveCharacterService active)
        {
            // The ingestion path doesn't read wall-clock time — chests/defeats
            // are anchored on the envelope Timestamp passed by the test
            // script. TimeProvider.System is fine here; LootSource's
            // duration-elapsed check is the only consumer and the
            // 2026-05-20 12:00 anchor + 3-h placeholder keeps every test
            // envelope inside the firing window relative to system time.
            // (No FireReady assertions in this test file — Catalog +
            // Progress are what we pin.)
            var time = TimeProvider.System;

            var derivedStore = new PerCharacterStore<DerivedProgress>(charsDir, "gandalf-derived.json",
                DerivedProgressJsonContext.Default.DerivedProgress);
            var derivedView = new PerCharacterView<DerivedProgress>(active, derivedStore);
            Derived = new DerivedTimerProgressService(derivedView, time);

            var cacheStore = new JsonSettingsStore<LootCatalogCache>(cachePath,
                LootCatalogCacheJsonContext.Default.LootCatalogCache);
            var cache = cacheStore.Load();
            AreaTracker = new PlayerAreaTracker(new AreaTransitionParser());
            Source = new LootSource(Derived, cacheStore, cache,
                areaTracker: AreaTracker, refData: null, time: time);
            Bracket = new LootBracketTracker(
                Source,
                new ChestInteractionParser(),
                new ChestRejectionParser(),
                new MilkingRejectionParser(),
                new InteractionEndParser(),
                new InteractionDelayLoopParser(),
                new InteractionWaitParser());
        }

        public async Task<(LootIngestionService svc, Task exec)> StartServiceAsync(FakeLogStreamDriver driver)
        {
            var svc = new LootIngestionService(driver, Bracket, BossKill, DefeatCooldown,
                AreaTracker, Source);
            var exec = svc.StartAsync(CancellationToken.None);
            // Gandalf is eager — no module gate to open. Subscription happens
            // synchronously inside ExecuteAsync; wait for it to land.
            await WaitUntilAsync(() => driver.HasSubscription);
            return (svc, exec);
        }

        public async Task StopAsync(LootIngestionService svc, Task exec)
        {
            await svc.StopAsync(CancellationToken.None);
            await exec;
        }

        public void Dispose()
        {
            Source.Dispose();
            Derived.Dispose();
        }
    }

    /// <summary>
    /// Minimal in-process <see cref="ILogStreamDriver"/> that captures the
    /// subscription callback so the test can synthesise envelopes directly.
    /// Mirrors the test stubs in <c>SarumanChatIngestionServiceTests</c> (#557)
    /// and <c>SarumanDiscoveryIngestionServiceTests</c> (#563) — scoped to one
    /// subscription because <see cref="LootIngestionService"/> only
    /// subscribes once.
    /// </summary>
    private sealed class FakeLogStreamDriver : ILogStreamDriver
    {
        private Func<LogEnvelope<LocalPlayerLogLine>, ValueTask>? _handler;

        public LogSubscriptionOptions? LastOptions { get; private set; }
        public bool HasSubscription => _handler is not null;

        public ILogSubscription Subscribe<T>(
            Func<LogEnvelope<T>, ValueTask> handler,
            LogSubscriptionOptions? options = null) where T : class
        {
            if (typeof(T) != typeof(LocalPlayerLogLine))
                throw new InvalidOperationException(
                    $"LootIngestionService must subscribe with T=LocalPlayerLogLine, got {typeof(T).Name}");
            LastOptions = options ?? LogSubscriptionOptions.Default;
            _handler = (Func<LogEnvelope<LocalPlayerLogLine>, ValueTask>)(object)handler;
            return new FakeSub(() => _handler = null);
        }

        public ValueTask Deliver(LogEnvelope<LocalPlayerLogLine> envelope)
        {
            var h = _handler ?? throw new InvalidOperationException("No active subscription");
            return h(envelope);
        }

        private sealed class FakeSub : ILogSubscription
        {
            private readonly Action _onDispose;
            public FakeSub(Action onDispose) { _onDispose = onDispose; }
            public string Id => "fake#loot";
            public LogSubscriptionDiagnostics Diagnostics => new(0, 0, 0, 0, 0, LogSubscriptionState.Healthy);
            public event EventHandler? StateChanged { add { } remove { } }
            public void Dispose() => _onDispose();
        }
    }
}
