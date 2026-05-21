using System.IO;
using System.Threading.Channels;
using Arwen.Domain;
using Arwen.Parsing;
using Arwen.State;
using FluentAssertions;
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
    public async Task L1_replay_then_live_drain_yields_identical_state_to_all_live()
    {
        // ── Scenario: an in-session gift to Sanja. The log produces three
        //    events in a fixed order:
        //      StartInteraction(NPC_Sanja) — sets active NPC + absolute favor
        //      DeleteItem(7001)            — instanceId resolves to "Moonstone"
        //      DeltaFavor(NPC_Sanja, +30)  — correlated → calibration observation
        // We assert the gift lands as a confirmed observation in both runs.

        var giftedAt = new DateTimeOffset(2026, 5, 19, 10, 30, 00, TimeSpan.Zero);

        // ── Pass 1: all live (no replay).
        FavorIngestionFixture passLive;
        using (passLive = NewFixture("Pass1"))
        {
            await passLive.StartAsync();
            passLive.Driver.PushLive(MakeLine(
                $"ProcessStartInteraction(42, 0, 100.0, True, \"NPC_Sanja\")", giftedAt));
            passLive.Driver.PushLive(MakeLine(
                $"ProcessDeleteItem(7001)", giftedAt));
            passLive.Driver.PushLive(MakeLine(
                $"ProcessDeltaFavor(0, \"NPC_Sanja\", 30.0, True)", giftedAt));
            await passLive.WaitForDrainAsync();
            await passLive.StopAsync();
        }

        // ── Pass 2: split — first two events arrive as replay, last as live.
        //    This exercises the production FromSessionStart path: the L1
        //    driver yields backlog with IsReplay=true, then live tail with
        //    IsReplay=false. The handler must not gate on IsReplay (Arwen
        //    needs the whole backlog so calibration sees in-session pairs).
        FavorIngestionFixture passSplit;
        using (passSplit = NewFixture("Pass2"))
        {
            passSplit.Driver.PushReplay(MakeLine(
                $"ProcessStartInteraction(42, 0, 100.0, True, \"NPC_Sanja\")", giftedAt));
            passSplit.Driver.PushReplay(MakeLine(
                $"ProcessDeleteItem(7001)", giftedAt));
            await passSplit.StartAsync();
            passSplit.Driver.PushLive(MakeLine(
                $"ProcessDeltaFavor(0, \"NPC_Sanja\", 30.0, True)", giftedAt));
            await passSplit.WaitForDrainAsync();
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
        // re-emitted DeleteItem/DeltaFavor pairs across replays. This is the
        // load-bearing reason archetype-B Arwen doesn't need the L1
        // SkipProcessedHighWater filter — feeding the same events twice must
        // leave state byte-identical.

        var giftedAt = new DateTimeOffset(2026, 5, 19, 10, 30, 00, TimeSpan.Zero);

        using var fixture = NewFixture("DedupRelaunch");
        await fixture.StartAsync();

        // First pass — observation lands.
        fixture.Driver.PushLive(MakeLine(
            $"ProcessStartInteraction(42, 0, 100.0, True, \"NPC_Sanja\")", giftedAt));
        fixture.Driver.PushLive(MakeLine($"ProcessDeleteItem(7001)", giftedAt));
        fixture.Driver.PushLive(MakeLine(
            $"ProcessDeltaFavor(0, \"NPC_Sanja\", 30.0, True)", giftedAt));
        await fixture.WaitForDrainAsync();

        fixture.Calibration.Data.Observations.Should().HaveCount(1);
        var afterFirst = fixture.Calibration.Data.Observations[0];

        // Second pass — same events, same log-line timestamps. Calibration's
        // ObservationKey dedup must drop the replay; observation count and
        // values stay identical.
        fixture.Driver.PushLive(MakeLine(
            $"ProcessStartInteraction(42, 0, 100.0, True, \"NPC_Sanja\")", giftedAt));
        fixture.Driver.PushLive(MakeLine($"ProcessDeleteItem(7001)", giftedAt));
        fixture.Driver.PushLive(MakeLine(
            $"ProcessDeltaFavor(0, \"NPC_Sanja\", 30.0, True)", giftedAt));
        await fixture.WaitForDrainAsync();

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

        private readonly PerCharacterView<ArwenFavorState> _view;
        private readonly SettingsAutoSaver<ArwenSettings> _saver;
        private readonly ArwenSettings _settings;

        public FavorIngestionFixture(string charactersRoot, string arwenDir, string passName)
        {
            var refData = BuildRefData();
            var index = new GiftIndex();
            index.Build(refData.Items, refData.Npcs);
            var inv = new FakeInventory();
            inv.Add(7001, "Moonstone");
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
            Service = new FavorIngestionService(
                Driver,
                new FavorLogParser(),
                stateService,
                Calibration,
                _view,
                active,
                _saver,
                Gates);
        }

        public async Task StartAsync()
        {
            await Service.StartAsync(CancellationToken.None);
            Gates.For("arwen").Open();
            // Allow ExecuteAsync to pass the WaitAsync and call Subscribe.
            // The driver's Subscribe is synchronous from the consumer side,
            // so a brief poll on the pump task being ready suffices.
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
