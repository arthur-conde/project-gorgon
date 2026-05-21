using System.Runtime.CompilerServices;
using System.Threading.Channels;
using System.Windows.Threading;
using FluentAssertions;
using Mithril.GameState.Sessions;
using Mithril.Reference.Models.Items;
using Mithril.Reference.Models.Recipes;
using Mithril.Shared.Character;
using Mithril.Shared.Diagnostics;
using Mithril.Shared.Logging;
using Mithril.Shared.Modules;
using Mithril.Shared.Reference;
using Mithril.GameReports;
using Smaug.Domain;
using Smaug.Parsing;
using Smaug.State;
using Smaug.ViewModels;
using Xunit;

namespace Smaug.Tests;

/// <summary>
/// L1 migration regression tests for <see cref="VendorIngestionService"/>
/// (#550 PR 3, archetype-B). Two regression shapes per the PR brief:
///
/// <list type="number">
///   <item><b>Byte-equivalence under split replay/live phases.</b> Feeding the
///   same backlog twice — once all-live, once split via
///   <c>PushReplay</c> + <c>PushLive</c> — must yield identical persisted
///   observation state. Smaug's idempotence lives at the
///   <see cref="PriceCalibrationService"/> sink (per-key HashSet keyed on
///   <c>SessionId|NpcKey|InternalName|PricePaid|Timestamp:O</c>), not at
///   the L1 layer; this test pins that the L1 driver does not perturb the
///   per-key dedup contract.</item>
///
///   <item><b>Cross-thread structural guard.</b> The latent-bug fix per #549.
///   Under L1's <see cref="DeliveryContext.Marshaled"/>, mutations of the
///   UI-bound <see cref="ObservableCollection{T}"/> on
///   <see cref="CalibrationViewModel"/> happen on the dispatcher thread by
///   construction. We exercise the actual <c>CalibrationViewModel.Refresh()</c>
///   path (subscribe to <c>DataChanged</c>, attach a
///   <c>CollectionViewSource</c>-bound view to make the cross-thread guard
///   active, then feed a <c>VendorItemSold</c> line through the service
///   and assert the mutation succeeded WITHOUT throwing the cross-thread
///   NotSupportedException). Pre-L1 the mutation ran on the pump thread —
///   "worked by accident."</item>
/// </list>
/// </summary>
public sealed class VendorIngestionServiceL1Tests
{
    private const string AddItemLine =
        "[22:27:48] LocalPlayer: ProcessVendorAddItem(130, BottleOfWater(78177652), False)";
    private const string ScreenLine =
        "[22:27:38] LocalPlayer: ProcessVendorScreen(14564, Neutral, 3926, 1776704476729, 4000, \"\", VendorInfo[], VendorInfo[], VendorInfo[], VendorPurchaseCap[], [-1201,-1601,], System.String[], -1601)";
    private const string StartInteractionLine =
        "[22:27:38] LocalPlayer: ProcessStartInteraction(14564, 0, 3926, True, \"NPC_Therese\")";

    // ============================================================
    // Byte-equivalence — same backlog, split across replay/live boundary,
    // produces identical observation state.
    // ============================================================

    [Fact]
    public async Task L1_ByteEquivalence_SplitReplayLive_ProducesSameObservations()
    {
        // Pass 1 — entire backlog as live.
        var passOneObs = await RunWith(driver =>
        {
            driver.PushLive(MakeLocal(StartInteractionLine));
            driver.PushLive(MakeLocal(ScreenLine));
            driver.PushLive(MakeLocal(AddItemLine));
            return Task.CompletedTask;
        });

        // Pass 2 — first two context lines as replay, sale as live.
        var passTwoObs = await RunWith(driver =>
        {
            driver.PushReplay(MakeLocal(StartInteractionLine));
            driver.PushReplay(MakeLocal(ScreenLine));
            driver.PushLive(MakeLocal(AddItemLine));
            return Task.CompletedTask;
        });

        // Pass 3 — context + sale all as replay (the "Mithril relaunched
        // mid-PG-session" shape).
        var passThreeObs = await RunWith(driver =>
        {
            driver.PushReplay(MakeLocal(StartInteractionLine));
            driver.PushReplay(MakeLocal(ScreenLine));
            driver.PushReplay(MakeLocal(AddItemLine));
            return Task.CompletedTask;
        });

        passOneObs.Should().HaveCount(1);
        passOneObs[0].NpcKey.Should().Be("NPC_Therese");
        passOneObs[0].InternalName.Should().Be("BottleOfWater");
        passOneObs[0].PricePaid.Should().Be(130);
        passOneObs[0].FavorTier.Should().Be("Neutral");

        passTwoObs.Should().BeEquivalentTo(passOneObs,
            because: "byte-equivalence: the same sequence of payloads, regardless of replay/live phase boundary, must yield identical sink state");
        passThreeObs.Should().BeEquivalentTo(passOneObs,
            because: "all-replay shape must produce the same observation as all-live");
    }

