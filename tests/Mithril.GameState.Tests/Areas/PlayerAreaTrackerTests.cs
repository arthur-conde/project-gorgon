using FluentAssertions;
using Mithril.GameState.Areas;
using Mithril.GameState.Areas.Parsing;
using Mithril.WorldSim;
using Xunit;

namespace Mithril.GameState.Tests.Areas;

/// <summary>
/// Unit tests for the folder + three-surface event behaviour of
/// <see cref="PlayerAreaTracker"/> after the #775 conversion. The producer-side
/// behaviour (reverse-scan pre-warm helper, L1 SystemSignal forwarding,
/// ReachedLive flip) lives in
/// <see cref="Producers.AreaLoadingFrameProducerTests"/>; the end-to-end
/// replay-determinism integration lives in
/// <see cref="PlayerAreaWorldIntegrationTests"/>.
/// </summary>
public sealed class PlayerAreaTrackerTests
{
    private static PlayerAreaTracker Build() => new(new AreaTransitionParser());

    private static readonly IWorldClock UnusedClock = new StubClock();

    [Fact]
    public void Initial_state_is_null()
    {
        Build().CurrentArea.Should().BeNull();
    }

    // ── Subscribe: replay-on-attach + live callbacks ──────────────────────

    [Fact]
    public void Subscribe_replays_a_Snapshot_with_current_area_on_attach()
    {
        var tracker = Build();
        tracker.Observe("LOADING LEVEL AreaSerbule",
            new DateTime(2026, 5, 23, 14, 11, 10, DateTimeKind.Utc));

        var observed = new List<PlayerAreaChanged>();
        using var _sub = tracker.Subscribe(observed.Add);

        observed.Should().ContainSingle()
            .Which.Should().BeEquivalentTo(new PlayerAreaChanged(
                PlayerAreaChangeKind.Snapshot,
                Previous: null,
                Current: "AreaSerbule",
                At: new DateTimeOffset(2026, 5, 23, 14, 11, 10, TimeSpan.Zero)));
    }

    [Fact]
    public void Subscribe_replays_a_Snapshot_with_null_area_before_any_observation()
    {
        var tracker = Build();
        var observed = new List<PlayerAreaChanged>();
        using var _sub = tracker.Subscribe(observed.Add);

        observed.Should().ContainSingle()
            .Which.Should().BeEquivalentTo(new PlayerAreaChanged(
                PlayerAreaChangeKind.Snapshot,
                Previous: null,
                Current: null,
                At: DateTimeOffset.MinValue));
    }

    [Fact]
    public void Subscribe_fires_Changed_notifications_for_subsequent_transitions()
    {
        var tracker = Build();
        var observed = new List<PlayerAreaChanged>();
        using var _sub = tracker.Subscribe(observed.Add);
        observed.Clear(); // discard the Snapshot replay

        var ts = new DateTime(2026, 5, 23, 14, 11, 10, DateTimeKind.Utc);
        tracker.Observe("LOADING LEVEL AreaSerbule", ts);

        observed.Should().ContainSingle()
            .Which.Should().BeEquivalentTo(new PlayerAreaChanged(
                PlayerAreaChangeKind.Changed,
                Previous: null,
                Current: "AreaSerbule",
                At: new DateTimeOffset(ts, TimeSpan.Zero)));
    }

    [Fact]
    public void Subscribe_dispose_stops_subsequent_callbacks()
    {
        var tracker = Build();
        var observed = new List<PlayerAreaChanged>();
        var sub = tracker.Subscribe(observed.Add);
        observed.Clear();

        sub.Dispose();
        tracker.Observe("LOADING LEVEL AreaSerbule", DateTime.UtcNow);

        observed.Should().BeEmpty();
    }

    // ── Legacy Observe path (Legolas + Gandalf bridges still call this) ───

    [Fact]
    public void Observe_real_area_sets_CurrentArea()
    {
        var tracker = Build();
        tracker.Observe("LOADING LEVEL AreaSerbule", DateTime.UtcNow);
        tracker.CurrentArea.Should().Be("AreaSerbule");
    }

    [Fact]
    public void Observe_portal_transition_replaces_area()
    {
        var tracker = Build();
        tracker.Observe("LOADING LEVEL AreaSerbule", DateTime.UtcNow);
        tracker.Observe("LOADING LEVEL AreaEltibule", DateTime.UtcNow);
        tracker.CurrentArea.Should().Be("AreaEltibule");
    }

    [Fact]
    public void Observe_ChooseCharacter_clears_area()
    {
        var tracker = Build();
        tracker.Observe("LOADING LEVEL AreaSerbule", DateTime.UtcNow);
        tracker.Observe("LOADING LEVEL ChooseCharacter", DateTime.UtcNow);
        tracker.CurrentArea.Should().BeNull();
    }

    [Fact]
    public void Observe_disconnect_clears_area()
    {
        var tracker = Build();
        tracker.Observe("LOADING LEVEL AreaSerbule", DateTime.UtcNow);
        tracker.Observe("LOADING LEVEL ", DateTime.UtcNow);
        tracker.CurrentArea.Should().BeNull();
    }

    [Fact]
    public void Observe_unrelated_line_is_noop()
    {
        var tracker = Build();
        tracker.Observe("LOADING LEVEL AreaSerbule", DateTime.UtcNow);
        tracker.Observe("LocalPlayer: ProcessAddItem(Apple(1), -1, True)", DateTime.UtcNow);
        tracker.Observe("ProcessChat(General, \"hi\")", DateTime.UtcNow);
        tracker.CurrentArea.Should().Be("AreaSerbule");
    }

