using System.Runtime.CompilerServices;
using System.Threading.Channels;
using FluentAssertions;
using Mithril.GameState.Sessions;
using Mithril.GameState.Skills;
using Mithril.Reference.Models.Items;
using Mithril.Reference.Models.Recipes;
using Mithril.Shared.Character;
using Mithril.Shared.Logging;
using Mithril.Shared.Modules;
using Mithril.Shared.Reference;
using Mithril.GameReports;
using Smaug.Domain;
using Smaug.Parsing;
using Smaug.State;
using Xunit;

namespace Smaug.Tests;

/// <summary>
/// #580 — verifies <see cref="VendorIngestionService"/> sources its
/// CivicPride effective level from the shared
/// <see cref="IPlayerSkillState"/> rather than from re-parsing
/// <c>ProcessLoadSkills</c> / <c>ProcessUpdateSkill</c>. Class A migration
/// from the post-#578 consumer audit (#579).
///
/// <para>Covers the four spec test points:</para>
/// <list type="number">
///   <item>Snapshot carrying <c>CivicPride</c> with <c>Level + BonusLevels</c>
///   is folded into <see cref="VendorSellContext.CivicPrideLevel"/>.</item>
///   <item>Snapshot missing the <c>CivicPride</c> key does <b>not</b> reset
///   the previously-set level — absent ≠ unlearned, it's "not yet observed."
///   Load-bearing for the (future) granular-changes channel and for any
///   warm-up window where a snapshot arrives partial.</item>
///   <item>Replay-then-live ordering: a level set during snapshot replay is
///   visible to the first vendor envelope. Concretely, a sell observed
///   via the vendor pipe immediately after gate open + replayed snapshot
///   records with the replayed <c>CivicPrideLevel</c>.</item>
///   <item>The CivicPride-less <see cref="VendorLogParser"/> still delivers
///   the surviving event surface end-to-end (covered by
///   <see cref="VendorIngestionServiceL1Tests"/> — this file does not
///   re-cover the byte-equivalence / Marshaled paths).</item>
/// </list>
/// </summary>
public sealed class VendorIngestionServiceSkillStateTests
{
    private const string StartInteractionLine =
        "[22:27:38] LocalPlayer: ProcessStartInteraction(14564, 0, 3926, True, \"NPC_Therese\")";
    private const string ScreenLine =
        "[22:27:38] LocalPlayer: ProcessVendorScreen(14564, Neutral, 3926, 1776704476729, 4000, \"\", VendorInfo[], VendorInfo[], VendorInfo[], VendorPurchaseCap[], [-1201,-1601,], System.String[], -1601)";
    private const string AddItemLine =
        "[22:27:48] LocalPlayer: ProcessVendorAddItem(130, BottleOfWater(78177652), False)";

    // ============================================================
    // Call 1 / principle eager-always (#695) — Smaug is a Lazy module and
    // pre-Call-1 its L1 subscription sat behind `gates.For("smaug").WaitAsync`
    // inside ExecuteAsync. After Call 1 the subscription attaches in
    // StartAsync; the smaug ModuleGate no longer participates in state
    // subscription. The TestHarness.StartAsync helper no longer touches a
    // ModuleGates instance — every test in this file structurally exercises
    // the eager-attach contract by construction. This explicit assertion
    // makes the contract visible.
    [Fact]
    public async Task L1_subscription_attaches_in_StartAsync_without_gate()
    {
        // The harness's StartAsync invokes service.StartAsync but never
        // creates a ModuleGates / Open() — Call 1's eager-attach contract.
        await using var harness = await TestHarness.StartAsync();

        // Push a skill snapshot so the OnSkillSnapshot handler has observable
        // effect — proves the subscription is live without any tab activation.
        harness.SkillState.Push(SnapshotWithCivicPride(level: 7, bonus: 0));

        harness.Context.CivicPrideLevel.Should().Be(7,
            "the IPlayerSkillState subscription was attached during StartAsync, before any module gate could be opened");
    }

    // ============================================================
    // Test 1 — snapshot with CivicPride → context updated to raw+bonus.
    // ============================================================