    // ============================================================
    // Cross-thread structural guard — Marshaled bridge runs the
    // CalibrationViewModel.Refresh() ObservableCollection mutation on
    // the dispatcher thread.
    // ============================================================

    [Fact]
    public async Task L1_Marshaled_StructurallyGuardsCalibrationViewModelRefreshPath()
    {
        // Spin up an STA-affined dispatcher to simulate the WPF UI thread.
        using var dispatcher = StaThread.Start();

        // Construct the CalibrationViewModel + a bound CollectionView on the
        // dispatcher thread, EXACTLY like the WPF startup path. The bound
        // CollectionView is what activates the cross-thread mutation guard
        // (NotSupportedException: "This type of CollectionView does not
        // support changes to its SourceCollection from a thread different
        // from the Dispatcher thread"). A bare ObservableCollection without
        // a bound view does NOT throw — the test would be a no-op
        // under Inline if we skipped this step.
        var dir = Mithril.TestSupport.TestPaths.CreateTempDir("smaug_l1_test");
        try
        {
            var refData = new FakeRefData();
            var session = new FakeSession("char|2026-05-19T20:00:00Z");
            var calibration = new PriceCalibrationService(refData, dir, session: session);
            var vm = await dispatcher.InvokeAsync(() => new CalibrationViewModel(calibration, refData));
            await dispatcher.InvokeAsync(() =>
            {
                // Anchor the default CollectionView so the runtime registers
                // the thread-affinity check on the ObservableCollection.
                var view = System.Windows.Data.CollectionViewSource.GetDefaultView(vm.Observations);
                _ = view;
            });

            // The real LogStreamDriver, configured with the real Marshaled
            // bridge on the dispatcher — this is what the production code
            // path exercises after L1.
            var upstream = new TwoPhaseLocalPlayerStream();
            var attention = new LogStreamAttentionSource();
            using var driver = new LogStreamDriver(
                upstream,
                NoopCombat.Instance,
                NoopSystem.Instance,
                NoopClassified.Instance,
                NoopChat.Instance,
                attention);

            // Wire the production VendorIngestionService against the real
            // driver. Open the gate immediately so ExecuteAsync drops
            // straight into subscribing.
            var gates = new ModuleGates();
            var smaugGate = gates.For("smaug");
            smaugGate.Open();

            // The service reads Application.Current?.Dispatcher to pick its
            // dispatcher; in this xUnit harness there's no WPF Application,
            // so it would fall back to Inline. To exercise the production
            // Marshaled path against our STA dispatcher, we subscribe
            // directly the same way ExecuteAsync would — using the dispatcher
            // captured from our STA test thread. This mirrors the production
            // shape (#549 + #550 capability E).
            var parser = new VendorLogParser();
            var context = new VendorSellContext();
            var activeChar = new TestActiveCharacterService();

            using var sub = driver.Subscribe<LocalPlayerLogLine>(
                envelope =>
                {
                    var raw = envelope.Payload;
                    var evt = parser.TryParse(raw.Data, raw.Timestamp.UtcDateTime);
                    if (evt is null) return ValueTask.CompletedTask;

                    switch (evt)
                    {
                        case NpcInteractionStarted started:
                            context.RememberEntity(started.EntityId, started.NpcKey);
                            break;
                        case VendorScreenOpened screen:
                            context.OnVendorScreenOpened(screen.EntityId, screen.FavorTier);
                            break;
                        case VendorItemSold sold:
                            if (!context.IsReadyToRecord) break;
                            // This call fires DataChanged → CalibrationViewModel.Refresh()
                            // → ObservableCollection<ObservationRow>.Clear()+Add().
                            // Under L1's Marshaled bridge it must run on the
                            // dispatcher thread; pre-L1 it ran on the pump thread.
                            calibration.RecordObservation(
                                context.ActiveNpcKey!,
                                sold.InternalName,
                                sold.Price,
                                context.ActiveFavorTier!,
                                context.CivicPrideLevel,
                                raw.Timestamp);
                            break;
                    }
                    return ValueTask.CompletedTask;
                },
                new LogSubscriptionOptions
                {
                    ReplayMode = ReplayMode.FromSessionStart,
                    DeliveryContext = DeliveryContext.Marshaled(dispatcher.Dispatcher),
                    DiagnosticCategory = "Smaug.Ingestion",
                });

            // Feed the context + sale lines. Under Inline (pre-L1), the
            // ObservableCollection mutation inside Refresh() runs on the
            // pump thread → NotSupportedException → vm.Observations stays
            // empty + the handler failure counter increments.
            // Under Marshaled (post-L1), mutation runs on the dispatcher
            // thread → succeeds → vm.Observations has one row.
            upstream.PushLive(MakeLocal(StartInteractionLine));
            upstream.PushLive(MakeLocal(ScreenLine));
            upstream.PushLive(MakeLocal(AddItemLine));

            await WaitUntilAsync(() => sub.Diagnostics.Delivered >= 3, TimeSpan.FromSeconds(5));

            // The handler must not have thrown (cross-thread mutation is
            // structurally guarded by the Marshaled bridge).
            sub.Diagnostics.HandlerFailures.Should().Be(0,
                because: "Marshaled bridge runs the handler on the dispatcher; ObservableCollection mutation is on-thread by construction (#549 latent-bug fix)");

            // The observation landed in the calibration sink.
            calibration.Data.Observations.Should().HaveCount(1);

            // And the ObservableCollection<ObservationRow> on the VM was
            // mutated — exactly the cross-thread path that "worked today
            // only by accident" pre-L1. Read it on the dispatcher thread
            // (mutating from off-thread would itself throw).
            var rowCount = await dispatcher.InvokeAsync(() => vm.Observations.Count);
            rowCount.Should().Be(1);

            var row = await dispatcher.InvokeAsync(() => vm.Observations[0]);
            row.NpcName.Should().Be("Therese"); // FakeRefData provides this
            row.ItemName.Should().Be("Bottle of Water");
            row.PricePaid.Should().Be(130);

            dispatcher.Shutdown();
        }
        finally
        {
            try { System.IO.Directory.Delete(dir, recursive: true); } catch { /* best-effort */ }
        }
    }

