using Arda.Abstractions.Logs;
using Arda.World.Player.Events;
using FluentAssertions;
using Mithril.GameState.Pins;
using Mithril.GameState.Tests.TestSupport;
using Xunit;

namespace Mithril.GameState.Tests.Pins;

/// <summary>
/// Arda domain-event-driven PlayerPinTracker tests. The tracker subscribes to
/// <see cref="MapPinAdded"/>, <see cref="MapPinRemoved"/>, and
/// <see cref="AreaChanged"/> on the <see cref="IDomainEventSubscriber"/> bus.
/// Tests drive it via <see cref="TestDomainEventBus"/> which publishes domain
/// events directly.
/// </summary>
public sealed class PlayerPinTrackerTests
{
    private static readonly DateTimeOffset Ts =
        new(2026, 5, 18, 10, 10, 3, TimeSpan.Zero);

    private static LogLineMetadata Meta(DateTimeOffset? ts = null) =>
        new(Timestamp: ts ?? Ts, ReadOn: ts ?? Ts, IsReplay: false);

    private static (PlayerPinTracker tracker, TestDomainEventBus bus) NewTracker()
    {
        var bus = new TestDomainEventBus();
        var tracker = new PlayerPinTracker(bus);
        tracker.Start();
        return (tracker, bus);
    }

    [Fact]
    public void Add_populates_current_area_pins_with_decoded_appearance()
    {
        var (svc, bus) = NewTracker();
        using var _ = svc;

        bus.Publish(new AreaChanged(null, "AreaSerbule", Meta()));
        bus.Publish(new MapPinAdded(1425.06, 2924.99, "South", Shape: 1, Color: 1, Meta()));

        svc.CurrentArea.Should().Be("AreaSerbule");
        var pin = svc.CurrentAreaPins.Should().ContainSingle().Subject;
        pin.Label.Should().Be("South");
        pin.X.Should().Be(1425.06);
        pin.Z.Should().Be(2924.99);
        pin.Shape.Should().Be(PinShape.Square);
        pin.Color.Should().Be(PinColor.Red);
        pin.Appearance.Should().Be("red square");
    }

    [Fact]
    public void Replay_burst_is_idempotent_no_duplicates_no_extra_events()
    {
        var (svc, bus) = NewTracker();
        using var _ = svc;

        bus.Publish(new AreaChanged(null, "AreaSerbule", Meta()));
        bus.Publish(new MapPinAdded(10, 20, "A", 0, 0, Meta()));
        bus.Publish(new MapPinAdded(30, 40, "B", 0, 0, Meta()));

        var seen = new List<PinSetChanged>();
        using var sub = svc.Subscribe(seen.Add);

        // Login/area-entry re-emits the whole set verbatim.
        bus.Publish(new MapPinAdded(10, 20, "A", 0, 0, Meta()));
        bus.Publish(new MapPinAdded(30, 40, "B", 0, 0, Meta()));

        svc.CurrentAreaPins.Should().HaveCount(2);
        // 1 Snapshot from Subscribe + 0 extra Adds (idempotent re-adds
        // produce no notification).
        seen.Count(n => n.Kind == PinSetChange.Added).Should().Be(0);
    }

    [Fact]
    public void Remove_deletes_by_coordinate()
    {
        var (svc, bus) = NewTracker();
        using var _ = svc;

        bus.Publish(new AreaChanged(null, "AreaSerbule", Meta()));
        bus.Publish(new MapPinAdded(10, 20, "A", 0, 0, Meta()));
        bus.Publish(new MapPinRemoved(10, 20, "A", Meta()));

        svc.CurrentAreaPins.Should().BeEmpty();
    }

    [Fact]
    public void Rename_is_remove_then_add_at_same_coord()
    {
        var (svc, bus) = NewTracker();
        using var _ = svc;

        bus.Publish(new AreaChanged(null, "AreaSerbule", Meta()));
        bus.Publish(new MapPinAdded(10, 20, "", 0, 0, Meta()));
        bus.Publish(new MapPinRemoved(10, 20, "", Meta()));
        bus.Publish(new MapPinAdded(10, 20, "Calib 1", 0, 0, Meta()));

        svc.CurrentAreaPins.Should().ContainSingle()
            .Which.Label.Should().Be("Calib 1");
    }

    [Fact]
    public void Area_change_swaps_the_set_and_raises_AreaChanged()
    {
        var (svc, bus) = NewTracker();
        using var _ = svc;

        bus.Publish(new AreaChanged(null, "AreaSerbule", Meta()));
        bus.Publish(new MapPinAdded(10, 20, "A", 0, 0, Meta()));

        var seen = new List<PinSetChanged>();
        using var sub = svc.Subscribe(seen.Add); // Snapshot replay first

        bus.Publish(new AreaChanged("AreaSerbule", "AreaEltibule", Meta()));

        svc.CurrentArea.Should().Be("AreaEltibule");
        svc.CurrentAreaPins.Should().BeEmpty();
        seen.Should().Contain(n => n.Kind == PinSetChange.AreaChanged && n.Area == "AreaEltibule");
    }

