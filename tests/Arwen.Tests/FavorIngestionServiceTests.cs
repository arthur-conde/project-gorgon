using System.IO;
using System.Threading.Channels;
using Arwen.Domain;
using Arwen.Parsing;
using Arwen.State;
using FluentAssertions;
using Mithril.GameState.Gifting;
using Mithril.Reference.Models.Items;
using Mithril.Shared.Character;
using Mithril.Shared.Logging;
using Mithril.Shared.Modules;
using Mithril.Shared.Reference;
using Mithril.Shared.Settings;
using Mithril.GameReports;
using Xunit;

namespace Arwen.Tests;

/// <summary>
/// Byte-equivalence regression test for the L1 migration of
/// <see cref="FavorIngestionService"/> (#550 PR 3). Arwen is an archetype-B
/// consumer with <see cref="ReplayMode.FromSessionStart"/>, no high-water
/// filter (the calibration sink owns per-key dedup), and Marshaled delivery.
///
/// <para>The test feeds the same event sequence in two shapes and asserts
/// identical final state: (a) all-live drain, vs. (b) replay then live split
/// at the same logical boundary. Re-feeding the same events a second time
/// then checks that <see cref="CalibrationService"/>'s sink-layer
/// <c>_observationKeys</c> HashSet keeps state byte-equivalent — the
/// load-bearing dedup that makes the high-water L1 filter unnecessary.</para>
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
    public async Task L1_favor_snapshot_replays_identically_across_run_shapes()
    {
        // Production-shape regression test for the L1 favor-snapshot side
        // (archetype-B #550 PR 3) — the calibration sink-layer dedup keeps
        // observations stable across replays. Two passes feed the same
        // sequence in different shapes (all-live vs replay-then-live) and
        // assert identical final state.
        //
        // Post-#608: the L1 path no longer drives gift detection (the
        // IGiftSignalService is the production producer of GiftAccepted).
        // The fixture's PublishGiftAccepted helper mimics what the signal
        // service emits when its FSM resolves a gift; the resolved event
        // routes through CalibrationService.OnGiftAccepted, which calls
        // RecordObservation directly.

        var giftedAt = new DateTimeOffset(2026, 5, 19, 10, 30, 00, TimeSpan.Zero);

        // ── Pass 1: all live (no replay).
        FavorIngestionFixture passLive;
        using (passLive = NewFixture("Pass1"))
        {
            await passLive.StartAsync();
            passLive.Driver.PushLive(MakeLine(
                $"ProcessStartInteraction(42, 0, 100.0, True, \"NPC_Sanja\")", giftedAt));
            await passLive.Driver.DrainLocalPlayerAsync();
            passLive.PublishGiftAccepted(7001, "Moonstone", "NPC_Sanja", 30.0, giftedAt);
            await passLive.StopAsync();
        }

        // ── Pass 2: split — StartInteraction arrives as L1 replay; the
        //    GiftAccepted arrives on the signal-service channel after
        //    StartAsync (mirroring the production FromSessionStart drain
        //    where the signal service's own L1 backlog produces resolved
        //    events as the gate opens).
        FavorIngestionFixture passSplit;
        using (passSplit = NewFixture("Pass2"))
        {
            passSplit.Driver.PushReplay(MakeLine(
                $"ProcessStartInteraction(42, 0, 100.0, True, \"NPC_Sanja\")", giftedAt));
            await passSplit.StartAsync();
            await passSplit.Driver.DrainLocalPlayerAsync();
            passSplit.PublishGiftAccepted(7001, "Moonstone", "NPC_Sanja", 30.0, giftedAt);
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
        // Timestamp is keyed to the log-line instant (not wall clock), so both passes match.
        split.Timestamp.Should().Be(live.Timestamp);

        // Exact-favor snapshot from the StartInteraction also matches.
        var liveFavor = passLive.FavorState.GetExactFavor("NPC_Sanja");
        var splitFavor = passSplit.FavorState.GetExactFavor("NPC_Sanja");
        splitFavor.Should().NotBeNull();
        liveFavor.Should().NotBeNull();
        splitFavor!.ExactFavor.Should().Be(liveFavor!.ExactFavor);
    }

    [Fact]
    public async Task Sink_layer_dedup_collapses_replay_on_relaunch()
    {
        // Calibration's _observationKeys HashSet keyed
        // SessionId|InstanceId|NpcKey|Item|Delta|Timestamp:O short-circuits
        // re-emitted GiftAccepted events across replays. This is the
        // load-bearing reason archetype-B Arwen doesn't need a high-water
        // filter on either subscription — feeding the same event twice must
        // leave state byte-identical.

        var giftedAt = new DateTimeOffset(2026, 5, 19, 10, 30, 00, TimeSpan.Zero);

        using var fixture = NewFixture("DedupRelaunch");
        await fixture.StartAsync();

        // First pass — observation lands.
        fixture.Driver.PushLive(MakeLine(
            $"ProcessStartInteraction(42, 0, 100.0, True, \"NPC_Sanja\")", giftedAt));
        await fixture.Driver.DrainLocalPlayerAsync();
        fixture.PublishGiftAccepted(7001, "Moonstone", "NPC_Sanja", 30.0, giftedAt);

        fixture.Calibration.Data.Observations.Should().HaveCount(1);
        var afterFirst = fixture.Calibration.Data.Observations[0];

        // Second pass — same events, same log-line timestamps. Calibration's
        // ObservationKey dedup must drop the replay; observation count and
        // values stay identical.
        fixture.Driver.PushLive(MakeLine(
            $"ProcessStartInteraction(42, 0, 100.0, True, \"NPC_Sanja\")", giftedAt));
        await fixture.Driver.DrainLocalPlayerAsync();
        fixture.PublishGiftAccepted(7001, "Moonstone", "NPC_Sanja", 30.0, giftedAt);

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
        // Principle 13 — world-event-driven paths must not leak the host's
        // real wall clock into derived state. Pre-#715 this handler stamped
        // ArwenFavorState.SetExactFavor with DateTimeOffset.UtcNow, so the
        // persisted NpcFavorSnapshot.Timestamp drifted across replay runs
        // even when the underlying log line carried the same envelope
        // timestamp. Post-#715 the timestamp comes from the envelope (the
        // same `ts` already in scope for the gift-detection plumbing two
        // lines above) — replay produces byte-identical snapshots.
        //
        // The test runs the same log line twice with a real-elapsed pause
        // between the two passes and asserts the persisted timestamp is the
        // envelope's value (well in the past), not "now-ish".

        var giftedAt = new DateTimeOffset(2020, 1, 2, 3, 4, 5, TimeSpan.Zero);

        using var passOne = NewFixture("PassOne");
        await passOne.StartAsync();
        passOne.Driver.PushLive(MakeLine(
            "ProcessStartInteraction(42, 0, 73.5, True, \"NPC_Sanja\")", giftedAt));
        await passOne.Driver.DrainLocalPlayerAsync();
        await passOne.StopAsync();

        // Inject a real-elapsed gap between the two passes. Under the old
        // wall-clock stamp the two snapshot timestamps would diverge by at
        // least this much; under the envelope-stamp fix they're identical.
        await Task.Delay(TimeSpan.FromMilliseconds(50));

        using var passTwo = NewFixture("PassTwo");
        await passTwo.StartAsync();
        passTwo.Driver.PushLive(MakeLine(
            "ProcessStartInteraction(42, 0, 73.5, True, \"NPC_Sanja\")", giftedAt));
        await passTwo.Driver.DrainLocalPlayerAsync();
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
    public async Task Subscription_attaches_in_StartAsync_without_opening_module_gate()
    {
        // Call 1 / principle eager-always: the L1 LocalPlayer subscription and
        // the IGiftSignalService React subscription must both be in place by
        // the time `await service.StartAsync(ct)` returns — regardless of
        // whether the Arwen tab is ever activated. Pre-Call-1 the L1
        // subscribe sat inside ExecuteAsync behind `await gate.WaitAsync`;
        // post-Call-1 it lifts into StartAsync so a session-start replay
        // drain reaches the FavorState rebuild even on a never-opened tab.
        //
        // The original sink-list source is the Call 1 ratification in
        // docs/world-simulator.md §Decisions ratified post-#642 (resolves #695).

        var giftedAt = new DateTimeOffset(2026, 5, 19, 10, 30, 00, TimeSpan.Zero);

        using var fixture = NewFixture("EagerStartAsync");

        // Pre-load the L1 driver with a session-start replay snapshot.
        fixture.Driver.PushReplay(MakeLine(
            $"ProcessStartInteraction(42, 0, 100.0, True, \"NPC_Sanja\")", giftedAt));

        // Start the host WITHOUT opening the arwen module gate. The Call 1
        // contract requires the subscription to be live regardless.
        await fixture.Service.StartAsync(CancellationToken.None);
        await fixture.Driver.WaitForSubscriptionAsync();

        fixture.Gates.For("arwen").IsOpen.Should().BeFalse(
            "the gate-retirement audit — this test must not touch ModuleGate.Open to validate the eager attach (Call 1)");

        // The replay snapshot drains through the live subscription.
        await fixture.Driver.DrainLocalPlayerAsync();
        await fixture.Service.StopAsync(CancellationToken.None);

        // FavorState was rebuilt from the replayed verb — no tab activation needed.
        var favor = fixture.FavorState.GetExactFavor("NPC_Sanja");
        favor.Should().NotBeNull(
            "the L1 subscription's session-start replay processed the FavorUpdate while the gate stayed closed");
        favor!.ExactFavor.Should().Be(100.0);
    }

    [Fact]
    public async Task Late_subscribe_to_IGiftSignalService_still_observes_replayed_gifts()
    {
        // ── The #608 race contract (iteration 2 of the PR).
        //
        //    Concrete scenario: a gift's ProcessAddItem + ProcessStartInteraction
        //    + ProcessDeleteItem + ProcessDeltaFavor all land in the L1
        //    backlog (replay-from-session-start). The signal service's
        //    single L1 subscription consumes the backlog as part of its
        //    HostedService.ExecuteAsync, resolves the gift, and emits
        //    GiftAccepted on its React channel.
        //
        //    If FavorIngestionService.ExecuteAsync hasn't subscribed to
        //    IGiftSignalService yet (e.g., it's still blocked on
        //    _gate.WaitAsync), the emitted GiftAccepted is held in the
        //    signal service's internal event log. When the subscription
        //    later attaches with the default replay=FromSessionStart, the
        //    full backlog is atomically replayed to the new handler.
        //
        //    This test pre-loads the FakeGiftSignalService with a backlog
        //    GiftAccepted, then subscribes; the replay path delivers it,
        //    and CalibrationService.OnGiftAccepted records the observation.
        //    This is the late-subscribe-safety property that distinguishes
        //    the Tier-2 signal-service approach from a direct PlayerWorld
        //    bus subscription (which has no replay).

        var giftedAt = new DateTimeOffset(2026, 5, 19, 10, 30, 00, TimeSpan.Zero);

        using var fixture = NewFixture("LateSubscribe");

        // Pre-load a resolved gift into the signal service's event log
        // BEFORE the FavorIngestionService subscribes. With no replay
        // semantics this event would be dropped; with the React-channel
        // replay-on-subscribe contract, it lands when Arwen subscribes.
        fixture.GiftSignal.PublishToBacklog(new GiftAccepted(
            NpcKey: "NPC_Sanja",
            ItemInstanceId: 7001,
            ItemInternalName: "Moonstone",
            DeltaFavor: 30.0,
            Timestamp: giftedAt,
            InteractionStartedAt: giftedAt));

        // Now Arwen comes up. The subscription's default replay mode delivers
        // the backlog before any live events.
        await fixture.StartAsync();
        await fixture.GiftSignal.WaitForSubscriptionAsync();

        fixture.Calibration.Data.Observations.Should().HaveCount(1,
            "late-subscribe to IGiftSignalService replays the resolved gift backlog");
        var observation = fixture.Calibration.Data.Observations[0];
        observation.NpcKey.Should().Be("NPC_Sanja");
        observation.ItemInternalName.Should().Be("Moonstone");
        observation.FavorDelta.Should().Be(30.0);
        observation.InstanceId.Should().Be(7001);

        await fixture.StopAsync();
    }

    // ── Helpers ───────────────────────────────────────────────────────

    private FavorIngestionFixture NewFixture(string passName) =>
        new(_charactersRoot, _arwenDir, passName);

    private static LocalPlayerLogLine MakeLine(string data, DateTimeOffset ts) =>
        new(ts, data, Sequence: 0, ReadMonotonicTicks: 0);

    /// <summary>
    /// Composes a fresh <see cref="FavorIngestionService"/> end-to-end with a
    /// minimal in-memory <see cref="ILogStreamDriver"/> fake (no real
    /// dispatcher — falls back to Inline delivery), seeded reference data,
    /// and a per-test data directory for calibration persistence.
    /// </summary>
    private sealed class FavorIngestionFixture : IDisposable
    {
        public TestLogStreamDriver Driver { get; }
        public CalibrationService Calibration { get; }
        public ArwenFavorState FavorState { get; }
        public FavorIngestionService Service { get; }
        public ModuleGates Gates { get; }
        public FakeGiftSignalService GiftSignal { get; }

        private readonly PerCharacterView<ArwenFavorState> _view;
        private readonly SettingsAutoSaver<ArwenSettings> _saver;
        private readonly ArwenSettings _settings;

        public FavorIngestionFixture(string charactersRoot, string arwenDir, string passName)
        {
            var refData = BuildRefData();
            var index = new GiftIndex();
            index.Build(refData.Items, refData.Npcs);
            var inv = new FakeInventory();
            // No pre-seed of inv — post-#608 the GiftAccepted event carries
            // the resolved InternalName directly. The fixture's
            // PublishGiftAccepted helper mimics what IGiftSignalService emits
            // when its FSM resolves a gift; production code uses the same
            // subscription path.
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
            // Touch Current so the view caches a state for the active character;
            // the ingestion service writes into _view.Current.SetExactFavor.
            FavorState = _view.Current!;

            var stateService = new FavorStateService(refData, active, _view);

            _settings = new ArwenSettings();
            _saver = new SettingsAutoSaver<ArwenSettings>(
                new InMemorySettingsStore(),
                _settings);

            Gates = new ModuleGates();

            Driver = new TestLogStreamDriver();
            GiftSignal = new FakeGiftSignalService();
            Service = new FavorIngestionService(
                Driver,
                new FavorLogParser(),
                stateService,
                Calibration,
                _view,
                active,
                _saver,
                GiftSignal);
        }

        /// <summary>
        /// Emit a resolved <see cref="GiftAccepted"/> on the fake signal
        /// service, mimicking what <c>GiftSignalService</c> publishes when
        /// its single-pump FSM correlates the verb triple. The handler runs
        /// synchronously on the caller thread, matching production's
        /// "fires on the signal service's L1 pump" contract.
        /// </summary>
        public void PublishGiftAccepted(
            long instanceId,
            string internalName,
            string npcKey,
            double delta,
            DateTimeOffset ts)
        {
            GiftSignal.PublishLive(new GiftAccepted(
                NpcKey: npcKey,
                ItemInstanceId: instanceId,
                ItemInternalName: internalName,
                DeltaFavor: delta,
                Timestamp: ts,
                InteractionStartedAt: ts));
        }

        public async Task StartAsync()
        {
            // Post-Call-1 (#695): the L1 subscription attaches inside
            // FavorIngestionService.StartAsync — Subscribe completes before
            // this await returns, so the WaitForSubscriptionAsync poll
            // below is a defence-in-depth sync rather than a wait-for-gate.
            // The Gates field stays on the fixture only so the
            // "Subscription_attaches_in_StartAsync_without_opening_module_gate"
            // test can assert IsOpen.Should().BeFalse() against it.
            await Service.StartAsync(CancellationToken.None);
            await Driver.WaitForSubscriptionAsync();
        }

        public Task WaitForDrainAsync() => Driver.DrainLocalPlayerAsync();

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
            Driver.Dispose();
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

        /// <summary>
        /// Stub <see cref="IGiftSignalService"/> for tests. Implements the
        /// React-channel atomic-replay-then-live contract that
        /// <c>GiftSignalService</c> ships with: <see cref="Subscribe"/>
        /// replays the in-memory event log to the new handler under a lock
        /// before adding it to the live-handler list, so the late-subscribe
        /// path is exercised exactly as production code does.
        ///
        /// <para><see cref="PublishToBacklog"/> appends to the event log
        /// without firing live handlers (simulates events that resolved
        /// before any subscription attached). <see cref="PublishLive"/>
        /// fires synchronously to all attached handlers AND appends to the
        /// log so subsequent subscribers see the same event on replay.</para>
        /// </summary>
        internal sealed class FakeGiftSignalService : IGiftSignalService
        {
            private readonly object _lock = new();
            private readonly List<GiftAccepted> _eventLog = new();
            private readonly List<Action<GiftAccepted>> _handlers = new();
            private TaskCompletionSource _subscribed = new(TaskCreationOptions.RunContinuationsAsynchronously);

            public Task WaitForSubscriptionAsync(TimeSpan? timeout = null) =>
                _subscribed.Task.WaitAsync(timeout ?? TimeSpan.FromSeconds(5));

            public IDisposable Subscribe(
                Action<GiftAccepted> handler,
                ReplayMode replay = ReplayMode.FromSessionStart)
            {
                ArgumentNullException.ThrowIfNull(handler);
                lock (_lock)
                {
                    if (replay == ReplayMode.FromSessionStart)
                    {
                        foreach (var evt in _eventLog) handler(evt);
                    }
                    _handlers.Add(handler);
                    _subscribed.TrySetResult();
                    return new Sub(this, handler);
                }
            }

            public void PublishToBacklog(GiftAccepted gift)
            {
                lock (_lock) { _eventLog.Add(gift); }
            }

            public void PublishLive(GiftAccepted gift)
            {
                List<Action<GiftAccepted>> snap;
                lock (_lock)
                {
                    _eventLog.Add(gift);
                    snap = _handlers.ToList();
                }
                foreach (var h in snap) h(gift);
            }

            private sealed class Sub : IDisposable
            {
                private readonly FakeGiftSignalService _owner;
                private readonly Action<GiftAccepted> _handler;

                public Sub(FakeGiftSignalService owner, Action<GiftAccepted> handler)
                {
                    _owner = owner;
                    _handler = handler;
                }

                public void Dispose()
                {
                    lock (_owner._lock) { _owner._handlers.Remove(_handler); }
                }
            }
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
    /// Minimal in-memory <see cref="ILogStreamDriver"/> for archetype-B Arwen.
    /// Mirrors the production driver's two-phase shape (replay snapshot
    /// followed by live channel) without pulling in the full L0.5 router
    /// stack. Only the LocalPlayer pipe is implemented — Arwen has no other
    /// subscription.
    /// </summary>
    private sealed class TestLogStreamDriver : ILogStreamDriver, IDisposable
    {
        private readonly List<LocalPlayerLogLine> _replay = new();
        private readonly Channel<LocalPlayerLogLine> _live =
            Channel.CreateUnbounded<LocalPlayerLogLine>();
        private readonly object _gate = new();
        private bool _snapshotted;
        private long _pending;
        private TaskCompletionSource _drained = ResolvedTcs();
        private readonly TaskCompletionSource _subscribed = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly List<IDisposable> _subs = new();

        public void PushReplay(LocalPlayerLogLine line)
        {
            lock (_gate)
            {
                if (_snapshotted)
                    throw new InvalidOperationException("PushReplay called after snapshot — push before StartAsync.");
                Interlocked.Increment(ref _pending);
                Interlocked.Exchange(ref _drained, NewTcs());
                _replay.Add(line);
            }
        }

        public void PushLive(LocalPlayerLogLine line)
        {
            Interlocked.Increment(ref _pending);
            Interlocked.Exchange(ref _drained, NewTcs());
            _live.Writer.TryWrite(line);
        }

        public Task DrainLocalPlayerAsync(TimeSpan? timeout = null) =>
            Volatile.Read(ref _drained).Task.WaitAsync(timeout ?? TimeSpan.FromSeconds(5));

        public Task WaitForSubscriptionAsync() =>
            _subscribed.Task.WaitAsync(TimeSpan.FromSeconds(5));

        public ILogSubscription Subscribe<T>(
            Func<LogEnvelope<T>, ValueTask> handler,
            LogSubscriptionOptions? options = null) where T : class
        {
            if (typeof(T) != typeof(LocalPlayerLogLine))
                throw new ArgumentException(
                    $"TestLogStreamDriver in this fixture only supports LocalPlayerLogLine, got {typeof(T).Name}");

            var typed = (Func<LogEnvelope<LocalPlayerLogLine>, ValueTask>)(object)handler;
            var sub = new Subscription(this, typed);
            lock (_subs) _subs.Add(sub);
            sub.Start();
            _subscribed.TrySetResult();
            return sub;
        }

        public void Dispose()
        {
            IDisposable[] toDispose;
            lock (_subs)
            {
                toDispose = _subs.ToArray();
                _subs.Clear();
            }
            foreach (var s in toDispose) s.Dispose();
            _live.Writer.TryComplete();
        }

        private void RecordDelivered()
        {
            if (Interlocked.Decrement(ref _pending) == 0)
                Volatile.Read(ref _drained).TrySetResult();
        }

        private async IAsyncEnumerable<LogEnvelope<LocalPlayerLogLine>> SubscribeStreamAsync(
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
        {
            LocalPlayerLogLine[] snap;
            lock (_gate) { snap = _replay.ToArray(); _snapshotted = true; }
            foreach (var line in snap)
            {
                if (ct.IsCancellationRequested) yield break;
                yield return new LogEnvelope<LocalPlayerLogLine>(line, IsReplay: true);
            }
            await foreach (var line in _live.Reader.ReadAllAsync(ct).ConfigureAwait(false))
                yield return new LogEnvelope<LocalPlayerLogLine>(line, IsReplay: false);
        }

        private static TaskCompletionSource NewTcs() =>
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        private static TaskCompletionSource ResolvedTcs()
        {
            var t = NewTcs();
            t.TrySetResult();
            return t;
        }

        private sealed class Subscription : ILogSubscription
        {
            private readonly TestLogStreamDriver _driver;
            private readonly Func<LogEnvelope<LocalPlayerLogLine>, ValueTask> _handler;
            private readonly CancellationTokenSource _cts = new();
            private Task? _pump;
            private int _disposed;

            public Subscription(
                TestLogStreamDriver driver,
                Func<LogEnvelope<LocalPlayerLogLine>, ValueTask> handler)
            {
                _driver = driver;
                _handler = handler;
            }

            public string Id { get; } = $"test#{Guid.NewGuid():N}";
            public LogSubscriptionDiagnostics Diagnostics =>
                new(0, 0, 0, 0, 0, LogSubscriptionState.Healthy);
            public event EventHandler? StateChanged { add { } remove { } }

            public void Start() => _pump = Task.Run(PumpAsync);

            private async Task PumpAsync()
            {
                var ct = _cts.Token;
                try
                {
                    await foreach (var env in _driver.SubscribeStreamAsync(ct).ConfigureAwait(false))
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
                try { _pump?.Wait(TimeSpan.FromSeconds(2)); } catch { }
                try { _cts.Dispose(); } catch { }
            }
        }
    }
}