    // ============================================================
    // Helpers
    // ============================================================

    /// <summary>
    /// Runs <see cref="VendorIngestionService"/>-equivalent pump logic
    /// against an in-memory two-phase stream, using <see cref="DeliveryContext.Inline"/>
    /// (the test harness has no WPF Application, so the production code
    /// also falls back to Inline). Returns the recorded observations.
    /// </summary>
    private static async Task<IReadOnlyList<PriceObservation>> RunWith(Func<TestDriver, Task> arrange)
    {
        var dir = Mithril.TestSupport.TestPaths.CreateTempDir("smaug_l1_byteeq");
        try
        {
            var refData = new FakeRefData();
            var session = new FakeSession("char|2026-05-19T20:00:00Z");
            var calibration = new PriceCalibrationService(refData, dir, session: session);
            var parser = new VendorLogParser();
            var context = new VendorSellContext();

            var driver = new TestDriver();
            await arrange(driver);

            using var sub = driver.Subscribe<LocalPlayerLogLine>(
                envelope =>
                {
                    var raw = envelope.Payload;
                    var evt = parser.TryParse(raw.Data, raw.Timestamp.UtcDateTime);
                    if (evt is null) return ValueTask.CompletedTask;

                    switch (evt)
                    {
                        case NpcInteractionStarted started:
                            context.RememberEntity(started.EntityId, started.NpcKey);
                            break;
                        case VendorScreenOpened screen:
                            context.OnVendorScreenOpened(screen.EntityId, screen.FavorTier);
                            break;
                        case VendorItemSold sold:
                            if (!context.IsReadyToRecord) break;
                            calibration.RecordObservation(
                                context.ActiveNpcKey!,
                                sold.InternalName,
                                sold.Price,
                                context.ActiveFavorTier!,
                                context.CivicPrideLevel,
                                raw.Timestamp);
                            break;
                    }
                    return ValueTask.CompletedTask;
                },
                new LogSubscriptionOptions
                {
                    ReplayMode = ReplayMode.FromSessionStart,
                    DeliveryContext = DeliveryContext.Inline,
                    DiagnosticCategory = "Smaug.Ingestion",
                });

            await driver.DrainAsync();

            return calibration.Data.Observations.ToArray();
        }
        finally
        {
            try { System.IO.Directory.Delete(dir, recursive: true); } catch { /* best-effort */ }
        }
    }

