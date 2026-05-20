using System.Runtime.CompilerServices;
using System.Threading.Channels;
using FluentAssertions;
using Mithril.Reference.Models.Items;
using Mithril.Reference.Models.Recipes;
using Mithril.Shared.Logging;
using Mithril.Shared.Reference;
using Pippin.Domain;
using Pippin.Parsing;
using Pippin.State;
using Xunit;

namespace Pippin.Tests;

/// <summary>
/// L1 migration (#550 PR 3 archetype-B) regression tests for the
/// <see cref="GourmandIngestionService"/> shape. Three claims pinned:
///
/// <list type="bullet">
///   <item><b>Replay-then-live byte equivalence.</b> Splitting the same
///   <c>FoodsConsumedReport</c>-bearing line across the driver's
///   <c>PushReplay</c> + <c>PushLive</c> boundary yields the same state as
///   pushing it all-live. The flag in
///   <see cref="LogEnvelope{T}.IsReplay"/> must not gate the handler.</item>
///   <item><b>High-water restart stability.</b> A second subscription seeded
///   with <see cref="LogSubscriptionOptions.SkipProcessedHighWater"/> set to
///   the prior session's last-processed <c>Sequence</c> drops every
///   re-emitted envelope before the handler runs — no further state change.
///   This is the canonical Pippin idempotence pattern (#549).</item>
///   <item><b>Parser anchor-drop.</b> The L0.5 <c>LocalPlayer:</c> actor
///   token is stripped upstream; the parser must consume bare
///   <c>ProcessBook(...)</c> shapes. Pinned by
///   <see cref="GourmandLogParser_consumes_bare_ProcessBook_payload"/>.</item>
/// </list>
///
/// <para><b>Scope note.</b> The full ingestion service requires
/// <see cref="Mithril.Shared.Character.PerCharacterView{T}"/> +
/// <see cref="Mithril.Shared.Modules.ModuleGates"/> + WPF dispatcher
/// wiring. These tests drive the state-machine + parser layer using the
/// same handler shape the service composes — the slice under test is the
/// driver-bounded envelope-fan + the parser/state-machine pair, which is
/// where the L1 migration's behavioural changes live. The dispatcher
/// marshalling is the driver's invariant (tested in
/// <c>Mithril.Shared.Tests</c>); we deliberately use
/// <see cref="DeliveryContext.Inline"/> here so the test pump doesn't need
/// a UI dispatcher.</para>
/// </summary>
public class GourmandIngestionL1Tests
{
    private const string ReportLine =
        """[22:04:09] LocalPlayer: ProcessBook("Skill Info", "Foods Consumed:\n\n  Apple Juice: 8\n  Bacon (HAS MEAT): 2\n", "SkillReport", "", "", False, False, False, False, False, "")""";

    // Same body, different displayed Foods Consumed list — used to assert
    // state actually changes when the high-water filter ISN'T set.
    private const string SecondReportLine =
        """[22:05:10] LocalPlayer: ProcessBook("Skill Info", "Foods Consumed:\n\n  Apple Juice: 8\n  Bacon (HAS MEAT): 2\n  Grapes: 3\n", "SkillReport", "", "", False, False, False, False, False, "")""";

    private static FoodCatalog NewCatalog()
    {
        var stub = new StubReferenceDataService(new Dictionary<long, Item>
        {
            [1] = new() { Id = 1, Name = "Apple Juice", InternalName = "FoodAppleJuice", MaxStackSize = 1, IconId = 0, Keywords = [], FoodDesc = "Level 0 Snack" },
            [2] = new() { Id = 2, Name = "Bacon", InternalName = "FoodBacon", MaxStackSize = 1, IconId = 0, Keywords = [], FoodDesc = "Level 0 Snack" },
            [3] = new() { Id = 3, Name = "Grapes", InternalName = "FoodGrapes", MaxStackSize = 1, IconId = 0, Keywords = [], FoodDesc = "Level 0 Snack" },
        });
        return new FoodCatalog(stub);
    }