    [Fact]
    public void Subscribe_replays_snapshot_synchronously_then_delivers_live()
    {
        var (svc, bus) = NewTracker();
        using var _ = svc;

        bus.Publish(new AreaChanged(null, "AreaSerbule", Meta()));
        bus.Publish(new MapPinAdded(10, 20, "A", 0, 0, Meta()));

        var seen = new List<PinSetChanged>();
        using var sub = svc.Subscribe(seen.Add);
        seen.Should().ContainSingle()
            .Which.Should().Match<PinSetChanged>(n =>
                n.Kind == PinSetChange.Snapshot && n.Pins.Count == 1);

        bus.Publish(new MapPinAdded(30, 40, "B", 0, 0, Meta()));

        var added = seen.Last();
        added.Kind.Should().Be(PinSetChange.Added);
        added.Pin!.Label.Should().Be("B");
        added.ObservedAt.Offset.Should().Be(TimeSpan.Zero);
    }

    [Fact]
    public void Cross_pipe_ordering_AreaChanged_precedes_PinBurst_no_stale_drop()
    {
        var (svc, bus) = NewTracker();
        using var _ = svc;

        bus.Publish(new AreaChanged(null, "AreaSerbule", Meta()));
        bus.Publish(new MapPinAdded(100, 200, "OLD", 0, 0, Meta()));
        svc.CurrentArea.Should().Be("AreaSerbule");
        svc.CurrentAreaPins.Should().ContainSingle().Which.Label.Should().Be("OLD");

        bus.Publish(new AreaChanged("AreaSerbule", "AreaEltibule", Meta()));
        bus.Publish(new MapPinAdded(500, 600, "NEW", 0, 0, Meta()));

        svc.CurrentArea.Should().Be("AreaEltibule");
        svc.CurrentAreaPins.Should().ContainSingle().Which.Label.Should().Be("NEW");
    }

    [Fact]
    public void Subscribe_snapshot_stamp_is_last_event_timestamp_not_wall_clock()
    {
        // Principle 13 — world-event-driven paths must not leak wall clock.
        var areaTs = new DateTimeOffset(2020, 1, 2, 3, 4, 5, TimeSpan.Zero);
        var pinTs = areaTs.AddMinutes(1);

        var (svc, bus) = NewTracker();
        using var _ = svc;

        bus.Publish(new AreaChanged(null, "AreaSerbule", Meta(areaTs)));
        bus.Publish(new MapPinAdded(10, 20, "A", 0, 0, Meta(pinTs)));

        var seenA = new List<PinSetChanged>();
        using var subA = svc.Subscribe(seenA.Add);

        var seenB = new List<PinSetChanged>();
        using var subB = svc.Subscribe(seenB.Add);

        seenA.Should().ContainSingle()
            .Which.Kind.Should().Be(PinSetChange.Snapshot);
        seenB.Should().ContainSingle()
            .Which.Kind.Should().Be(PinSetChange.Snapshot);

        seenA[0].ObservedAt.Should().Be(pinTs,
            "the Snapshot stamp must come from the most-recent event the tracker applied, not wall clock");
        seenB[0].ObservedAt.Should().Be(seenA[0].ObservedAt,
            "late subscribers observing the same tracker state must see identical snapshot timestamps");
    }

    [Fact]
    public void Area_change_notification_uses_event_timestamp_not_wall_clock()
    {
        var firstAreaTs = new DateTimeOffset(2020, 1, 2, 3, 4, 5, TimeSpan.Zero);
        var secondAreaTs = firstAreaTs.AddMinutes(7);

        var (svc, bus) = NewTracker();
        using var _ = svc;

        var seen = new List<PinSetChanged>();
        using var sub = svc.Subscribe(seen.Add);

        bus.Publish(new AreaChanged(null, "AreaSerbule", Meta(firstAreaTs)));
        bus.Publish(new AreaChanged("AreaSerbule", "AreaEltibule", Meta(secondAreaTs)));

        var areaChanges = seen.Where(n => n.Kind == PinSetChange.AreaChanged).ToList();
        areaChanges.Should().HaveCount(2,
            "one AreaChanged per event (initial + the second transition)");
        areaChanges[0].ObservedAt.Should().Be(firstAreaTs,
            "AreaChanged note must stamp with the triggering event's timestamp, not wall clock");
        areaChanges[1].ObservedAt.Should().Be(secondAreaTs);
    }

}