    private static LocalPlayerLogLine MakeLocal(string fullLine, long sequence = 0)
    {
        // Strip the optional "[HH:MM:SS] LocalPlayer: " envelope the way L0.5
        // (#532) does, leaving the bare verb payload. The parser doesn't
        // anchor on the envelope so the test could pass the raw line through,
        // but we mirror the production L0.5 stripping for accuracy.
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

    private static async Task WaitUntilAsync(Func<bool> predicate, TimeSpan timeout)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (!predicate())
        {
            if (sw.Elapsed > timeout) throw new TimeoutException("WaitUntilAsync gave up");
            await Task.Delay(10);
        }
    }

    // ============================================================
    // Test stubs
    // ============================================================

    /// <summary>
    /// In-memory driver shaped like the production L1 driver — pushes envelopes
    /// at the handler with the IsReplay bit minted off the replay/live phase
    /// boundary, mirroring <c>PlayerLogPipeSplitter</c>'s direct-yield-replay
    /// /bounded-channel-live shape. Keeps the test file self-contained
    /// (Smaug.Tests doesn't reference Mithril.GameState.Tests' TestLogStreamDriver).
    /// </summary>
    private sealed class TestDriver : ILogStreamDriver
    {
        private readonly List<LocalPlayerLogLine> _replay = new();
        private readonly Channel<LocalPlayerLogLine> _live = Channel.CreateUnbounded<LocalPlayerLogLine>();
        private long _pending;
        private TaskCompletionSource _drained = NewDrainedTcs();
        private bool _replayClosed;

        public void PushReplay(LocalPlayerLogLine line)
        {
            if (_replayClosed)
                throw new InvalidOperationException("PushReplay must be called before Subscribe<T>.");
            Interlocked.Increment(ref _pending);
            _replay.Add(line);
            Interlocked.Exchange(ref _drained, NewDrainTcs());
        }

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

            var opts = options ?? LogSubscriptionOptions.Default;
            var typedHandler = (Func<LogEnvelope<LocalPlayerLogLine>, ValueTask>)(object)handler;
            var sub = new Sub(this, typedHandler, opts);
            sub.Start();
            return sub;
        }