    /// <summary>
    /// The post-#550 PR #555 anchor-drop: <see cref="GourmandLogParser"/>
    /// must match a payload that has already had the <c>[ts] LocalPlayer:</c>
    /// envelope eaten by L0.5 — i.e. the bare <c>ProcessBook(...)</c> shape.
    /// </summary>
    [Fact]
    public void GourmandLogParser_consumes_bare_ProcessBook_payload()
    {
        var parser = new GourmandLogParser();
        // Strip the envelope manually to mimic L0.5's classification: the
        // production LocalPlayerLogLine.Data is the bare verb-and-args
        // string.
        const string bareData =
            """ProcessBook("Skill Info", "Foods Consumed:\n\n  Apple Juice: 8\n", "SkillReport", "", "", False, False, False, False, False, "")""";

        var result = parser.TryParse(bareData, DateTime.UtcNow);

        result.Should().BeOfType<FoodsConsumedReport>();
        ((FoodsConsumedReport)result!).Foods.Single().Name.Should().Be("Apple Juice");
    }

    /// <summary>
    /// Feed the same report once all-live and once split replay→live; assert
    /// the resulting state-machine snapshot is identical. The handler must
    /// NOT gate on <see cref="LogEnvelope{T}.IsReplay"/>.
    /// </summary>
    [Fact]
    public async Task Replay_then_live_byte_equivalence()
    {
        // Pass 1 — single all-live emission.
        Dictionary<string, int> pass1Eaten;
        long pass1HighWater;
        {
            var catalog = NewCatalog();
            var state = new GourmandStateMachine(catalog);
            using var driver = new TestLogStreamDriver();
            var (sub, highWater) = SubscribeWithHighWaterCapture(driver, state, null);

            driver.PushLive(MakeLine(ReportLine, sequence: 100));
            await driver.DrainLocalPlayerAsync(TimeSpan.FromSeconds(5));

            pass1Eaten = new(state.EatenFoodsByInternalName, StringComparer.Ordinal);
            pass1HighWater = highWater();
            sub.Dispose();
        }

        // Pass 2 — same line split across the replay→live boundary. The L1
        // driver yields the replay snapshot first, then the live tail. The
        // handler must apply both: the second snapshot replaces with
        // identical content → state-machine ends in the same place as
        // pass 1.
        Dictionary<string, int> pass2Eaten;
        long pass2HighWater;
        {
            var catalog = NewCatalog();
            var state = new GourmandStateMachine(catalog);
            using var driver = new TestLogStreamDriver();

            driver.PushReplay(MakeLine(ReportLine, sequence: 100));
            var (sub, highWater) = SubscribeWithHighWaterCapture(driver, state, null);
            await driver.DrainLocalPlayerAsync(TimeSpan.FromSeconds(5));

            // Live re-emit of the same line at a higher sequence: snapshot
            // overwrite is idempotent (Clear + repopulate).
            driver.PushLive(MakeLine(ReportLine, sequence: 200));
            await driver.DrainLocalPlayerAsync(TimeSpan.FromSeconds(5));

            pass2Eaten = new(state.EatenFoodsByInternalName, StringComparer.Ordinal);
            pass2HighWater = highWater();
            sub.Dispose();
        }

        pass2Eaten.Should().BeEquivalentTo(pass1Eaten);
        // Both passes process at least the seq=100 envelope; pass 2 also
        // processes seq=200. High-water should reflect the latest seen.
        pass1HighWater.Should().Be(100);
        pass2HighWater.Should().Be(200);
    }

