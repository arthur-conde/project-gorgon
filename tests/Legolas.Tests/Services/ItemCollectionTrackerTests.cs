using FluentAssertions;
using Legolas.Domain;
using Legolas.Services;
using Legolas.Tests.TestSupport;
using Legolas.ViewModels;
using Mithril.GameState.Inventory;
using Mithril.Reference.Models.Items;
using Mithril.Shared.Diagnostics;
using Mithril.Shared.Logging;
using Mithril.Shared.Modules;
using Mithril.Shared.Reference;
using Mithril.WorldSim;
using Xunit;

namespace Legolas.Tests.Services;

/// <summary>
/// Covers the #606 migration: the chat <c>[Status] X xN added to inventory.</c>
/// ↔ <c>[Status] X collected!</c> correlator moved off <c>IChatLogStream</c>
/// and onto <see cref="IInventoryView.Bus"/> (typed-frame typed-bus per #602)
/// plus the new Player.log <c>ProcessScreenText(ImportantInfo, "&lt;Mineral&gt;
/// collected!")</c> parse. Behavioural shape preserved from the retired
/// <c>LogIngestionService</c> — credit-0 + warn on unmatched takes, Trace on
/// TTL eviction, FIFO summed credit across multiple pending adds, name-keyed
/// (display-name resolved via <see cref="IReferenceDataService"/>).
/// </summary>
public sealed class ItemCollectionTrackerTests
{
    // ── fixture build ───────────────────────────────────────────────────

    private sealed record Harness(
        ItemCollectionTracker Service,
        FakeInventoryView View,
        TestLogStreamDriver Driver,
        SessionState Session,
        CapturingSink Sink,
        ManualTimeProvider Clock);

    private static Harness Build()
    {
        var view = new FakeInventoryView();
        var driver = new TestLogStreamDriver();
        var parser = new PlayerLogParser();
        var session = new SessionState();
        var gates = new ModuleGates();
        gates.For("legolas").Open();
        var sink = new CapturingSink();
        var clock = new ManualTimeProvider(new DateTime(2026, 5, 22, 14, 0, 0, DateTimeKind.Utc));
        var refData = new FakeRefData();
        var svc = new ItemCollectionTracker(
            view, driver, parser, gates, session,
            refData: refData, diag: sink, time: clock);
        return new Harness(svc, view, driver, session, sink, clock);
    }

    private static SurveyItemViewModel SeedSurvey(SessionState session, string displayName)
    {
        var vm = new SurveyItemViewModel(Survey.Create(displayName, new MetreOffset(0, 0), gridIndex: 0));
        session.Surveys.Add(vm);
        return vm;
    }