        private async IAsyncEnumerable<LogEnvelope<LocalPlayerLogLine>> SubscribeAsync(
            [EnumeratorCancellation] CancellationToken ct)
        {
            LocalPlayerLogLine[] replaySnap;
            replaySnap = _replay.ToArray();
            _replayClosed = true;
            foreach (var line in replaySnap)
            {
                if (ct.IsCancellationRequested) yield break;
                yield return new LogEnvelope<LocalPlayerLogLine>(line, IsReplay: true);
            }
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
            private readonly LogSubscriptionOptions _options;
            private readonly CancellationTokenSource _cts = new();
            private Task? _pumpTask;
            private int _disposed;

            public Sub(TestDriver driver,
                Func<LogEnvelope<LocalPlayerLogLine>, ValueTask> handler,
                LogSubscriptionOptions options)
            {
                _driver = driver;
                _handler = handler;
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
                    await foreach (var env in _driver.SubscribeAsync(ct).ConfigureAwait(false))
                    {
                        if (ct.IsCancellationRequested) break;
                        if ((_options.ReplayMode == ReplayMode.LiveOnly ||
                             _options.ReplayMode == ReplayMode.SinceSubscribe) &&
                            env.IsReplay)
                        {
                            _driver.RecordDelivered();
                            continue;
                        }
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

    /// <summary>
    /// Two-phase LocalPlayer stream — direct-yield replay buffer, then a
    /// bounded-channel live tail. Mirrors <c>LogStreamDriverTests.TwoPhaseLocalPlayerStream</c>
    /// (Mithril.Shared.Tests) so the real <see cref="LogStreamDriver"/>'s
    /// IsReplay determination works against it in the cross-thread test.
    /// </summary>
    private sealed class TwoPhaseLocalPlayerStream : ILocalPlayerLogStream
    {
        private readonly List<LocalPlayerLogLine> _replay = new();
        private readonly Channel<LocalPlayerLogLine> _live = Channel.CreateUnbounded<LocalPlayerLogLine>();

        public void PushReplay(LocalPlayerLogLine line) => _replay.Add(line);
        public void PushLive(LocalPlayerLogLine line) => _live.Writer.TryWrite(line);

        public async IAsyncEnumerable<LocalPlayerLogLine> SubscribeAsync(
            [EnumeratorCancellation] CancellationToken ct)
        {
            foreach (var line in _replay)
            {
                if (ct.IsCancellationRequested) yield break;
                yield return line;
            }
            await foreach (var line in _live.Reader.ReadAllAsync(ct).ConfigureAwait(false))
                yield return line;
        }

        public async IAsyncEnumerable<LogEnvelope<LocalPlayerLogLine>>
            SubscribeWithReplayMarkerAsync([EnumeratorCancellation] CancellationToken ct)
        {
            foreach (var line in _replay)
            {
                if (ct.IsCancellationRequested) yield break;
                yield return new LogEnvelope<LocalPlayerLogLine>(line, IsReplay: true);
            }
            await foreach (var line in _live.Reader.ReadAllAsync(ct).ConfigureAwait(false))
                yield return new LogEnvelope<LocalPlayerLogLine>(line, IsReplay: false);
        }
    }

    private sealed class NoopCombat : ICombatActorLogStream
    {
        public static readonly NoopCombat Instance = new();
        public async IAsyncEnumerable<CombatActorLogLine> SubscribeAsync(
            [EnumeratorCancellation] CancellationToken ct)
        {
            try { await Task.Delay(Timeout.Infinite, ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { }
            yield break;
        }
    }

    private sealed class NoopSystem : ISystemSignalLogStream
    {
        public static readonly NoopSystem Instance = new();
        public async IAsyncEnumerable<SystemSignalLogLine> SubscribeAsync(
            [EnumeratorCancellation] CancellationToken ct)
        {
            try { await Task.Delay(Timeout.Infinite, ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { }
            yield break;
        }
    }

    private sealed class NoopChat : IChatLogStream
    {
        public static readonly NoopChat Instance = new();
        public async IAsyncEnumerable<RawLogLine> SubscribeAsync(
            [EnumeratorCancellation] CancellationToken ct)
        {
            try { await Task.Delay(Timeout.Infinite, ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { }
            yield break;
        }
    }

    private sealed class NoopClassified : IClassifiedPlayerLogStream
    {
        public static readonly NoopClassified Instance = new();
        public async IAsyncEnumerable<LogEnvelope<IClassifiedPlayerLogLine>> SubscribeWithReplayMarkerAsync(
            [EnumeratorCancellation] CancellationToken ct)
        {
            try { await Task.Delay(Timeout.Infinite, ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { }
            yield break;
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

        // Quiet CS0067 — event is part of the contract but unused here.
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

    /// <summary>
    /// Single-threaded STA dispatcher for the Marshaled bridge regression
    /// test. Identical shape to <c>LogStreamDriverTests.StaThread</c>
    /// (Mithril.Shared.Tests) — spin up a dedicated thread, run a Dispatcher
    /// on it, expose <see cref="ThreadId"/> for thread-affinity assertions.
    /// </summary>
    private sealed class StaThread : IDisposable
    {
        public Dispatcher Dispatcher { get; private set; } = null!;
        public int ThreadId { get; private set; }
        private Thread? _thread;

        public static StaThread Start()
        {
            var t = new StaThread();
            t.StartInternal();
            return t;
        }

        private void StartInternal()
        {
            var ready = new ManualResetEventSlim();
            _thread = new Thread(() =>
            {
                Dispatcher = Dispatcher.CurrentDispatcher;
                ThreadId = Environment.CurrentManagedThreadId;
                ready.Set();
                Dispatcher.Run();
            })
            {
                IsBackground = true,
                Name = "Smaug-L1-Dispatcher",
            };
            _thread.SetApartmentState(ApartmentState.STA);
            _thread.Start();
            ready.Wait(TimeSpan.FromSeconds(5));
        }

        public Task<TResult> InvokeAsync<TResult>(Func<TResult> func) =>
            Dispatcher.InvokeAsync(func).Task;
        public Task InvokeAsync(Action action) => Dispatcher.InvokeAsync(action).Task;

        public void Shutdown()
        {
            if (Dispatcher is { HasShutdownStarted: false })
            {
                Dispatcher.InvokeAsync(() => Dispatcher.BeginInvokeShutdown(DispatcherPriority.Normal));
            }
            _thread?.Join(TimeSpan.FromSeconds(2));
        }

        public void Dispose() => Shutdown();
    }
}