    /// <summary>
    /// Restart-stability: a second subscription seeded with
    /// <see cref="LogSubscriptionOptions.SkipProcessedHighWater"/> at the
    /// prior session's last-processed sequence drops every replayed envelope
    /// before the handler runs. State-machine sees no Apply, so the
    /// downstream <see cref="GourmandStateMachine.StateChanged"/> count is
    /// zero. This is the canonical Pippin idempotence pattern (#549).
    /// </summary>
    [Fact]
    public async Task High_water_filter_skips_already_processed_envelopes()
    {
        // Session 1 — process both reports, track the high-water.
        var catalog = NewCatalog();
        var state = new GourmandStateMachine(catalog);
        long session1HighWater;
        {
            using var driver = new TestLogStreamDriver();
            var (sub, highWater) = SubscribeWithHighWaterCapture(driver, state, null);

            driver.PushLive(MakeLine(ReportLine, sequence: 100));
            driver.PushLive(MakeLine(SecondReportLine, sequence: 200));
            await driver.DrainLocalPlayerAsync(TimeSpan.FromSeconds(5));

            session1HighWater = highWater();
            sub.Dispose();
        }

        // Snapshot state after session 1 + how many times StateChanged
        // fired in session 2 to assert the high-water gate actually fired
        // (no Apply means no StateChanged).
        var session1Snapshot = new Dictionary<string, int>(state.EatenFoodsByInternalName, StringComparer.Ordinal);
        session1HighWater.Should().Be(200);

        // Session 2 — fresh state machine, re-feed the same envelopes with
        // the high-water seeded from session 1. The driver must drop them
        // before handler invocation. The fresh state machine stays empty.
        var freshState = new GourmandStateMachine(NewCatalog());
        var freshChangedCount = 0;
        freshState.StateChanged += (_, _) => freshChangedCount++;
        {
            using var driver = new TestLogStreamDriver();
            var (sub, _) = SubscribeWithHighWaterCapture(driver, freshState, session1HighWater);

            // Replay phase — these are exactly the envelopes session 1 saw.
            driver.PushReplay(MakeLine(ReportLine, sequence: 100));
            driver.PushReplay(MakeLine(SecondReportLine, sequence: 200));
            await driver.DrainLocalPlayerAsync(TimeSpan.FromSeconds(5));

            sub.Dispose();
        }

        freshChangedCount.Should().Be(0, "every envelope was <= the high-water and skipped before the handler");
        freshState.EatenFoodsByInternalName.Should().BeEmpty();

        // And the original state's contents reflect session 1's two reports
        // (last one wins per GourmandStateMachine snapshot-overwrite shape):
        session1Snapshot.Should().ContainKey("FoodGrapes");
        session1Snapshot["FoodAppleJuice"].Should().Be(8);
    }

    /// <summary>
    /// A new envelope after the high-water STILL gets through — the filter
    /// is a "skip <= HighWater" gate, not a global mute.
    /// </summary>
    [Fact]
    public async Task High_water_does_not_block_post_high_water_envelopes()
    {
        var catalog = NewCatalog();
        var state = new GourmandStateMachine(catalog);

        using var driver = new TestLogStreamDriver();
        var (sub, highWater) = SubscribeWithHighWaterCapture(driver, state, persistedHighWater: 100);

        // Replay seq=50 (below high-water): skipped.
        driver.PushReplay(MakeLine(ReportLine, sequence: 50));
        // Replay seq=100 (== high-water): skipped (the filter is <=).
        driver.PushReplay(MakeLine(ReportLine, sequence: 100));
        // Live seq=200 (above high-water): delivered.
        await driver.DrainLocalPlayerAsync(TimeSpan.FromSeconds(5));
        driver.PushLive(MakeLine(SecondReportLine, sequence: 200));
        await driver.DrainLocalPlayerAsync(TimeSpan.FromSeconds(5));

        state.EatenFoodsByInternalName.Should().ContainKey("FoodGrapes");
        highWater().Should().Be(200);
        sub.Dispose();
    }

    // === helpers ===

    /// <summary>
    /// Mirrors <see cref="GourmandIngestionService"/>'s handler shape minus
    /// the <c>PerCharacterView</c>/dispatcher plumbing: parse → Apply →
    /// advance high-water. The returned <see cref="Func{Long}"/> exposes
    /// the current high-water for assertions.
    /// </summary>
    private static (ILogSubscription Sub, Func<long> HighWater) SubscribeWithHighWaterCapture(
        TestLogStreamDriver driver,
        GourmandStateMachine state,
        long? persistedHighWater)
    {
        var parser = new GourmandLogParser();
        long highWater = persistedHighWater ?? 0;

        var sub = driver.Subscribe<LocalPlayerLogLine>(
            envelope =>
            {
                var payload = envelope.Payload;
                var evt = parser.TryParse(payload.Data, payload.Timestamp.UtcDateTime);
                if (evt is GourmandEvent ge) state.Apply(ge);
                Interlocked.Exchange(ref highWater, payload.Sequence);
                return ValueTask.CompletedTask;
            },
            new LogSubscriptionOptions
            {
                ReplayMode = ReplayMode.FromSessionStart,
                DeliveryContext = DeliveryContext.Inline,
                SkipProcessedHighWater = persistedHighWater,
                DiagnosticCategory = "Pippin.Ingestion.Test",
            });

        return (sub, () => Interlocked.Read(ref highWater));
    }