    [Fact]
    public async Task SnapshotWithCivicPride_SetsEffectiveLevel()
    {
        await using var harness = await TestHarness.StartAsync();

        harness.SkillState.Push(SnapshotWithCivicPride(level: 15, bonus: 5));

        harness.Context.CivicPrideLevel.Should().Be(20,
            because: "OnSkillSnapshot must fold Level + BonusLevels into VendorSellContext.CivicPrideLevel");
    }

    // ============================================================
    // Test 2 — snapshot missing CivicPride does NOT reset prior value.
    // ============================================================

    [Fact]
    public async Task SnapshotWithoutCivicPride_PreservesPriorLevel()
    {
        await using var harness = await TestHarness.StartAsync();

        // First snapshot establishes the level.
        harness.SkillState.Push(SnapshotWithCivicPride(level: 12, bonus: 3));
        harness.Context.CivicPrideLevel.Should().Be(15);

        // Second snapshot — same author (skill tracker), but without the
        // CivicPride key (e.g. a deliberately-partial state during the
        // ProcessUpdateSkill warm-up window before the first
        // ProcessLoadSkills, or a future granular-changes channel that
        // omits unchanged skills). The level must stay put.
        harness.SkillState.Push(SnapshotWithoutCivicPride());

        harness.Context.CivicPrideLevel.Should().Be(15,
            because: "absence of the CivicPride key in a snapshot means 'not yet observed', not 'skill is unlearned' — the prior value is preserved");
    }

    // ============================================================
    // Test 3 — replay-then-live ordering: the replayed snapshot is
    // applied before the first vendor envelope reaches the handler.
    // ============================================================

    [Fact]
    public async Task ReplayedSnapshot_IsVisibleToFirstVendorSale()
    {
        // Seed the fake skill state BEFORE the service subscribes; that's
        // what IPlayerSkillState.Subscribe's atomic replay+attach
        // contract is designed to make visible to a late subscriber.
        var initial = SnapshotWithCivicPride(level: 27, bonus: 8); // effective = 35

        await using var harness = await TestHarness.StartAsync(initialSkillSnapshot: initial);

        // The service was started AFTER seeding; its Subscribe call should
        // have replayed the snapshot synchronously, populating
        // VendorSellContext.CivicPrideLevel before any vendor envelope is
        // dispatched.
        harness.Context.CivicPrideLevel.Should().Be(35);

        // Now feed the full vendor lead-up + sale; the recorded
        // observation must carry the replayed CivicPride level.
        harness.Driver.PushLive(MakeLocal(StartInteractionLine));
        harness.Driver.PushLive(MakeLocal(ScreenLine));
        harness.Driver.PushLive(MakeLocal(AddItemLine));

        await harness.Driver.DrainAsync();

        harness.Calibration.Data.Observations.Should().HaveCount(1);
        var obs = harness.Calibration.Data.Observations[0];
        obs.CivicPrideLevel.Should().Be(35,
            because: "the replayed CivicPride value must be picked up before the first vendor sale records — that is the IPlayerSkillState.Subscribe contract");
        obs.NpcKey.Should().Be("NPC_Therese");
        obs.PricePaid.Should().Be(130);
    }

    // ============================================================
    // Test 4 — live snapshot AFTER subscribe still updates the context.
    // ============================================================

    [Fact]
    public async Task LiveSnapshotAfterSubscribe_UpdatesContext()
    {
        await using var harness = await TestHarness.StartAsync();

        harness.SkillState.Push(SnapshotWithCivicPride(level: 5, bonus: 0));
        harness.Context.CivicPrideLevel.Should().Be(5);

        // Simulate a ProcessUpdateSkill landing later in the session.
        harness.SkillState.Push(SnapshotWithCivicPride(level: 6, bonus: 0));
        harness.Context.CivicPrideLevel.Should().Be(6,
            because: "subsequent snapshots delivered live by the tracker must continue to update the context");
    }

    // ============================================================
    // Helpers
    // ============================================================

