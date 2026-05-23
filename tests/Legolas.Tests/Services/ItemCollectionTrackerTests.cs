using FluentAssertions;
using Legolas.Domain;
using Legolas.Flow;
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
using Mithril.WorldSim.Player;

namespace Legolas.Tests.Services;

/// <summary>
/// Covers the post-#699 migration: the chat-Add ↔ Player.log-Collect attribution
/// retired its dependency on <see cref="IInventoryView"/>'s cross-source bus and
/// now reads <see cref="PlayerInventoryAdded"/> off <see cref="IPlayerWorld.Bus"/>
/// directly, with a survey-session-bounded FIFO replacing the prior
/// <c>PendingCorrelator&lt;string,int&gt;</c> (principle 4 single-world-direct
/// exit; the 5 s TTL retired alongside the cross-source race it guarded).
/// Credit semantics shifted from "stack-size paired by the view layer" to
/// "+1 per matched (Add, Collect) pair" — see the class-level remarks on
/// <see cref="ItemCollectionTracker"/> for the rationale and #699 for the
/// design conversation.
/// </summary>
public sealed class ItemCollectionTrackerTests
{
    // ── fixture build ───────────────────────────────────────────────────

    private sealed record Harness(
        ItemCollectionTracker Service,
        FakePlayerWorld World,
        TestLogStreamDriver Driver,
        SessionState Session,
        SurveyFlowController Flow,
        LegolasSettings Settings,
        CapturingSink Sink,
        ManualTimeProvider Clock,
        ModuleGates Gates);

    private static Harness Build() => Build(openGate: true);

    private static Harness Build(bool openGate)
    {
        var world = new FakePlayerWorld();
        var driver = new TestLogStreamDriver();
        var parser = new PlayerLogParser();
        var session = new SessionState();
        // Default AutoReset off in tests — production wiring auto-clears
        // CollectedItems via ClearSurveys() when the share-card snapshots on
        // the Done transition, which would race the test's CollectedItems
        // assertion. The tests that exercise the survey-end clear path
        // either drive Flow.Reset() explicitly or assert the transition's
        // Trace + the post-clear empty queue instead of the
        // pre-clear CollectedItems dict.
        var settings = new LegolasSettings { AutoResetWhenAllCollected = false };
        var flow = new SurveyFlowController(session, settings);
        var gates = new ModuleGates();
        if (openGate) gates.For("legolas").Open();
        var sink = new CapturingSink();
        var clock = new ManualTimeProvider(new DateTime(2026, 5, 22, 14, 0, 0, DateTimeKind.Utc));
        var refData = new FakeRefData();
        var svc = new ItemCollectionTracker(
            world, driver, parser, session, flow,
            refData: refData, diag: sink, time: clock);
        return new Harness(svc, world, driver, session, flow, settings, sink, clock, gates);
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
        // StartAsync returns only after the IPlayerWorld.Bus subscription is
        // attached — Call 1's eager-subscribe contract. No prior WaitUntil
        // race-guard required.
        await h.Service.StartAsync(cts.Token);
        try
        {
            await body();
        }
        finally
        {
            await cts.CancelAsync();
            try { await h.Service.StopAsync(CancellationToken.None); }
            catch (OperationCanceledException) { }
            h.Service.Dispose();
            h.Driver.Dispose();
        }
    }