    private static async Task Run(Harness h, Func<Task> body)
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var run = h.Service.StartAsync(cts.Token);
        try
        {
            // The ExecuteAsync pump opens the gate, then subscribes to the
            // inventory bus + log driver. Pushes that beat the subscription
            // are lost — wait for the inventory subscription to land so test
            // ordering is deterministic.
            await WaitUntil(() => h.View.AddedSubscriberCount > 0, cts.Token);
            await body();
        }
        finally
        {
            await cts.CancelAsync();
            try { await h.Service.StopAsync(CancellationToken.None); }
            catch (OperationCanceledException) { }
            _ = run;
            h.Service.Dispose();
            h.Driver.Dispose();
        }
    }

    // PG live capture mirrors — ProcessScreenText collect banners.
    private static LocalPlayerLogLine CollectLine(string mineral, string? bonus = null)
    {
        var msg = bonus is null
            ? $"ProcessScreenText(ImportantInfo, \"{mineral} collected!\")"
            : $"ProcessScreenText(ImportantInfo, \"{mineral} collected! Also found {bonus} (speed bonus!)\")";
        return new LocalPlayerLogLine(
            new DateTimeOffset(DateTime.UtcNow, TimeSpan.Zero),
            msg,
            Sequence: 0,
            ReadMonotonicTicks: 0);
    }

    private static List<(DiagnosticLevel Level, string Category, string Message)> WarnEntries(CapturingSink sink) =>
        sink.Snapshot().Where(e => e.Level == DiagnosticLevel.Warn).ToList();

    private static List<(DiagnosticLevel Level, string Category, string Message)> TraceEntries(CapturingSink sink) =>
        sink.Snapshot().Where(e => e.Level == DiagnosticLevel.Trace && e.Category == "Legolas.PendingAdds").ToList();

    private static async Task WaitUntil(Func<bool> predicate, CancellationToken ct)
    {
        while (!predicate())
        {
            ct.ThrowIfCancellationRequested();
            await Task.Delay(15, ct);
        }
    }

    // ── tests ───────────────────────────────────────────────────────────

    /// <summary>
    /// Added (SizeConfirmed=true) then ProcessScreenText collected! within TTL
    /// credits the stack size. Equivalent of the legacy chat add-then-collect
    /// happy path.
    /// </summary>
    [Fact]
    public async Task Added_then_Collect_credits_parsed_count()
    {
        var h = Build();
        await Run(h, async () =>
        {
            h.View.PushAdded("IronOre", stackSize: 3, sizeConfirmed: true);
            h.Driver.PushLive(CollectLine("Iron Ore"));
            await h.Driver.DrainLocalPlayerAsync();
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await WaitUntil(() => h.Session.CollectedItems.ContainsKey("Iron Ore"), cts.Token);

            h.Session.CollectedItems["Iron Ore"].Should().Be(3);
            WarnEntries(h.Sink).Should().BeEmpty();
            TraceEntries(h.Sink).Should().BeEmpty();
        });
    }

    /// <summary>
    /// Unconfirmed Added doesn't enqueue (the matching StackChanged back-fill
    /// will). This is the post-#602 semantic — the view defaults to (1,
    /// unconfirmed) when no chat correlation has paired yet.
    /// </summary>
    [Fact]
    public async Task Unconfirmed_Added_does_not_enqueue_credit()
    {
        var h = Build();
        var survey = SeedSurvey(h.Session, "Iron Ore");
        await Run(h, async () =>
        {
            h.View.PushAdded("IronOre", stackSize: 1, sizeConfirmed: false);
            h.Driver.PushLive(CollectLine("Iron Ore"));
            await h.Driver.DrainLocalPlayerAsync();
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await WaitUntil(() => WarnEntries(h.Sink).Count > 0, cts.Token);

            h.Session.CollectedItems.Should().NotContainKey("Iron Ore",
                "unconfirmed Added is intentionally skipped — StackChanged back-fill carries the size");
            survey.Collected.Should().BeTrue(
                "survey-row flip is independent of the count credit path");
            WarnEntries(h.Sink).Should().ContainSingle()
                .Which.Message.Should().Contain("Iron Ore").And.Contain("crediting 0");
        });
    }

    /// <summary>
    /// StackChanged (back-fill or stack bump) enqueues — this is how
    /// previously-defaulted Adds get their authoritative size credited.
    /// </summary>
    [Fact]
    public async Task StackChanged_after_unconfirmed_Add_credits_via_backfill()
    {
        var h = Build();
        await Run(h, async () =>
        {
            h.View.PushAdded("IronOre", stackSize: 1, sizeConfirmed: false);
            // View emits the back-fill within its own TTL window.
            h.View.PushStackChanged("IronOre", stackSize: 5);
            h.Driver.PushLive(CollectLine("Iron Ore"));
            await h.Driver.DrainLocalPlayerAsync();
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await WaitUntil(() => h.Session.CollectedItems.ContainsKey("Iron Ore"), cts.Token);

            h.Session.CollectedItems["Iron Ore"].Should().Be(5);
            WarnEntries(h.Sink).Should().BeEmpty();
        });
    }

    /// <summary>
    /// Multiple confirmed Adds for the same item collapse into one summed
    /// credit on collect (FIFO drain). Models a survey node that emits
    /// several adds (split-stacked drops) before the collect line.
    /// </summary>
    [Fact]
    public async Task Multiple_confirmed_Adds_same_name_collapse_into_summed_Collect()
    {
        var h = Build();
        await Run(h, async () =>
        {
            h.View.PushAdded("IronOre", stackSize: 3, sizeConfirmed: true);
            h.View.PushAdded("IronOre", stackSize: 2, sizeConfirmed: true);
            h.Driver.PushLive(CollectLine("Iron Ore"));
            await h.Driver.DrainLocalPlayerAsync();
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await WaitUntil(() => h.Session.CollectedItems.GetValueOrDefault("Iron Ore", 0) == 5, cts.Token);

            h.Session.CollectedItems["Iron Ore"].Should().Be(5);
            WarnEntries(h.Sink).Should().BeEmpty();
        });
    }

    /// <summary>
    /// Collect with no pending Add applies the credit-0 policy: dict
    /// untouched, warn emitted, survey-row flag still flips.
    /// </summary>
    [Fact]
    public async Task Collect_with_no_pending_Add_skips_dict_warns_and_flips_survey_row()
    {
        var h = Build();
        var survey = SeedSurvey(h.Session, "Iron Ore");
        await Run(h, async () =>
        {
            h.Driver.PushLive(CollectLine("Iron Ore"));
            await h.Driver.DrainLocalPlayerAsync();
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await WaitUntil(() => WarnEntries(h.Sink).Count > 0, cts.Token);

            h.Session.CollectedItems.Should().NotContainKey("Iron Ore");
            survey.Collected.Should().BeTrue();
            WarnEntries(h.Sink).Should().ContainSingle()
                .Which.Message.Should().Contain("Iron Ore").And.Contain("crediting 0");
        });
    }

    /// <summary>
    /// Speed-bonus tail credits the bonus item via the same per-name policy.
    /// Primary with pending Add gets credit; bonus with no Add gets credit-0
    /// + warn.
    /// </summary>
    [Fact]
    public async Task SpeedBonus_branch_applies_credit_zero_independently()
    {
        var h = Build();
        await Run(h, async () =>
        {
            h.View.PushAdded("Garnet", stackSize: 1, sizeConfirmed: true);
            // No add for Fluorite — bonus has no pending Add.
            h.Driver.PushLive(CollectLine("Garnet", bonus: "Fluorite"));
            await h.Driver.DrainLocalPlayerAsync();
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await WaitUntil(() => h.Session.CollectedItems.ContainsKey("Garnet"), cts.Token);
            await WaitUntil(() => WarnEntries(h.Sink).Count >= 1, cts.Token);

            h.Session.CollectedItems["Garnet"].Should().Be(1);
            h.Session.CollectedItems.Should().NotContainKey("Fluorite");
            WarnEntries(h.Sink).Should().ContainSingle()
                .Which.Message.Should().Contain("Fluorite");
        });
    }

    /// <summary>
    /// Added past TTL falls through to credit-0; the eviction Trace fires
    /// when the bucket is next touched.
    /// </summary>
    [Fact]
    public async Task Add_past_TTL_then_Collect_credits_zero_and_emits_eviction_trace()
    {
        var h = Build();
        var survey = SeedSurvey(h.Session, "Iron Ore");
        await Run(h, async () =>
        {
            h.View.PushAdded("IronOre", stackSize: 3, sizeConfirmed: true);
            // Let the Bus subscription land before advancing the clock.
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await WaitUntil(() => h.View.AddedSubscriberCount > 0, cts.Token);
            await Task.Delay(50);
            h.Clock.Advance(TimeSpan.FromSeconds(12)); // past 5s TTL
            h.Driver.PushLive(CollectLine("Iron Ore"));
            await h.Driver.DrainLocalPlayerAsync();
            await WaitUntil(() => WarnEntries(h.Sink).Count > 0, cts.Token);

            h.Session.CollectedItems.Should().NotContainKey("Iron Ore");
            survey.Collected.Should().BeTrue();
            WarnEntries(h.Sink).Should().ContainSingle()
                .Which.Message.Should().Contain("Iron Ore").And.Contain("crediting 0");
            TraceEntries(h.Sink).Should().ContainSingle()
                .Which.Message.Should().Contain("Iron Ore").And.Contain("x3");
        });
    }

    /// <summary>
    /// Add under one display name must NOT satisfy a Collect for a different
    /// display name. Cross-key contamination guard.
    /// </summary>
    [Fact]
    public async Task Add_under_one_key_is_not_consumed_by_Collect_under_another_key()
    {
        var h = Build();
        await Run(h, async () =>
        {
            h.View.PushAdded("IronOre", stackSize: 3, sizeConfirmed: true);
            h.Driver.PushLive(CollectLine("Copper Ore"));
            await h.Driver.DrainLocalPlayerAsync();
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await WaitUntil(() => WarnEntries(h.Sink).Count >= 1, cts.Token);

            h.Session.CollectedItems.Should().NotContainKey("Copper Ore");
            WarnEntries(h.Sink).Should().ContainSingle()
                .Which.Message.Should().Contain("Copper Ore");

            // Iron Ore's Add must still be pending — later same-name collect credits it.
            h.Driver.PushLive(CollectLine("Iron Ore"));
            await h.Driver.DrainLocalPlayerAsync();
            await WaitUntil(() => h.Session.CollectedItems.ContainsKey("Iron Ore"), cts.Token);

            h.Session.CollectedItems["Iron Ore"].Should().Be(3);
        });
    }

    /// <summary>
    /// Zero-count Added is dropped at the boundary — never enqueues — so a
    /// later Collect under the same name doesn't credit 0.
    /// </summary>
    [Fact]
    public async Task Zero_stackSize_Added_is_dropped_at_boundary()
    {
        var h = Build();
        var survey = SeedSurvey(h.Session, "Iron Ore");
        await Run(h, async () =>
        {
            h.View.PushAdded("IronOre", stackSize: 0, sizeConfirmed: true);
            h.Driver.PushLive(CollectLine("Iron Ore"));
            await h.Driver.DrainLocalPlayerAsync();
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await WaitUntil(() => WarnEntries(h.Sink).Count > 0, cts.Token);

            h.Session.CollectedItems.Should().NotContainKey("Iron Ore");
            survey.Collected.Should().BeTrue();
            WarnEntries(h.Sink).Should().ContainSingle();
        });
    }

    /// <summary>
    /// Item that lacks reference-data registration still resolves to the
    /// InternalName as a fallback display key. PG patches occasionally add
    /// items ahead of catalog refresh — the credit-0 + warn path is the
    /// graceful degradation.
    /// </summary>
    [Fact]
    public async Task Unknown_InternalName_falls_back_to_InternalName_as_display_key()
    {
        var h = Build();
        await Run(h, async () =>
        {
            // Not in FakeRefData — display name falls back to InternalName.
            h.View.PushAdded("MysteryItem_v3", stackSize: 2, sizeConfirmed: true);
            h.Driver.PushLive(CollectLine("MysteryItem_v3"));
            await h.Driver.DrainLocalPlayerAsync();
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await WaitUntil(() => h.Session.CollectedItems.ContainsKey("MysteryItem_v3"), cts.Token);

            h.Session.CollectedItems["MysteryItem_v3"].Should().Be(2);
        });
    }

    // ── fakes ───────────────────────────────────────────────────────────

    private sealed class FakeInventoryView : IInventoryView
    {
        private readonly TestBus _bus = new();
        public IWorldEventBus Bus => _bus;
        public bool TryResolve(long instanceId, out string internalName) { internalName = ""; return false; }
        public bool TryGetStackSize(long instanceId, out int stackSize) { stackSize = 0; return false; }
        public IDisposable Subscribe(Action<InventoryEvent> handler, ReplayMode replay = ReplayMode.FromSessionStart)
            => throw new NotSupportedException("Legacy shim not used in #606 tests");

        public int AddedSubscriberCount => _bus.SubscriberCountFor(typeof(InventoryItemAdded));

        public void PushAdded(string internalName, int stackSize, bool sizeConfirmed)
        {
            var ts = new DateTimeOffset(2026, 5, 22, 14, 0, 0, TimeSpan.Zero);
            _bus.Publish(new Frame<InventoryItemAdded>(ts,
                new InventoryItemAdded(InstanceId: 1, internalName, stackSize, sizeConfirmed, ts.UtcDateTime)));
        }

        public void PushStackChanged(string internalName, int stackSize)
        {
            var ts = new DateTimeOffset(2026, 5, 22, 14, 0, 1, TimeSpan.Zero);
            _bus.Publish(new Frame<InventoryStackChanged>(ts,
                new InventoryStackChanged(InstanceId: 1, internalName, stackSize, SizeConfirmed: true, ts.UtcDateTime)));
        }
    }

    private sealed class TestBus : IWorldEventBus
    {
        private readonly object _lock = new();
        private readonly Dictionary<Type, List<Action<IFrame>>> _handlers = new();

        public IDisposable Subscribe<T>(Action<Frame<T>> handler)
        {
            ArgumentNullException.ThrowIfNull(handler);
            lock (_lock)
            {
                if (!_handlers.TryGetValue(typeof(T), out var list))
                    _handlers[typeof(T)] = list = new List<Action<IFrame>>();
                Action<IFrame> wrapper = f => handler((Frame<T>)f);
                list.Add(wrapper);
                return new Sub(this, typeof(T), wrapper);
            }
        }

        public int SubscriberCountFor(Type t)
        {
            lock (_lock) return _handlers.TryGetValue(t, out var list) ? list.Count : 0;
        }

        public void Publish<T>(Frame<T> frame)
        {
            List<Action<IFrame>>? snap;
            lock (_lock)
            {
                if (!_handlers.TryGetValue(typeof(T), out var list)) return;
                snap = list.ToList();
            }
            foreach (var h in snap) h(frame);
        }

        private sealed class Sub(TestBus o, Type t, Action<IFrame> h) : IDisposable
        {
            public void Dispose()
            {
                lock (o._lock) { if (o._handlers.TryGetValue(t, out var list)) list.Remove(h); }
            }
        }
    }

    private sealed class ManualTimeProvider : TimeProvider
    {
        private DateTimeOffset _now;
        public ManualTimeProvider(DateTime utcStart) =>
            _now = new DateTimeOffset(utcStart, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(TimeSpan delta) => _now = _now.Add(delta);
    }

    /// <summary>
    /// Minimal IReferenceDataService for the InternalName→DisplayName
    /// resolution path. Only <see cref="ItemsByInternalName"/> matters here
    /// — the rest of the catalog is satisfied by the interface's default
    /// member accessors.
    /// </summary>
    private sealed class FakeRefData : IReferenceDataService
    {
        private static readonly Item _ironOre = new()
        {
            Id = 1, Name = "Iron Ore", InternalName = "IronOre",
            MaxStackSize = 1000, IconId = 0, Keywords = [],
        };
        private static readonly Item _copperOre = new()
        {
            Id = 2, Name = "Copper Ore", InternalName = "CopperOre",
            MaxStackSize = 1000, IconId = 0, Keywords = [],
        };
        private static readonly Item _garnet = new()
        {
            Id = 3, Name = "Garnet", InternalName = "Garnet",
            MaxStackSize = 1000, IconId = 0, Keywords = [],
        };

        public IReadOnlyList<string> Keys { get; } = ["items"];
        public IReadOnlyDictionary<long, Item> Items { get; } = new Dictionary<long, Item>
        {
            [1L] = _ironOre, [2L] = _copperOre, [3L] = _garnet,
        };
        public IReadOnlyDictionary<string, Item> ItemsByInternalName { get; } = new Dictionary<string, Item>(StringComparer.Ordinal)
        {
            ["IronOre"] = _ironOre,
            ["CopperOre"] = _copperOre,
            ["Garnet"] = _garnet,
        };
        public ItemKeywordIndex KeywordIndex => new(Items);
        public IReadOnlyDictionary<string, Mithril.Reference.Models.Recipes.Recipe> Recipes { get; }
            = new Dictionary<string, Mithril.Reference.Models.Recipes.Recipe>();
        public IReadOnlyDictionary<string, Mithril.Reference.Models.Recipes.Recipe> RecipesByInternalName { get; }
            = new Dictionary<string, Mithril.Reference.Models.Recipes.Recipe>();
        public IReadOnlyDictionary<string, SkillEntry> Skills { get; } = new Dictionary<string, SkillEntry>();
        public IReadOnlyDictionary<string, XpTableEntry> XpTables { get; } = new Dictionary<string, XpTableEntry>();
        public IReadOnlyDictionary<string, NpcEntry> Npcs { get; } = new Dictionary<string, NpcEntry>();
        public IReadOnlyDictionary<string, AreaEntry> Areas { get; } = new Dictionary<string, AreaEntry>(StringComparer.Ordinal);
        public IReadOnlyDictionary<string, IReadOnlyList<ItemSource>> ItemSources { get; } = new Dictionary<string, IReadOnlyList<ItemSource>>();
        public IReadOnlyDictionary<string, AttributeEntry> Attributes { get; } = new Dictionary<string, AttributeEntry>();
        public IReadOnlyDictionary<string, PowerEntry> Powers { get; } = new Dictionary<string, PowerEntry>();
        public IReadOnlyDictionary<string, IReadOnlyList<string>> Profiles { get; } = new Dictionary<string, IReadOnlyList<string>>();
        public IReadOnlyDictionary<string, Mithril.Reference.Models.Quests.Quest> Quests { get; }
            = new Dictionary<string, Mithril.Reference.Models.Quests.Quest>();
        public IReadOnlyDictionary<string, Mithril.Reference.Models.Quests.Quest> QuestsByInternalName { get; }
            = new Dictionary<string, Mithril.Reference.Models.Quests.Quest>();
        public IReadOnlyDictionary<string, string> Strings { get; } = new Dictionary<string, string>(StringComparer.Ordinal);
        public ReferenceFileSnapshot GetSnapshot(string key) => new(key, ReferenceFileSource.Bundled, "test", null, 0);
        public Task RefreshAsync(string key, CancellationToken ct = default) => Task.CompletedTask;
        public Task RefreshAllAsync(CancellationToken ct = default) => Task.CompletedTask;
        public void BeginBackgroundRefresh() { }
        public event EventHandler<string>? FileUpdated { add { } remove { } }
    }

    private sealed class CapturingSink : IDiagnosticsSink
    {
        private readonly List<(DiagnosticLevel Level, string Category, string Message)> _entries = new();
        public void Write(DiagnosticLevel level, string category, string message)
        {
            lock (_entries) _entries.Add((level, category, message));
        }

        public IReadOnlyList<(DiagnosticLevel Level, string Category, string Message)> Snapshot()
        {
            lock (_entries) return _entries.ToArray();
        }

        IReadOnlyList<DiagnosticEntry> IDiagnosticsSink.Snapshot() => Array.Empty<DiagnosticEntry>();
        public event EventHandler<DiagnosticEntry>? EntryAdded { add { } remove { } }
    }
}