    [Fact]
    public void Observe_re_emitting_same_area_does_not_fire_Changed_callback()
    {
        var tracker = Build();
        var observed = new List<PlayerAreaChanged>();
        using var _sub = tracker.Subscribe(observed.Add);
        observed.Clear(); // discard the Snapshot replay

        tracker.Observe("LOADING LEVEL AreaSerbule", DateTime.UtcNow);
        tracker.Observe("LOADING LEVEL AreaSerbule", DateTime.UtcNow);

        observed.Should().ContainSingle()
            .Which.Kind.Should().Be(PlayerAreaChangeKind.Changed);
    }

    // ── Folder Apply path (world-driven) ──────────────────────────────────

    [Fact]
    public void Apply_first_frame_updates_state_and_emits_Changed()
    {
        var tracker = Build();
        var ts = new DateTimeOffset(2026, 5, 23, 14, 30, 0, TimeSpan.Zero);

        var changes = tracker.Apply(
            new Frame<AreaLoadingFrame>(ts, new AreaLoadingFrame("AreaSerbule")),
            UnusedClock);

        tracker.CurrentArea.Should().Be("AreaSerbule");
        changes.Should().ContainSingle()
            .Which.Should().BeEquivalentTo(new PlayerAreaChanged(
                PlayerAreaChangeKind.Changed, null, "AreaSerbule", ts));
    }

    [Fact]
    public void Apply_unchanged_area_returns_empty_change_list()
    {
        var tracker = Build();
        var ts1 = new DateTimeOffset(2026, 5, 23, 14, 30, 0, TimeSpan.Zero);
        var ts2 = new DateTimeOffset(2026, 5, 23, 14, 31, 0, TimeSpan.Zero);

        tracker.Apply(new Frame<AreaLoadingFrame>(ts1, new AreaLoadingFrame("AreaSerbule")), UnusedClock);
        var changes = tracker.Apply(
            new Frame<AreaLoadingFrame>(ts2, new AreaLoadingFrame("AreaSerbule")),
            UnusedClock);

        changes.Should().BeEmpty();
    }

    [Fact]
    public void Apply_carries_previous_area_in_Changed_event()
    {
        var tracker = Build();
        var ts1 = new DateTimeOffset(2026, 5, 23, 14, 30, 0, TimeSpan.Zero);
        var ts2 = new DateTimeOffset(2026, 5, 23, 14, 31, 0, TimeSpan.Zero);

        tracker.Apply(new Frame<AreaLoadingFrame>(ts1, new AreaLoadingFrame("AreaSerbule")), UnusedClock);
        var changes = tracker.Apply(
            new Frame<AreaLoadingFrame>(ts2, new AreaLoadingFrame("AreaEltibule")),
            UnusedClock);

        changes.Should().ContainSingle()
            .Which.Should().BeEquivalentTo(new PlayerAreaChanged(
                PlayerAreaChangeKind.Changed, "AreaSerbule", "AreaEltibule", ts2));
    }

    [Fact]
    public void Apply_null_area_signals_character_select_or_disconnect()
    {
        var tracker = Build();
        var ts1 = new DateTimeOffset(2026, 5, 23, 14, 30, 0, TimeSpan.Zero);
        var ts2 = new DateTimeOffset(2026, 5, 23, 14, 35, 0, TimeSpan.Zero);

        tracker.Apply(new Frame<AreaLoadingFrame>(ts1, new AreaLoadingFrame("AreaSerbule")), UnusedClock);
        var changes = tracker.Apply(
            new Frame<AreaLoadingFrame>(ts2, new AreaLoadingFrame(null)),
            UnusedClock);

        tracker.CurrentArea.Should().BeNull();
        changes.Should().ContainSingle()
            .Which.Should().BeEquivalentTo(new PlayerAreaChanged(
                PlayerAreaChangeKind.Changed, "AreaSerbule", null, ts2));
    }

    [Fact]
    public void Apply_also_fires_legacy_Subscribe_handlers_with_Changed_kind()
    {
        var tracker = Build();
        var observed = new List<PlayerAreaChanged>();
        using var _sub = tracker.Subscribe(observed.Add);
        observed.Clear();

        var ts = new DateTimeOffset(2026, 5, 23, 14, 30, 0, TimeSpan.Zero);
        tracker.Apply(new Frame<AreaLoadingFrame>(ts, new AreaLoadingFrame("AreaSerbule")), UnusedClock);

        observed.Should().ContainSingle()
            .Which.Should().BeEquivalentTo(new PlayerAreaChanged(
                PlayerAreaChangeKind.Changed, null, "AreaSerbule", ts));
    }

    // ── Cross-path idempotency (double-feed during migration window) ──────

    [Fact]
    public void Apply_then_Observe_same_area_is_idempotent()
    {
        // The producer's frame and the legacy bridge's Observe push-in both
        // land at the same area key on a zone transition. Last-writer-wins
        // — neither path fires a redundant callback past the first.
        var tracker = Build();
        var observed = new List<PlayerAreaChanged>();
        using var _sub = tracker.Subscribe(observed.Add);
        observed.Clear();

        var ts = new DateTimeOffset(2026, 5, 23, 14, 30, 0, TimeSpan.Zero);
        tracker.Apply(new Frame<AreaLoadingFrame>(ts, new AreaLoadingFrame("AreaSerbule")), UnusedClock);
        tracker.Observe("LOADING LEVEL AreaSerbule", ts.UtcDateTime);

        observed.Should().ContainSingle()
            .Which.Kind.Should().Be(PlayerAreaChangeKind.Changed);
        tracker.CurrentArea.Should().Be("AreaSerbule");
    }

    private sealed class StubClock : IWorldClock
    {
        public DateTimeOffset Now => DateTimeOffset.MinValue;
        public long Frame => 0;
        public WorldMode Mode => WorldMode.Live;
    }
}