    // Synthetic frame helper — drives the PlayerInventoryAdded channel the
    // post-#699 ItemCollectionTracker subscribes to.
    private static long s_nextInstanceId = 1;
    private static void PushAdded(Harness h, string internalName, long? instanceId = null)
    {
        var id = instanceId ?? Interlocked.Increment(ref s_nextInstanceId);
        var ts = new DateTimeOffset(2026, 5, 22, 14, 0, 0, TimeSpan.Zero);
        h.World.Bus.Publish(ts, new PlayerInventoryAdded(id, internalName, ts.UtcDateTime));
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
    /// Call 1 / principle eager-always (#695, #705): both the
    /// IPlayerWorld.Bus subscription AND the L1 LocalPlayer subscription
    /// (pre-Call-1 sat behind <c>_gates.For("legolas").WaitAsync</c> inside
    /// ExecuteAsync) must be live after `await service.StartAsync(ct)` —
    /// regardless of whether the Legolas tab is ever activated. The bus side
    /// further migrated off <see cref="IInventoryView.Bus"/> onto
    /// <see cref="IPlayerWorld.Bus"/> in #699; the eager-attach shape is
    /// otherwise unchanged from PR #705.
    /// </summary>
    [Fact]
    public async Task Subscription_attaches_in_StartAsync_without_opening_module_gate()
    {
        var h = Build(openGate: false);
        SeedSurvey(h.Session, "Iron Ore");
        await Run(h, async () =>
        {
            PushAdded(h, "IronOre");
            h.Driver.PushLive(CollectLine("Iron Ore"));
            await h.Driver.DrainLocalPlayerAsync();
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            await WaitUntil(() => h.Session.CollectedItems.ContainsKey("Iron Ore"), cts.Token);

            h.Gates.For("legolas").IsOpen.Should().BeFalse(
                "the gate-retirement audit — this test must not touch ModuleGate.Open to validate the eager attach (Call 1)");
            h.Session.CollectedItems["Iron Ore"].Should().Be(1,
                "post-#699 credit is +1 per matched (Add, Collect) pair");
        });
    }

    /// <summary>
    /// Add then Collect credits one. Equivalent of the legacy chat-then-collect
    /// happy path, simplified post-#699 to the +1-per-pair semantic.
    /// </summary>
    [Fact]
    public async Task Added_then_Collect_credits_one_per_pair()
    {
        var h = Build();
        await Run(h, async () =>
        {
            PushAdded(h, "IronOre");
            h.Driver.PushLive(CollectLine("Iron Ore"));
            await h.Driver.DrainLocalPlayerAsync();
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await WaitUntil(() => h.Session.CollectedItems.ContainsKey("Iron Ore"), cts.Token);

            h.Session.CollectedItems["Iron Ore"].Should().Be(1);
            WarnEntries(h.Sink).Should().BeEmpty();
            TraceEntries(h.Sink).Should().BeEmpty();
        });
    }

    /// <summary>
    /// Multiple Adds for the same name don't aggregate on a single Collect —
    /// each Collect dequeues one pending Add. A second Collect pairs with the
    /// next pending Add. Post-#699 the prior <c>SumPendingFor</c> drain-all
    /// idiom retired alongside the cross-source PendingCorrelator.
    /// </summary>
    [Fact]
    public async Task Multiple_Adds_pair_with_one_Collect_each()
    {
        var h = Build();
        await Run(h, async () =>
        {
            PushAdded(h, "IronOre");
            PushAdded(h, "IronOre");
            h.Driver.PushLive(CollectLine("Iron Ore"));
            await h.Driver.DrainLocalPlayerAsync();
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await WaitUntil(() => h.Session.CollectedItems.GetValueOrDefault("Iron Ore", 0) == 1, cts.Token);

            h.Session.CollectedItems["Iron Ore"].Should().Be(1,
                "first Collect dequeues one pending Add");

            // Second Collect pairs with the second pending Add.
            h.Driver.PushLive(CollectLine("Iron Ore"));
            await h.Driver.DrainLocalPlayerAsync();
            await WaitUntil(() => h.Session.CollectedItems["Iron Ore"] == 2, cts.Token);

            h.Session.CollectedItems["Iron Ore"].Should().Be(2);
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
            PushAdded(h, "Garnet");
            // No Add for Fluorite — bonus has no pending Add.
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
    /// Add under one display name must NOT satisfy a Collect for a different
    /// display name. Cross-key contamination guard.
    /// </summary>
    [Fact]
    public async Task Add_under_one_key_is_not_consumed_by_Collect_under_another_key()
    {
        var h = Build();
        await Run(h, async () =>
        {
            PushAdded(h, "IronOre");
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

            h.Session.CollectedItems["Iron Ore"].Should().Be(1);
        });
    }

    /// <summary>
    /// Item that lacks reference-data registration still resolves to the
    /// InternalName as a fallback display key. PG patches occasionally add
    /// items ahead of catalog refresh — the credit-0 + warn path is the
    /// graceful degradation; happy-path stays correct.
    /// </summary>
    [Fact]
    public async Task Unknown_InternalName_falls_back_to_InternalName_as_display_key()
    {
        var h = Build();
        await Run(h, async () =>
        {
            // Not in FakeRefData — display name falls back to InternalName.
            PushAdded(h, "MysteryItem_v3");
            h.Driver.PushLive(CollectLine("MysteryItem_v3"));
            await h.Driver.DrainLocalPlayerAsync();
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await WaitUntil(() => h.Session.CollectedItems.ContainsKey("MysteryItem_v3"), cts.Token);

            h.Session.CollectedItems["MysteryItem_v3"].Should().Be(1);
        });
    }

    // ── Subscribe-before-publish ordering guard ─────────────────────────

    /// <summary>
    /// F1 regression guard (carried forward from PR #705): frames published
    /// <em>between</em> <see cref="ItemCollectionTracker.StartAsync"/>
    /// returning and the host pump's <see cref="ItemCollectionTracker.ExecuteAsync"/>
    /// opening the gate must still be observed. Pre-#688/#606 the bus
    /// subscriptions sat inside <c>ExecuteAsync</c> behind the gate-wait; on
    /// a lazy module that meant every InventoryItemAdded published before
    /// first-tab activation was lost (no replay-on-subscribe). Post-#699 the
    /// guard re-anchors to <see cref="IPlayerWorld.Bus"/> but the contract is
    /// unchanged.
    /// </summary>
    [Fact]
    public async Task Bus_frames_published_before_gate_opens_still_credit()
    {
        // Build a harness whose gate is initially CLOSED.
        var h = Build(openGate: false);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await h.Service.StartAsync(cts.Token);
        try
        {
            h.World.Bus.SubscriberCountFor(typeof(PlayerInventoryAdded)).Should().BeGreaterThan(0,
                "StartAsync must attach the PlayerWorld.Bus subscription before returning, " +
                "regardless of the module-gate state");

            // Push an Add while the gate is still closed — pre-#606 this
            // frame would have been lost.
            PushAdded(h, "IronOre");

            // Open the gate; ExecuteAsync's L1 subscription wakes up and
            // takes the Collect path.
            h.Gates.For("legolas").Open();
            h.Driver.PushLive(CollectLine("Iron Ore"));
            await h.Driver.DrainLocalPlayerAsync();
            await WaitUntil(() => h.Session.CollectedItems.ContainsKey("Iron Ore"), cts.Token);

            h.Session.CollectedItems["Iron Ore"].Should().Be(1,
                "the pre-gate Add must remain pending until the post-gate Collect drains it");
            WarnEntries(h.Sink).Should().BeEmpty();
        }
        finally
        {
            await cts.CancelAsync();
            try { await h.Service.StopAsync(CancellationToken.None); }
            catch (OperationCanceledException) { }
            h.Service.Dispose();
            h.Driver.Dispose();
        }
    }

    // ── Survey-session lifecycle (#699 new behaviour) ──────────────────

    /// <summary>
    /// Post-#699 the cross-source PendingCorrelator's 5 s TTL retired; the
    /// new lifecycle bound is the survey FSM's session. Transitioning to
    /// <see cref="SurveyFlowState.Done"/> clears the pending-Add queue and
    /// surfaces any unmatched names in the Legolas.PendingAdds Trace stream.
    /// </summary>
    [Fact]
    public async Task SurveyFlow_transition_to_Done_clears_pending_and_traces_unmatched()
    {
        var h = Build();
        await Run(h, async () =>
        {
            PushAdded(h, "IronOre");
            PushAdded(h, "Garnet");
            TraceEntries(h.Sink).Should().BeEmpty(
                "no transition has fired yet");

            // Need a pin first so OnAllCollected gates open; simulate the
            // FSM reaching Done by walking through the public transitions.
            // (The most direct path: seed a single survey pin then mark it
            // collected so OnAllCollected fires.)
            var survey = SeedSurvey(h.Session, "Iron Ore");
            // Walk Listening → Gathering (need pins; SeedSurvey added one).
            h.Flow.OptimizeRoute();
            h.Flow.CurrentState.Should().Be(SurveyFlowState.Gathering);
            // Flip the survey row to trigger AllCollected → Done.
            survey.UpdateModel(survey.Model with { Collected = true });
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await WaitUntil(() =>
                h.Flow.CurrentState == SurveyFlowState.Listening
                || h.Flow.CurrentState == SurveyFlowState.Done, cts.Token);

            var traces = TraceEntries(h.Sink);
            traces.Should().ContainSingle(
                "the survey-end transition emits a single Trace listing unmatched pending Adds");
            traces.Single().Message.Should().Contain("Iron Ore").And.Contain("Garnet");

            // The pending queue is empty — a fresh Collect after the
            // transition lands in the credit-0 path.
            h.Driver.PushLive(CollectLine("Iron Ore"));
            await h.Driver.DrainLocalPlayerAsync();
            await WaitUntil(() => WarnEntries(h.Sink).Count > 0, cts.Token);
            WarnEntries(h.Sink).Should().ContainSingle()
                .Which.Message.Should().Contain("Iron Ore").And.Contain("crediting 0");
        });
    }

    /// <summary>
    /// Manual <see cref="SurveyFlowController.Reset"/> from an active phase
    /// is the second survey-session-end signal (auto-reset takes the same
    /// path post-Done). Either trigger clears the pending-Add queue.
    /// </summary>
    [Fact]
    public async Task SurveyFlow_Reset_clears_pending_and_traces_unmatched()
    {
        var h = Build();
        await Run(h, async () =>
        {
            // Seed a pin so Reset has something to clear (and so the
            // transition fires — Listening → Listening is a no-op).
            SeedSurvey(h.Session, "Iron Ore");
            h.Flow.OptimizeRoute();
            h.Flow.CurrentState.Should().Be(SurveyFlowState.Gathering);
            PushAdded(h, "IronOre");

            h.Flow.Reset();

            var traces = TraceEntries(h.Sink);
            traces.Should().ContainSingle();
            traces.Single().Message.Should().Contain("Reset").And.Contain("Iron Ore");

            // Pending queue empty post-reset.
            h.Driver.PushLive(CollectLine("Iron Ore"));
            await h.Driver.DrainLocalPlayerAsync();
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await WaitUntil(() => WarnEntries(h.Sink).Count > 0, cts.Token);
            WarnEntries(h.Sink).Single().Message.Should().Contain("crediting 0");
        });
    }

    // ── Replay determinism (#699 new property) ──────────────────────────

    /// <summary>
    /// Pre-#699, the correlator's <see cref="TimeProvider.System"/>-backed
    /// 5 s TTL caused the same source corpus to produce different eviction
    /// trajectories under different wall-clock attach times — a determinism
    /// violation. Post-#699 the TTL retires entirely; pairing depends only
    /// on FIFO order within the survey session, which is itself driven by
    /// PlayerWorld's source-stream merger. Drive the same synthetic Add /
    /// Collect interleavings with different "elapsed-time gaps" (real
    /// elapsed via the injected ManualTimeProvider) and assert that the
    /// resulting attribution is identical.
    /// </summary>
    [Fact]
    public async Task Identical_PlayerInventoryAdded_and_Collect_sequences_credit_identically_regardless_of_real_elapsed_gaps()
    {
        Dictionary<string, int> RunOnce(TimeSpan[] gaps)
        {
            var h = Build();
            try
            {
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                h.Service.StartAsync(cts.Token).GetAwaiter().GetResult();

                // Same ordering, different real-elapsed gaps. Drive the
                // injected clock forward between events to simulate
                // wildly-different attach scenarios.
                PushAdded(h, "IronOre");
                h.Clock.Advance(gaps[0]);
                PushAdded(h, "IronOre");
                h.Clock.Advance(gaps[1]);
                PushAdded(h, "Garnet");
                h.Clock.Advance(gaps[2]);

                h.Driver.PushLive(CollectLine("Iron Ore"));
                h.Driver.DrainLocalPlayerAsync().GetAwaiter().GetResult();
                h.Clock.Advance(gaps[3]);
                h.Driver.PushLive(CollectLine("Iron Ore"));
                h.Driver.DrainLocalPlayerAsync().GetAwaiter().GetResult();
                h.Clock.Advance(gaps[4]);
                h.Driver.PushLive(CollectLine("Garnet"));
                h.Driver.DrainLocalPlayerAsync().GetAwaiter().GetResult();

                WaitUntil(() => h.Session.CollectedItems.ContainsKey("Garnet"), cts.Token)
                    .GetAwaiter().GetResult();

                return new Dictionary<string, int>(h.Session.CollectedItems);
            }
            finally
            {
                h.Service.Dispose();
                h.Driver.Dispose();
            }
        }

        // Three scenarios — same Add/Collect sequence, different "real elapsed"
        // gaps between events. Pre-#699 the 5 s TTL would have evicted some
        // pending Adds under the long-gap scenario; post-#699 nothing depends
        // on real elapsed time, so all three runs must produce the same dict.
        var tightGaps = new[] { TimeSpan.FromMilliseconds(50), TimeSpan.FromMilliseconds(50),
                                TimeSpan.FromMilliseconds(50), TimeSpan.FromMilliseconds(50),
                                TimeSpan.FromMilliseconds(50) };
        var wideGaps = new[]  { TimeSpan.FromSeconds(30), TimeSpan.FromMinutes(2),
                                TimeSpan.FromMinutes(5), TimeSpan.FromSeconds(45),
                                TimeSpan.FromMinutes(1) };
        var mixedGaps = new[] { TimeSpan.FromMilliseconds(10), TimeSpan.FromMinutes(10),
                                TimeSpan.FromMilliseconds(10), TimeSpan.FromSeconds(8),
                                TimeSpan.FromMilliseconds(10) };

        var tight = await Task.Run(() => RunOnce(tightGaps));
        var wide = await Task.Run(() => RunOnce(wideGaps));
        var mixed = await Task.Run(() => RunOnce(mixedGaps));

        tight.Should().BeEquivalentTo(wide,
            "the same Add/Collect sequence must produce identical credits regardless of elapsed-time gaps");
        wide.Should().BeEquivalentTo(mixed,
            "no surface in the post-#699 design depends on real elapsed time");
        tight["Iron Ore"].Should().Be(2);
        tight["Garnet"].Should().Be(1);
    }

    // ── F2 regression guards (carried forward, semantics-shifted) ──────

    /// <summary>
    /// F2 item 1 (#523 verification-owed pin). Inverted order — Collect
    /// arrives before any Add — must fail cleanly (credit-0 + warn), NOT
    /// silently. The late Add enqueues but is never retroactively credited;
    /// it surfaces in the Trace when the survey ends.
    /// </summary>
    [Fact]
    public async Task Order_inverted_Collect_then_Add_credits_zero_and_warns_and_surfaces_on_session_end()
    {
        var h = Build();
        await Run(h, async () =>
        {
            // Collect arrives BEFORE any Add — no pending bucket for it.
            h.Driver.PushLive(CollectLine("Iron Ore"));
            await h.Driver.DrainLocalPlayerAsync();
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await WaitUntil(() => WarnEntries(h.Sink).Count >= 1, cts.Token);

            h.Session.CollectedItems.Should().NotContainKey("Iron Ore");
            WarnEntries(h.Sink).Should().ContainSingle()
                .Which.Message.Should().Contain("Iron Ore").And.Contain("crediting 0");

            // Late Add enqueues but is NOT retroactively credited.
            PushAdded(h, "IronOre");
            h.Session.CollectedItems.Should().NotContainKey("Iron Ore",
                "the late Add must NOT have been retroactively credited to the earlier Collect");

            // Survey-session end surfaces the orphan Add as Trace.
            SeedSurvey(h.Session, "Iron Ore");
            h.Flow.OptimizeRoute();
            h.Flow.Reset();

            TraceEntries(h.Sink).Should().ContainSingle()
                .Which.Message.Should().Contain("Iron Ore");
        });
    }

    /// <summary>
    /// F2 item 4 — pins the ctor's
    /// <c>_warn = new ThrottledWarn(diag, "Legolas.Ingestion", time: clock)</c>
    /// wiring. Without the injected <see cref="TimeProvider"/>,
    /// <see cref="ThrottledWarn"/> would consult the PlayerWorld.Clock-derived
    /// shim (post-#699; pre-#699 it was wall-clock) and a test that emits
    /// multiple unmatched Collects close together would see only one warn
    /// regardless of fake-clock advance. Three unmatched Collects spaced past
    /// the default 5-second throttle window must produce three distinct warns.
    /// </summary>
    [Fact]
    public async Task ThrottledWarn_uses_injected_TimeProvider_so_advancing_past_window_emits_independently()
    {
        var h = Build();
        await Run(h, async () =>
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));

            h.Driver.PushLive(CollectLine("Iron Ore")); // warn 1
            await h.Driver.DrainLocalPlayerAsync();
            await WaitUntil(() => WarnEntries(h.Sink).Count == 1, cts.Token);

            h.Clock.Advance(TimeSpan.FromSeconds(6)); // past throttle window
            h.Driver.PushLive(CollectLine("Iron Ore")); // warn 2
            await h.Driver.DrainLocalPlayerAsync();
            await WaitUntil(() => WarnEntries(h.Sink).Count == 2, cts.Token);

            h.Clock.Advance(TimeSpan.FromSeconds(6));
            h.Driver.PushLive(CollectLine("Iron Ore")); // warn 3
            await h.Driver.DrainLocalPlayerAsync();
            await WaitUntil(() => WarnEntries(h.Sink).Count == 3, cts.Token);

            WarnEntries(h.Sink).Should().HaveCount(3,
                "each Collect crossed the throttle window via the fake clock");
        });
    }

    // ── fakes ───────────────────────────────────────────────────────────

    /// <summary>
    /// Minimal <see cref="IPlayerWorld"/> stub for ItemCollectionTracker
    /// tests. Mirrors the Gandalf.Tests <c>TestPlayerWorld</c> shape: exposes
    /// a synthetic-publish <see cref="TestEventBus"/> for the bus surface and
    /// a mutable clock for the optional Mode/Now manipulation; the producer /
    /// folder / composer registration surfaces throw to flag accidental use
    /// (those code paths live in <c>Mithril.WorldSim.Player.Tests</c>).
    /// </summary>
    private sealed class FakePlayerWorld : IPlayerWorld
    {
        public MutableWorldClock Clock { get; } = new();
        public TestEventBus Bus { get; } = new();

        IWorldClock IWorld.Clock => Clock;
        IWorldEventBus IWorld.Bus => Bus;

        public void RegisterProducer<T>(IFrameProducer<T> producer) =>
            throw new NotSupportedException("FakePlayerWorld: register on a real world.");
        public void RegisterFolder<T>(IFolder<T> folder) =>
            throw new NotSupportedException("FakePlayerWorld: register on a real world.");
        public void RegisterComposer(IComposer composer) =>
            throw new NotSupportedException("FakePlayerWorld: register on a real world.");
        public Task StartMerger(CancellationToken ct) => Task.CompletedTask;
    }

    private sealed class MutableWorldClock : IWorldClock
    {
        public DateTimeOffset Now { get; set; } = DateTimeOffset.MinValue;
        public long Frame { get; set; }
        public WorldMode Mode { get; set; } = WorldMode.Live;
    }

    private sealed class TestEventBus : IWorldEventBus
    {
        private readonly object _lock = new();
        private readonly Dictionary<Type, List<Delegate>> _handlers = new();

        public IDisposable Subscribe<T>(Action<Frame<T>> handler)
        {
            ArgumentNullException.ThrowIfNull(handler);
            lock (_lock)
            {
                if (!_handlers.TryGetValue(typeof(T), out var list))
                    _handlers[typeof(T)] = list = new List<Delegate>();
                list.Add(handler);
                return new Sub(this, typeof(T), handler);
            }
        }

        public void Publish<T>(DateTimeOffset timestamp, T payload)
        {
            List<Delegate>? snap;
            lock (_lock)
            {
                if (!_handlers.TryGetValue(typeof(T), out var list)) return;
                snap = list.ToList();
            }
            var frame = new Frame<T>(timestamp, payload);
            foreach (var h in snap) ((Action<Frame<T>>)h)(frame);
        }

        public int SubscriberCountFor(Type t)
        {
            lock (_lock) return _handlers.TryGetValue(t, out var list) ? list.Count : 0;
        }

        private sealed class Sub(TestEventBus o, Type t, Delegate h) : IDisposable
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
    /// resolution path.
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