    private static PlayerSkillSnapshot SnapshotWithCivicPride(int level, int bonus)
    {
        var skills = new Dictionary<string, SkillProgressSnapshot>(StringComparer.Ordinal)
        {
            ["CivicPride"] = new SkillProgressSnapshot(
                Level: level,
                BonusLevels: bonus,
                XpTowardNextLevel: 0,
                XpNeededForNextLevel: 0,
                MaxLevel: 50),
        };
        return new PlayerSkillSnapshot(skills, DateTime.UtcNow, SkillStateSource.LiveLog);
    }

    private static PlayerSkillSnapshot SnapshotWithoutCivicPride()
    {
        // A snapshot carrying some other skill but not CivicPride.
        var skills = new Dictionary<string, SkillProgressSnapshot>(StringComparer.Ordinal)
        {
            ["Toolcrafting"] = new SkillProgressSnapshot(
                Level: 15, BonusLevels: 0,
                XpTowardNextLevel: 0, XpNeededForNextLevel: 0, MaxLevel: 50),
        };
        return new PlayerSkillSnapshot(skills, DateTime.UtcNow, SkillStateSource.LiveLog);
    }

    private static LocalPlayerLogLine MakeLocal(string fullLine, long sequence = 0)
    {
        // Same envelope-stripping the L0.5 pipeline does, mirrored from
        // VendorIngestionServiceL1Tests.MakeLocal.
        const string actorToken = "LocalPlayer: ";
        var idx = 0;
        if (fullLine.Length > 11
            && fullLine[0] == '['
            && fullLine[3] == ':'
            && fullLine[6] == ':'
            && fullLine[9] == ']')
        {
            idx = 11;
        }
        if (idx + actorToken.Length <= fullLine.Length
            && fullLine.IndexOf(actorToken, idx, StringComparison.Ordinal) == idx)
        {
            idx += actorToken.Length;
        }
        var data = idx == 0 ? fullLine : fullLine.Substring(idx);
        return new LocalPlayerLogLine(
            new DateTimeOffset(2026, 5, 19, 22, 27, 48, TimeSpan.Zero),
            data,
            Sequence: sequence,
            ReadMonotonicTicks: 0);
    }

    // ============================================================
    // Test harness — minimal scaffold to start VendorIngestionService
    // against a fake IPlayerSkillState + an in-memory L1 driver.
    // ============================================================

    private sealed class TestHarness : IAsyncDisposable
    {
        public VendorIngestionService Service { get; }
        public TestDriver Driver { get; }
        public FakeSkillState SkillState { get; }
        public VendorSellContext Context { get; }
        public PriceCalibrationService Calibration { get; }
        private readonly string _tempDir;
        private readonly CancellationTokenSource _cts = new();

        private TestHarness(
            VendorIngestionService service,
            TestDriver driver,
            FakeSkillState skillState,
            VendorSellContext context,
            PriceCalibrationService calibration,
            string tempDir)
        {
            Service = service;
            Driver = driver;
            SkillState = skillState;
            Context = context;
            Calibration = calibration;
            _tempDir = tempDir;
        }

        public static async Task<TestHarness> StartAsync(
            PlayerSkillSnapshot? initialSkillSnapshot = null)
        {
            var tempDir = Mithril.TestSupport.TestPaths.CreateTempDir("smaug_580_test");
            var refData = new FakeRefData();
            var session = new FakeSession("char|2026-05-19T20:00:00Z");
            var calibration = new PriceCalibrationService(refData, tempDir, session: session);
            var parser = new VendorLogParser();
            var context = new VendorSellContext();
            var activeChar = new TestActiveCharacterService();
            var driver = new TestDriver();
            var skillState = new FakeSkillState(initialSkillSnapshot ?? PlayerSkillSnapshot.Empty);

            // Post-Call-1 (#695): VendorIngestionService no longer takes a
            // ModuleGates parameter — its L1 subscription attaches eagerly
            // in StartAsync, not behind the smaug gate.
            var service = new VendorIngestionService(
                driver, parser, calibration, context, activeChar, skillState);

            // Kick the service via the hosted-service contract. StartAsync
            // attaches the L1 subscription and the skill subscription
            // before returning, then schedules ExecuteAsync's park loop.
            await service.StartAsync(CancellationToken.None);

            // Give the service a moment to land on its Subscribe calls.
            await WaitUntilAsync(() => skillState.HandlerAttached, TimeSpan.FromSeconds(2));

            return new TestHarness(service, driver, skillState, context, calibration, tempDir);
        }