    /// <summary>
    /// Build a <see cref="LocalPlayerLogLine"/> directly from a full Player.log
    /// shape, mimicking what L0.5 (#532) does on real input: peel the
    /// <c>[ts] LocalPlayer:</c> envelope, leave the rest as <c>Data</c>.
    /// </summary>
    private static LocalPlayerLogLine MakeLine(string fullLine, long sequence)
    {
        const int tsPrefixLen = 11;            // "[HH:MM:SS] "
        const string actorToken = "LocalPlayer: ";
        var idx = 0;
        if (fullLine.Length > tsPrefixLen
            && fullLine[0] == '['
            && fullLine[3] == ':'
            && fullLine[6] == ':'
            && fullLine[9] == ']')
        {
            idx = tsPrefixLen;
        }
        if (idx + actorToken.Length <= fullLine.Length
            && fullLine.IndexOf(actorToken, idx, StringComparison.Ordinal) == idx)
        {
            idx += actorToken.Length;
        }
        var data = idx == 0 ? fullLine : fullLine.Substring(idx);
        return new LocalPlayerLogLine(
            Timestamp: new DateTimeOffset(2026, 5, 18, 22, 4, 9, TimeSpan.Zero),
            Data: data,
            Sequence: sequence,
            ReadMonotonicTicks: 0);
    }

    /// <summary>Minimal stub so FoodCatalog can be constructed with controllable contents.</summary>
    private sealed class StubReferenceDataService : IReferenceDataService
    {
        private readonly Dictionary<long, Item> _items;
        public StubReferenceDataService(Dictionary<long, Item> items) { _items = items; }
        public IReadOnlyList<string> Keys { get; } = [];
        public IReadOnlyDictionary<long, Item> Items => _items;
        public IReadOnlyDictionary<string, Item> ItemsByInternalName { get; } = new Dictionary<string, Item>();
        public ItemKeywordIndex KeywordIndex => ItemKeywordIndex.Empty;
        public IReadOnlyDictionary<string, Recipe> Recipes { get; } = new Dictionary<string, Recipe>();
        public IReadOnlyDictionary<string, Recipe> RecipesByInternalName { get; } = new Dictionary<string, Recipe>();
        public IReadOnlyDictionary<string, SkillEntry> Skills { get; } = new Dictionary<string, SkillEntry>();
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
        public event EventHandler<string>? FileUpdated { add { } remove { } }
        public ReferenceFileSnapshot GetSnapshot(string key) => new(key, ReferenceFileSource.Bundled, "", null, 0);
        public Task RefreshAsync(string key, CancellationToken ct = default) => Task.CompletedTask;
        public Task RefreshAllAsync(CancellationToken ct = default) => Task.CompletedTask;
        public void BeginBackgroundRefresh() { }
    }

    /// <summary>
    /// Test-only <see cref="ILogStreamDriver"/> — minimal copy of the pattern
    /// used by archetype-A tests (<c>tests/Mithril.GameState.Tests/TestSupport/TestLogStreamDriver.cs</c>),
    /// scoped to the <see cref="LocalPlayerLogLine"/> path Pippin needs. The
    /// archetype-B sibling consumers (Samwise, Saruman/Discovery, Legolas)
    /// converge on the same shape — once a third consumer needs it, this
    /// should move to <c>Mithril.Shared.TestSupport</c> (call-site: #549
    /// disposition table, four archetype-B consumers all want this).
    /// </summary>
    private sealed class TestLogStreamDriver : ILogStreamDriver, IDisposable
    {
        private readonly Pipe<LocalPlayerLogLine> _localPlayer = new();
        private readonly List<IDisposable> _subscriptions = new();

        public void PushReplay(LocalPlayerLogLine line) => _localPlayer.AddReplay(line);
        public void PushLive(LocalPlayerLogLine line) => _localPlayer.PushLive(line);

        public Task DrainLocalPlayerAsync(TimeSpan? timeout = null) =>
            _localPlayer.DrainAsync(timeout ?? TimeSpan.FromSeconds(5));

        public ILogSubscription Subscribe<T>(
            Func<LogEnvelope<T>, ValueTask> handler,
            LogSubscriptionOptions? options = null) where T : class
        {
            var opts = options ?? LogSubscriptionOptions.Default;
            if (typeof(T) != typeof(LocalPlayerLogLine))
                throw new ArgumentException("test driver only handles LocalPlayerLogLine", nameof(T));

            var sub = new Subscription<LocalPlayerLogLine>(
                _localPlayer,
                (Func<LogEnvelope<LocalPlayerLogLine>, ValueTask>)(object)handler,
                line => line.Sequence,
                opts);
            sub.Start();
            lock (_subscriptions) _subscriptions.Add(sub);
            return sub;
        }

