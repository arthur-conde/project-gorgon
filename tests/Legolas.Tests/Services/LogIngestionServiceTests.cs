using System.Threading.Channels;
using FluentAssertions;
using Legolas.Domain;
using Legolas.Flow;
using Legolas.Services;
using Legolas.ViewModels;
using Mithril.Shared.Diagnostics;
using Mithril.Shared.Logging;
using Mithril.Shared.Modules;
using Xunit;

namespace Legolas.Tests.Services;

/// <summary>
/// Covers the #523-deliverable-2 migration of the chat-side ADD↔COLLECT
/// correlation from a hand-rolled buffer + "credit at least 1" fallback to
/// the shared <c>PendingCorrelator&lt;string, int&gt;</c> primitive with an
/// explicit credit-0 + diag.Warn policy on the take side and a Trace eviction
/// callback on the TTL-evict side.
///
/// Verification owed (issue #523):
///   - item 2 ("synthetic same-second pair correlates regardless of order"):
///     for Legolas this reduces to "the inverted COLLECT-before-ADD case
///     fails cleanly, not silently" — see <see cref="Order_inverted_Collect_then_Add_credits_zero_and_warns"/>.
///   - item 3 (no regression after migration): the full fixture.
/// </summary>
public sealed class LogIngestionServiceTests
{
    // ---- fixture build ---------------------------------------------------

    private sealed record Harness(
        LogIngestionService Service,
        ScriptedChatStream Stream,
        SessionState Session,
        CapturingSink Sink,
        ManualTimeProvider Clock);

    private static Harness Build()
    {
        var stream = new ScriptedChatStream();
        var session = new SessionState();
        var gates = new ModuleGates();
        gates.For("legolas").Open();
        var sink = new CapturingSink();
        var clock = new ManualTimeProvider(new DateTime(2026, 5, 20, 14, 0, 0, DateTimeKind.Utc));
        // The motherlode coordinator and area-calibration service aren't
        // exercised by add/collect pathways, but the ctor needs concrete
        // references. Reuse the test stubs other fixtures already ship.
        var motherlode = new MotherlodeMeasurementCoordinator(
            new MultilaterationSolver(),
            new MotherlodeFlowController(session),
            new FakePlayerPositionTracker(),
            new FakePlayerPinTracker());
        var svc = new LogIngestionService(
            stream,
            new ChatLogParser(),
            gates,
            session,
            motherlode,
            new FakeAreaCalibrationService(),
            diag: sink,
            time: clock);
        return new Harness(svc, stream, session, sink, clock);
    }

    private static SurveyItemViewModel SeedSurvey(SessionState session, string name)
    {
        // Relative-offset survey is the cheapest shape and the chat-collect
        // name-match loop reads only Name + Collected + RouteOrder.
        var vm = new SurveyItemViewModel(Survey.Create(name, new MetreOffset(0, 0), gridIndex: 0));
        session.Surveys.Add(vm);
        return vm;
    }