        public async ValueTask DisposeAsync()
        {
            try { _cts.Cancel(); } catch { }
            try { await Service.StopAsync(CancellationToken.None).ConfigureAwait(false); } catch { }
            try { Service.Dispose(); } catch { }
            try { _cts.Dispose(); } catch { }
            try { System.IO.Directory.Delete(_tempDir, recursive: true); } catch { /* best-effort */ }
        }

        private static async Task WaitUntilAsync(Func<bool> predicate, TimeSpan timeout)
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            while (!predicate())
            {
                if (sw.Elapsed > timeout) throw new TimeoutException("WaitUntilAsync gave up");
                await Task.Delay(10);
            }
        }
    }

    // ============================================================
    // Test stubs
    // ============================================================

    /// <summary>
    /// In-memory <see cref="IPlayerSkillState"/> that captures the
    /// subscribed handler and lets the test drive snapshots through it.
    /// Mirrors the production tracker's atomic replay+attach contract on
    /// the snapshot channel (Subscribe delivers Current synchronously
    /// before returning).
    /// </summary>
    private sealed class FakeSkillState : IPlayerSkillState
    {
        private readonly object _lock = new();
        private readonly List<Action<PlayerSkillSnapshot>> _handlers = new();
        private PlayerSkillSnapshot _current;

        public FakeSkillState(PlayerSkillSnapshot initial) { _current = initial; }

        public PlayerSkillSnapshot Current
        {
            get { lock (_lock) return _current; }
        }

        public bool HandlerAttached
        {
            get { lock (_lock) return _handlers.Count > 0; }
        }

        public IDisposable Subscribe(Action<PlayerSkillSnapshot> handler)
        {
            lock (_lock)
            {
                handler(_current); // atomic replay under the lock
                _handlers.Add(handler);
                return new Unsub(this, handler);
            }
        }

        public IDisposable SubscribeChanges(Action<SkillChange> handler)
        {
            // Not exercised by this consumer (Smaug uses the snapshot
            // channel). Return a no-op disposable.
            return new NoopDisposable();
        }

        /// <summary>
        /// Test-side push: updates <see cref="Current"/> and fires every
        /// subscribed handler synchronously under the lock, mirroring the
        /// production service's live-dispatch shape.
        /// </summary>
        public void Push(PlayerSkillSnapshot snapshot)
        {
            lock (_lock)
            {
                _current = snapshot;
                foreach (var h in _handlers) h(snapshot);
            }
        }

        private sealed class Unsub : IDisposable
        {
            private FakeSkillState? _owner;
            private readonly Action<PlayerSkillSnapshot> _h;
            public Unsub(FakeSkillState o, Action<PlayerSkillSnapshot> h) { _owner = o; _h = h; }
            public void Dispose()
            {
                var o = Interlocked.Exchange(ref _owner, null);
                if (o is null) return;
                lock (o._lock) { o._handlers.Remove(_h); }
            }
        }

        private sealed class NoopDisposable : IDisposable { public void Dispose() { } }
    }

    /// <summary>
    /// In-memory L1 driver shaped after the production driver — only the
    /// pieces VendorIngestionService exercises. Same Channel-backed live
    /// queue pattern as VendorIngestionServiceL1Tests.TestDriver, kept
    /// minimal here.
    /// </summary>
    private sealed class TestDriver : ILogStreamDriver
    {
        private readonly Channel<LocalPlayerLogLine> _live = Channel.CreateUnbounded<LocalPlayerLogLine>();
        private long _pending;
        private TaskCompletionSource _drained = NewDrainedTcs();

        public void PushLive(LocalPlayerLogLine line)
        {
            Interlocked.Increment(ref _pending);
            Interlocked.Exchange(ref _drained, NewDrainTcs());
            _live.Writer.TryWrite(line);
        }

        public Task DrainAsync(TimeSpan? timeout = null) =>
            _drained.Task.WaitAsync(timeout ?? TimeSpan.FromSeconds(5));

        public ILogSubscription Subscribe<T>(
            Func<LogEnvelope<T>, ValueTask> handler,
            LogSubscriptionOptions? options = null) where T : class
        {
            if (typeof(T) != typeof(LocalPlayerLogLine))
                throw new ArgumentException(
                    "TestDriver only supports LocalPlayerLogLine subscriptions.",
                    nameof(T));

            var typedHandler = (Func<LogEnvelope<LocalPlayerLogLine>, ValueTask>)(object)handler;
            var sub = new Sub(this, typedHandler);
            sub.Start();
            return sub;
        }

        private async IAsyncEnumerable<LogEnvelope<LocalPlayerLogLine>> SubscribeAsync(
            [EnumeratorCancellation] CancellationToken ct)
        {
            await foreach (var line in _live.Reader.ReadAllAsync(ct).ConfigureAwait(false))
                yield return new LogEnvelope<LocalPlayerLogLine>(line, IsReplay: false);
        }

        private void RecordDelivered()
        {
            if (Interlocked.Decrement(ref _pending) == 0)
                _drained.TrySetResult();
        }

        private static TaskCompletionSource NewDrainTcs() =>
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        private static TaskCompletionSource NewDrainedTcs()
        {
            var tcs = NewDrainTcs();
            tcs.TrySetResult();
            return tcs;
        }

        private sealed class Sub : ILogSubscription
        {
            private readonly TestDriver _driver;
            private readonly Func<LogEnvelope<LocalPlayerLogLine>, ValueTask> _handler;
            private readonly CancellationTokenSource _cts = new();
            private Task? _pumpTask;
            private int _disposed;

            public Sub(TestDriver d, Func<LogEnvelope<LocalPlayerLogLine>, ValueTask> h)
            {
                _driver = d;
                _handler = h;
            }

            public string Id { get; } = $"test#{Guid.NewGuid():N}";
            public LogSubscriptionDiagnostics Diagnostics =>
                new(0, 0, 0, 0, 0, LogSubscriptionState.Healthy);
            public event EventHandler? StateChanged { add { } remove { } }

            public void Start() => _pumpTask = Task.Run(PumpAsync);

            private async Task PumpAsync()
            {
                var ct = _cts.Token;
                try
                {
                    await foreach (var env in _driver.SubscribeAsync(ct).ConfigureAwait(false))
                    {
                        if (ct.IsCancellationRequested) break;
                        try { await _handler(env).ConfigureAwait(false); }
                        catch { /* mirror driver containment */ }
                        finally { _driver.RecordDelivered(); }
                    }
                }
                catch (OperationCanceledException) { /* expected on dispose */ }
            }

            public void Dispose()
            {
                if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
                try { _cts.Cancel(); } catch { }
                try { _pumpTask?.Wait(TimeSpan.FromSeconds(2)); } catch { }
                try { _cts.Dispose(); } catch { }
            }
        }
    }

    private sealed class FakeSession : IGameSessionService
    {
        public GameSession? Current { get; private set; }
        public event EventHandler<GameSession>? SessionStarted;
        public FakeSession(string sessionId)
        {
            Current = new GameSession(sessionId, "char",
                new DateTime(2026, 5, 19, 20, 0, 0, DateTimeKind.Utc), TimeSpan.Zero);
        }
        public IDisposable Subscribe(Action<GameSession> handler)
        {
            if (Current is not null) handler(Current);
            return new Sub();
        }
        private sealed class Sub : IDisposable { public void Dispose() { } }
        private void RaiseUnused() => SessionStarted?.Invoke(this, Current!);
    }

    private sealed class FakeRefData : IReferenceDataService
    {
        public IReadOnlyList<string> Keys { get; } = ["items"];
        public IReadOnlyDictionary<long, Item> Items { get; }
        public IReadOnlyDictionary<string, Item> ItemsByInternalName { get; }
        public ItemKeywordIndex KeywordIndex => new(new Dictionary<long, Item>());
        public IReadOnlyDictionary<string, Recipe> Recipes { get; } = new Dictionary<string, Recipe>();
        public IReadOnlyDictionary<string, Recipe> RecipesByInternalName { get; } = new Dictionary<string, Recipe>();
        public IReadOnlyDictionary<string, SkillEntry> Skills { get; } = new Dictionary<string, SkillEntry>();
        public IReadOnlyDictionary<string, XpTableEntry> XpTables { get; } = new Dictionary<string, XpTableEntry>();
        public IReadOnlyDictionary<string, NpcEntry> Npcs { get; }
        public IReadOnlyDictionary<string, AreaEntry> Areas { get; } = new Dictionary<string, AreaEntry>();
        public IReadOnlyDictionary<string, IReadOnlyList<ItemSource>> ItemSources { get; } = new Dictionary<string, IReadOnlyList<ItemSource>>();
        public IReadOnlyDictionary<string, AttributeEntry> Attributes { get; } = new Dictionary<string, AttributeEntry>();
        public IReadOnlyDictionary<string, PowerEntry> Powers { get; } = new Dictionary<string, PowerEntry>();
        public IReadOnlyDictionary<string, IReadOnlyList<string>> Profiles { get; } = new Dictionary<string, IReadOnlyList<string>>();
        public IReadOnlyDictionary<string, Mithril.Reference.Models.Quests.Quest> Quests { get; } = new Dictionary<string, Mithril.Reference.Models.Quests.Quest>();
        public IReadOnlyDictionary<string, Mithril.Reference.Models.Quests.Quest> QuestsByInternalName { get; } = new Dictionary<string, Mithril.Reference.Models.Quests.Quest>();
        public IReadOnlyDictionary<string, string> Strings { get; } = new Dictionary<string, string>(StringComparer.Ordinal);

        public FakeRefData()
        {
            var water = new Item
            {
                Id = 1,
                Name = "Bottle of Water",
                InternalName = "BottleOfWater",
                MaxStackSize = 10,
                IconId = 0,
                Keywords = [new ItemKeyword("Drink", 0)],
                Value = 11m,
            };
            Items = new Dictionary<long, Item> { [1] = water };
            ItemsByInternalName = new Dictionary<string, Item>(StringComparer.Ordinal)
            {
                ["BottleOfWater"] = water,
            };
            Npcs = new Dictionary<string, NpcEntry>(StringComparer.Ordinal)
            {
                ["NPC_Therese"] = new NpcEntry(
                    Key: "NPC_Therese",
                    Name: "Therese",
                    Area: "",
                    Preferences: Array.Empty<NpcPreference>(),
                    ItemGiftTiers: Array.Empty<string>(),
                    Services: Array.Empty<NpcService>()),
            };
        }

        public ReferenceFileSnapshot GetSnapshot(string key) =>
            new("items", ReferenceFileSource.Bundled, "test", null, 1);
        public Task RefreshAsync(string key, CancellationToken ct = default) => Task.CompletedTask;
        public Task RefreshAllAsync(CancellationToken ct = default) => Task.CompletedTask;
        public void BeginBackgroundRefresh() { }
        public event EventHandler<string>? FileUpdated { add { } remove { } }
    }

    private sealed class TestActiveCharacterService : IActiveCharacterService
    {
        public IReadOnlyList<CharacterSnapshot> Characters { get; set; } = [];
        public IReadOnlyList<ReportFileInfo> StorageReports { get; set; } = [];
        public string? ActiveCharacterName { get; private set; }
        public string? ActiveServer { get; private set; }
        public CharacterSnapshot? ActiveCharacter { get; set; }
        public ReportFileInfo? ActiveStorageReport { get; set; }
        public StorageReport? ActiveStorageContents { get; set; }
        public void SetActiveCharacter(string name, string server)
        {
            ActiveCharacterName = name;
            ActiveServer = server;
            ActiveCharacterChanged?.Invoke(this, EventArgs.Empty);
        }
        public void Refresh() { }
        public event EventHandler? ActiveCharacterChanged;
#pragma warning disable CS0067
        public event EventHandler? CharacterExportsChanged;
        public event EventHandler? StorageReportsChanged;
#pragma warning restore CS0067
        public void Dispose() { }
    }
}