        public void Dispose()
        {
            IDisposable[] toDispose;
            lock (_subscriptions) { toDispose = _subscriptions.ToArray(); _subscriptions.Clear(); }
            foreach (var s in toDispose) s.Dispose();
        }

        internal sealed class Pipe<T> where T : class
        {
            private readonly List<T> _replay = new();
            private readonly Channel<T> _live = Channel.CreateUnbounded<T>();
            private long _pending;
            private TaskCompletionSource _drained = NewDrainTcsResolved();
            private readonly object _gate = new();
            private bool _replaySnapshotted;

            internal void AddReplay(T line)
            {
                lock (_gate)
                {
                    if (_replaySnapshotted)
                        throw new InvalidOperationException(
                            "AddReplay must be called before any Subscribe<T> consumes the pipe.");
                    Interlocked.Increment(ref _pending);
                    _replay.Add(line);
                    Interlocked.Exchange(ref _drained, NewDrainTcs());
                }
            }

            internal void PushLive(T line)
            {
                Interlocked.Increment(ref _pending);
                Interlocked.Exchange(ref _drained, NewDrainTcs());
                _live.Writer.TryWrite(line);
            }

            internal void RecordDelivered()
            {
                if (Interlocked.Decrement(ref _pending) == 0)
                    _drained.TrySetResult();
            }

            internal Task DrainAsync(TimeSpan timeout) => _drained.Task.WaitAsync(timeout);

            private static TaskCompletionSource NewDrainTcsResolved()
            {
                var tcs = NewDrainTcs();
                tcs.TrySetResult();
                return tcs;
            }

            private static TaskCompletionSource NewDrainTcs() =>
                new(TaskCreationOptions.RunContinuationsAsynchronously);

            internal async IAsyncEnumerable<LogEnvelope<T>> SubscribeAsync(
                [EnumeratorCancellation] CancellationToken ct)
            {
                T[] replaySnap;
                lock (_gate) { replaySnap = _replay.ToArray(); _replaySnapshotted = true; }
                foreach (var line in replaySnap)
                {
                    if (ct.IsCancellationRequested) yield break;
                    yield return new LogEnvelope<T>(line, IsReplay: true);
                }
                await foreach (var line in _live.Reader.ReadAllAsync(ct).ConfigureAwait(false))
                    yield return new LogEnvelope<T>(line, IsReplay: false);
            }
        }

        private sealed class Subscription<T> : ILogSubscription, IDisposable where T : class
        {
            private readonly Pipe<T> _pipe;
            private readonly Func<LogEnvelope<T>, ValueTask> _handler;
            private readonly Func<T, long> _sequenceOf;
            private readonly LogSubscriptionOptions _options;
            private readonly CancellationTokenSource _cts = new();
            private Task? _pumpTask;
            private int _disposed;

            public Subscription(
                Pipe<T> pipe,
                Func<LogEnvelope<T>, ValueTask> handler,
                Func<T, long> sequenceOf,
                LogSubscriptionOptions options)
            {
                _pipe = pipe;
                _handler = handler;
                _sequenceOf = sequenceOf;
                _options = options;
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
                    await foreach (var env in _pipe.SubscribeAsync(ct).ConfigureAwait(false))
                    {
                        if (ct.IsCancellationRequested) break;

                        // High-water filter — drops envelopes whose Sequence
                        // is <= the persisted high-water before handler
                        // invocation. Mirrors the production driver.
                        if (_options.SkipProcessedHighWater is long hw && _sequenceOf(env.Payload) <= hw)
                        {
                            _pipe.RecordDelivered();
                            continue;
                        }

                        // Honor LiveOnly / SinceSubscribe: drop replay-phase.
                        if ((_options.ReplayMode == ReplayMode.LiveOnly ||
                             _options.ReplayMode == ReplayMode.SinceSubscribe) &&
                            env.IsReplay)
                        {
                            _pipe.RecordDelivered();
                            continue;
                        }

                        try { await _handler(env).ConfigureAwait(false); }
                        catch { /* mirror driver containment */ }
                        finally { _pipe.RecordDelivered(); }
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
}