    private static async Task Run(Harness h, Func<Task> body)
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var run = h.Service.StartAsync(cts.Token);
        try { await body(); }
        finally
        {
            await cts.CancelAsync();
            try { await h.Service.StopAsync(CancellationToken.None); }
            catch (OperationCanceledException) { }
            _ = run;
            h.Service.Dispose();
        }
    }

    private static IEnumerable<(DiagnosticLevel Level, string Category, string Message)> WarnEntries(CapturingSink sink) =>
        sink.Entries.Where(e => e.Level == DiagnosticLevel.Warn);

    private static IEnumerable<(DiagnosticLevel Level, string Category, string Message)> TraceEntries(CapturingSink sink) =>
        sink.Entries.Where(e => e.Level == DiagnosticLevel.Trace && e.Category == "Legolas.PendingAdds");

    // ---- tests -----------------------------------------------------------

    /// <summary>
    /// Test 1 — Add-then-Collect within TTL credits the parsed count, no warn,
    /// no eviction trace.
    /// </summary>
    [Fact]
    public async Task Add_then_Collect_credits_parsed_count()
    {
        var h = Build();
        await Run(h, async () =>
        {
            h.Stream.Push("[Status] Iron Ore x3 added to inventory.");
            h.Stream.Push("[Status] Iron Ore collected!");
            await h.Stream.WaitForDrainAsync();

            h.Session.CollectedItems.Should().ContainKey("Iron Ore").WhoseValue.Should().Be(3);
            WarnEntries(h.Sink).Should().BeEmpty();
            TraceEntries(h.Sink).Should().BeEmpty();
        });
    }

    /// <summary>
    /// Test 2 — Multiple ADDs under the same key (FIFO) collapse into one
    /// summed credit on COLLECT. Models a survey node that fires several
    /// chat-added lines (split-stacked drops) before the collect line.
    /// </summary>
    [Fact]
    public async Task Multiple_Adds_same_name_collapse_into_one_summed_Collect()
    {
        var h = Build();
        await Run(h, async () =>
        {
            h.Stream.Push("[Status] Iron Ore x3 added to inventory.");
            h.Stream.Push("[Status] Iron Ore x2 added to inventory.");
            h.Stream.Push("[Status] Iron Ore collected!");
            await h.Stream.WaitForDrainAsync();

            h.Session.CollectedItems["Iron Ore"].Should().Be(5);
            WarnEntries(h.Sink).Should().BeEmpty();
        });
    }

    /// <summary>
    /// Test 3 — COLLECT with no pending ADD applies the credit-0 policy: the
    /// dict is left untouched (key absent, NOT 0), a warn is emitted naming
    /// the item, and the survey row's <see cref="SurveyItemViewModel.Collected"/>
    /// flag still flips via its independent name-match path.
    /// </summary>
    [Fact]
    public async Task Collect_with_no_pending_Add_skips_dict_write_warns_and_still_flips_survey_row()
    {
        var h = Build();
        var survey = SeedSurvey(h.Session, "Iron Ore");
        await Run(h, async () =>
        {
            h.Stream.Push("[Status] Iron Ore collected!");
            await h.Stream.WaitForDrainAsync();

            h.Session.CollectedItems.Should().NotContainKey("Iron Ore",
                "credit-0 path skips AccumulateCollected so the share card omits an x0 line");
            survey.Collected.Should().BeTrue(
                "the survey row's Collected flag is independent of the count credit path");
            WarnEntries(h.Sink).Should().ContainSingle()
                .Which.Message.Should().Contain("Iron Ore").And.Contain("crediting 0");
        });
    }

    /// <summary>
    /// Test 4 — ADD within TTL window is matched (boundary check on TTL).
    /// </summary>
    [Fact]
    public async Task Add_then_advance_within_TTL_then_Collect_credits()
    {
        var h = Build();
        await Run(h, async () =>
        {
            h.Stream.Push("[Status] Iron Ore x3 added to inventory.");
            await h.Stream.WaitForDrainAsync();
            h.Clock.Advance(TimeSpan.FromSeconds(4)); // within 5s TTL
            h.Stream.Push("[Status] Iron Ore collected!");
            await h.Stream.WaitForDrainAsync();

            h.Session.CollectedItems["Iron Ore"].Should().Be(3);
            WarnEntries(h.Sink).Should().BeEmpty();
        });
    }

    /// <summary>
    /// Test 5 — ADD that times out before COLLECT falls through to credit-0,
    /// emits the take-side Warn, AND fires the eviction-side Trace callback
    /// (lazy eviction triggered by TryTake walking the bucket head).
    /// </summary>
    [Fact]
    public async Task Add_then_advance_past_TTL_then_Collect_credits_zero_warns_and_emits_eviction_trace()
    {
        var h = Build();
        var survey = SeedSurvey(h.Session, "Iron Ore");
        await Run(h, async () =>
        {
            h.Stream.Push("[Status] Iron Ore x3 added to inventory.");
            await h.Stream.WaitForDrainAsync();
            h.Clock.Advance(TimeSpan.FromSeconds(12)); // well past 5s TTL
            h.Stream.Push("[Status] Iron Ore collected!");
            await h.Stream.WaitForDrainAsync();

            h.Session.CollectedItems.Should().NotContainKey("Iron Ore");
            survey.Collected.Should().BeTrue();
            WarnEntries(h.Sink).Should().ContainSingle()
                .Which.Message.Should().Contain("Iron Ore").And.Contain("crediting 0");
            TraceEntries(h.Sink).Should().ContainSingle()
                .Which.Message.Should().Contain("Iron Ore").And.Contain("x3");
        });
    }

    /// <summary>
    /// Test 6 — SpeedBonusItem is credited via the same per-name policy. Primary
    /// has a pending ADD → credited; bonus has none → credit-0 + warn (only for
    /// the bonus). Verifies the two paths are independent.
    /// </summary>
    [Fact]
    public async Task SpeedBonusItem_branch_applies_credit_zero_per_item_independently()
    {
        var h = Build();
        await Run(h, async () =>
        {
            h.Stream.Push("[Status] Garnet x1 added to inventory.");
            // No "[Status] Fluorite added to inventory." — bonus has no ADD.
            h.Stream.Push("[Status] Garnet collected! Also found Fluorite (speed bonus!)");
            await h.Stream.WaitForDrainAsync();

            h.Session.CollectedItems.Should().ContainKey("Garnet").WhoseValue.Should().Be(1);
            h.Session.CollectedItems.Should().NotContainKey("Fluorite");
            WarnEntries(h.Sink).Should().ContainSingle()
                .Which.Message.Should().Contain("Fluorite");
        });
    }

    /// <summary>
    /// Test 7 — Order-inverted (COLLECT before ADD) yields the credit-0 path
    /// and the late ADD enqueues but is never retroactively credited. This is
    /// the "regardless of arrival order" assertion from #523 verification-owed
    /// item 2, narrowed to its Legolas-applicable form: the inversion fails
    /// cleanly (warn) rather than silently.
    /// </summary>
    [Fact]
    public async Task Order_inverted_Collect_then_Add_credits_zero_and_warns()
    {
        var h = Build();
        await Run(h, async () =>
        {
            h.Stream.Push("[Status] Iron Ore collected!");
            h.Stream.Push("[Status] Iron Ore x3 added to inventory.");
            await h.Stream.WaitForDrainAsync();

            h.Session.CollectedItems.Should().NotContainKey("Iron Ore");
            WarnEntries(h.Sink).Should().ContainSingle()
                .Which.Message.Should().Contain("Iron Ore");

            // The late ADD enqueues. Advance past TTL and trigger lazy eviction
            // by issuing an unrelated TryTake; the late ADD must NOT have been
            // retroactively credited to the earlier collect, and the eviction
            // Trace must fire when the bucket is next touched.
            h.Clock.Advance(TimeSpan.FromSeconds(12));
            h.Stream.Push("[Status] Iron Ore collected!"); // second collect, also unmatched
            await h.Stream.WaitForDrainAsync();

            h.Session.CollectedItems.Should().NotContainKey("Iron Ore");
            TraceEntries(h.Sink).Should().ContainSingle()
                .Which.Message.Should().Contain("Iron Ore").And.Contain("x3");
        });
    }

    /// <summary>
    /// Test 8a — documents the residual same-name-noise-within-TTL risk
    /// flagged in the design: an unrelated chat ADD (skinning/vendor/crafting)
    /// for the same display name that lands within the TTL window before a
    /// real survey COLLECT will be miscredited. This is the cost of dropping
    /// the pre-#523 <c>Clear()</c>-on-collect punctuation; mitigated by the
    /// 5-second TTL matching <c>InventoryService.PendingChatTtl</c>.
    /// </summary>
    [Fact]
    public async Task Inter_event_same_name_noise_within_TTL_silently_miscredits_documented_residual()
    {
        var h = Build();
        await Run(h, async () =>
        {
            h.Stream.Push("[Status] Iron Ore x1 added to inventory."); // pretend skinning
            await h.Stream.WaitForDrainAsync();
            h.Clock.Advance(TimeSpan.FromSeconds(2));
            h.Stream.Push("[Status] Iron Ore x3 added to inventory."); // real survey
            h.Stream.Push("[Status] Iron Ore collected!");
            await h.Stream.WaitForDrainAsync();

            h.Session.CollectedItems["Iron Ore"].Should().Be(4, "noise + real fell within TTL");
            WarnEntries(h.Sink).Should().BeEmpty("no take-side miss");
        });
    }

    /// <summary>
    /// Test 8b — the TTL mitigation in action: noise older than the TTL is
    /// evicted before COLLECT, so only the fresh ADD counts. Eviction Trace
    /// surfaces the dropped noise so post-hoc "why was my credit short"
    /// debugging has a trail.
    /// </summary>
    [Fact]
    public async Task Inter_event_same_name_noise_beyond_TTL_is_evicted_and_only_fresh_Add_credits()
    {
        var h = Build();
        await Run(h, async () =>
        {
            h.Stream.Push("[Status] Iron Ore x1 added to inventory."); // stale noise
            await h.Stream.WaitForDrainAsync();
            h.Clock.Advance(TimeSpan.FromSeconds(6)); // past 5s TTL
            h.Stream.Push("[Status] Iron Ore x3 added to inventory."); // fresh real
            h.Stream.Push("[Status] Iron Ore collected!");
            await h.Stream.WaitForDrainAsync();

            h.Session.CollectedItems["Iron Ore"].Should().Be(3, "stale Add was TTL-evicted before take");
            WarnEntries(h.Sink).Should().BeEmpty("the fresh ADD satisfied the COLLECT");
            TraceEntries(h.Sink).Should().ContainSingle()
                .Which.Message.Should().Contain("Iron Ore").And.Contain("x1");
        });
    }

    /// <summary>
    /// Negative-name cross-contamination guard — ADD under one key must NOT
    /// satisfy a COLLECT under a different key. The pending ADD survives the
    /// unrelated COLLECT and is available to a later same-name COLLECT.
    /// </summary>
    [Fact]
    public async Task Add_under_one_key_is_not_consumed_by_Collect_under_another_key()
    {
        var h = Build();
        await Run(h, async () =>
        {
            h.Stream.Push("[Status] Iron Ore x3 added to inventory.");
            h.Stream.Push("[Status] Copper Ore collected!");
            await h.Stream.WaitForDrainAsync();

            h.Session.CollectedItems.Should().NotContainKey("Copper Ore");
            WarnEntries(h.Sink).Should().ContainSingle()
                .Which.Message.Should().Contain("Copper Ore");

            // Iron Ore's ADD must still be pending — a later matching COLLECT
            // credits it.
            h.Stream.Push("[Status] Iron Ore collected!");
            await h.Stream.WaitForDrainAsync();

            h.Session.CollectedItems["Iron Ore"].Should().Be(3);
            WarnEntries(h.Sink).Should().ContainSingle(
                "no second warn — Iron Ore was successfully credited");
        });
    }

    // ---- helpers ---------------------------------------------------------

    private sealed class ScriptedChatStream : IChatLogStream
    {
        private readonly Channel<RawLogLine> _channel = Channel.CreateUnbounded<RawLogLine>();
        private long _pending;
        private TaskCompletionSource _drained = NewDrainTcs();

        public void Push(string line)
        {
            Interlocked.Increment(ref _pending);
            Interlocked.Exchange(ref _drained, NewDrainTcs());
            _channel.Writer.TryWrite(new RawLogLine(DateTime.UtcNow, line));
        }

        public Task WaitForDrainAsync() => _drained.Task;

        public async IAsyncEnumerable<RawLogLine> SubscribeAsync(
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
        {
            while (await _channel.Reader.WaitToReadAsync(ct).ConfigureAwait(false))
            {
                while (_channel.Reader.TryRead(out var line))
                {
                    yield return line;
                    if (Interlocked.Decrement(ref _pending) == 0)
                        _drained.TrySetResult();
                }
            }
        }

        private static TaskCompletionSource NewDrainTcs() =>
            new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    private sealed class ManualTimeProvider : TimeProvider
    {
        private DateTimeOffset _now;
        public ManualTimeProvider(DateTime utcStart) =>
            _now = new DateTimeOffset(utcStart, TimeSpan.Zero);

        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(TimeSpan delta) => _now = _now.Add(delta);
    }

    private sealed class CapturingSink : IDiagnosticsSink
    {
        public List<(DiagnosticLevel Level, string Category, string Message)> Entries { get; } = new();
        public void Write(DiagnosticLevel level, string category, string message)
        {
            lock (Entries) Entries.Add((level, category, message));
        }
        public IReadOnlyList<DiagnosticEntry> Snapshot() => Array.Empty<DiagnosticEntry>();
        public event EventHandler<DiagnosticEntry>? EntryAdded { add { } remove { } }
    }
}
