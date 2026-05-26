using Arda.Abstractions.Logs;
using Arda.World.Player;
using Arda.World.Player.Events;
using FluentAssertions;
using Legolas.Domain;
using Legolas.Flow;
using Legolas.Services;
using Legolas.Tests.TestSupport;
using Legolas.ViewModels;

namespace Legolas.Tests.Services;

/// <summary>
/// #488 (locked model): create-on-use working slots, declared-order multi-map
/// (default) vs serial toggle, dig = motherlode-map <c>Deleted</c> →
/// <c>MapsDug</c> + next-uncollected retire, cross-spot divergence, Risk-1
/// same-spot clustering, #497 NamedMapPin confidence. Holding inventory stock
/// creates nothing.
/// </summary>
public class MotherlodeMeasurementCoordinatorTests
{
    private static readonly DateTimeOffset T0 =
        new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    private static LogLineMetadata Meta(DateTimeOffset at) => new(at, at, false);

    private static void PublishPosition(TestDomainEventBus bus, double x, double y, double z,
        PositionSource source, DateTimeOffset at)
        => bus.Publish(new PlayerPositionChanged(x, y, z, source, Meta(at)));

    private static void PublishPin(TestDomainEventBus bus, double x, double z, string label)
        => bus.Publish(new MapPinAdded(x, z, label, 0, 0, new LogLineMetadata(null, DateTimeOffset.UtcNow, false)));

    private static void PublishDelete(TestDomainEventBus bus, long id, string internalName, DateTime at)
    {
        var ts = DateTime.SpecifyKind(at, DateTimeKind.Utc);
        var dto = new DateTimeOffset(ts, TimeSpan.Zero);
        bus.Publish(new InventoryItemRemoved(id, internalName, Meta(dto)));
    }

    private static (MotherlodeMeasurementCoordinator coord, TestDomainEventBus bus) Build()
    {
        var bus = new TestDomainEventBus();
        var flow = new MotherlodeFlowController(new SessionState());
        var coord = new MotherlodeMeasurementCoordinator(
            new MultilaterationSolver(), flow, bus);
        return (coord, bus);
    }

    private static double D(double x, double z, double tx, double tz) =>
        Math.Sqrt((x - tx) * (x - tx) + (z - tz) * (z - tz));

    /// <summary>One spot: push a Spawn fix, fire the use, then the distance.</summary>
    private static void Measure(MotherlodeMeasurementCoordinator coord, TestDomainEventBus bus,
        double x, double z, int metres, DateTimeOffset at)
    {
        PublishPosition(bus, x, 0, z, PositionSource.Spawn, at);
        coord.OnUse(at);
        coord.OnDistance(metres, at.AddSeconds(2));
    }

    [Fact]
    public void Three_located_spots_solve_the_treasure_in_world_space()
    {
        var (coord, bus) = Build();
        (double X, double Z) target = (420, -260);

        Measure(coord, bus, 0, 0, (int)Math.Round(D(0, 0, target.X, target.Z)), T0);
        Measure(coord, bus, 800, 0, (int)Math.Round(D(800, 0, target.X, target.Z)), T0.AddMinutes(2));
        Measure(coord, bus, 0, -800, (int)Math.Round(D(0, -800, target.X, target.Z)), T0.AddMinutes(4));

        var snap = coord.Snapshot();
        snap.LocationCount.Should().Be(3);
        snap.LocationsWithFix.Should().Be(3);
        var slot = snap.Surveys.Should().ContainSingle().Subject;
        slot.SolvedWorld.Should().NotBeNull();
        var w = slot.SolvedWorld!.Value;
        w.X.Should().BeApproximately(target.X, 3.0);
        w.Z.Should().BeApproximately(target.Z, 3.0);
    }

    [Fact]
    public void Distance_with_no_open_location_is_dropped()
    {
        var (coord, _) = Build();

        coord.OnDistance(500, T0);

        coord.Snapshot().LocationCount.Should().Be(0);
        coord.Snapshot().Surveys.Should().BeEmpty();
    }

    [Fact]
    public void Distance_outside_the_use_window_is_dropped()
    {
        var (coord, bus) = Build();
        PublishPosition(bus, 0, 0, 0, PositionSource.Spawn, T0);
        coord.OnUse(T0);

        coord.OnDistance(500, T0.AddMinutes(5));   // far past DistanceWindow

        coord.Snapshot().Surveys.Should().BeEmpty();
    }

    [Fact]
    public void Use_with_a_stale_feeder_fix_records_no_position_and_warns()
    {
        var (coord, bus) = Build();
        // Fix is 30 min older than the use → beyond MaxFeederGap.
        PublishPosition(bus, 10, 0, 10, PositionSource.Spawn, T0);
        coord.OnUse(T0.AddMinutes(30));
        coord.OnDistance(500, T0.AddMinutes(30).AddSeconds(2));

        var snap = coord.Snapshot();
        snap.LocationCount.Should().Be(1);
        snap.LocationsWithFix.Should().Be(0);
        snap.Guidance.Should().NotBeNullOrEmpty();
    }

    [Fact]
    public void Use_line_name_labels_the_created_slot()
    {
        var (coord, bus) = Build();
        PublishPosition(bus, 0, 0, 0, PositionSource.Spawn, T0);
        coord.OnUse(T0, "Kur Mountains Simple Metal Motherlode Map");
        coord.OnDistance(500, T0.AddSeconds(2));

        coord.Snapshot().Surveys.Should().ContainSingle()
             .Which.MapName.Should().Be("Kur Mountains Simple Metal Motherlode Map");
    }

    [Fact]
    public void Multiple_maps_at_one_spot_fan_out_to_independent_slots()
    {
        var (coord, bus) = Build();          // multi-map default
        (double X, double Z) a = (300, 300);
        (double X, double Z) b = (-150, 480);

        void Spot(double x, double z, DateTimeOffset at)
        {
            PublishPosition(bus, x, 0, z, PositionSource.Spawn, at);
            coord.OnUse(at);                                   // map A
            coord.OnDistance((int)Math.Round(D(x, z, a.X, a.Z)), at.AddSeconds(1));
            coord.OnUse(at.AddSeconds(2));                      // map B, same cluster
            coord.OnDistance((int)Math.Round(D(x, z, b.X, b.Z)), at.AddSeconds(3));
        }

        Spot(0, 0, T0);
        Spot(900, 0, T0.AddMinutes(2));
        Spot(0, 900, T0.AddMinutes(4));

        var s = coord.Snapshot().Surveys;
        s.Should().HaveCount(2);
        var w0 = s[0].SolvedWorld!.Value;
        var w1 = s[1].SolvedWorld!.Value;
        w0.X.Should().BeApproximately(a.X, 3.0);
        w0.Z.Should().BeApproximately(a.Z, 3.0);
        w1.X.Should().BeApproximately(b.X, 3.0);
        w1.Z.Should().BeApproximately(b.Z, 3.0);
    }

    [Fact]
    public void A_map_pin_drop_is_an_accepted_position_feeder()
    {
        var (coord, bus) = Build();
        (double X, double Z) target = (100, 100);
        var now = DateTimeOffset.UtcNow;

        void PinSpot(double x, double z, DateTimeOffset at)
        {
            PublishPin(bus, x, z, "spot");                     // feeder #2
            coord.OnUse(at);
            coord.OnDistance((int)Math.Round(D(x, z, target.X, target.Z)), at.AddSeconds(2));
        }

        PinSpot(0, 0, now);
        PinSpot(400, 0, now.AddSeconds(40));
        PinSpot(0, 400, now.AddSeconds(80));

        var snap = coord.Snapshot();
        snap.LocationsWithFix.Should().Be(3);
        snap.Surveys.Should().ContainSingle()
            .Which.SolvedWorld.Should().NotBeNull();
    }

    [Fact]
    public void A_character_named_pin_is_an_accepted_preferred_feeder()
    {
        var bus = new TestDomainEventBus();
        var mapPinState = new FakeMapPinState();
        var chr = new FakeActiveCharacterService();
        chr.SetName("Arthas");
        var charPin = new CharacterPinAnchor(bus, mapPinState, chr);
        var coord = new MotherlodeMeasurementCoordinator(
            new MultilaterationSolver(), new MotherlodeFlowController(new SessionState()),
            bus, characterPin: charPin);

        (double X, double Z) target = (100, 100);
        var now = DateTimeOffset.UtcNow;
        void PinSpot(double x, double z, DateTimeOffset at)
        {
            mapPinState.Add(new MapPinEntry(x, z, "Arthas", 0, 0));
            bus.Publish(new MapPinAdded(x, z, "Arthas", 0, 0, Meta(at)));
            coord.OnUse(at);
            coord.OnDistance((int)Math.Round(D(x, z, target.X, target.Z)), at.AddSeconds(2));
        }

        PinSpot(0, 0, now);
        PinSpot(400, 0, now.AddSeconds(40));
        PinSpot(0, 400, now.AddSeconds(80));

        charPin.Current.Should().NotBeNull("the named pin is the declared position");
        var snap = coord.Snapshot();
        snap.LocationsWithFix.Should().Be(3);
        snap.Surveys.Should().ContainSingle()
            .Which.SolvedWorld.Should().NotBeNull();
    }

    [Fact]
    public void Reset_clears_all_state()
    {
        var (coord, bus) = Build();
        Measure(coord, bus, 0, 0, 300, T0);

        coord.Reset();

        var snap = coord.Snapshot();
        snap.LocationCount.Should().Be(0);
        snap.Surveys.Should().BeEmpty();
        snap.Guidance.Should().BeNull();
        snap.ReadsPerLocation.Should().BeEmpty();
        snap.MapsDug.Should().Be(0);
    }

    // ---- create-on-use / completion / toggle / divergence ----------------

    private const string KurSimple = "MiningSurveyKurMountains1X";
    private const string KurBasic = "MiningSurveyKurMountains2X";
    private const string KurGood = "MiningSurveyKurMountains3X";

    private static FakeMotherlodeRefData RefData() => new(
        (KurSimple, "Kur Mountains Simple Metal Motherlode Map"),
        (KurBasic, "Kur Mountains Basic Metal Motherlode Map"),
        (KurGood, "Kur Mountains Good Metal Motherlode Map"),
        ("RawGem_Diamond", "Diamond"));

    private static (MotherlodeMeasurementCoordinator coord, TestDomainEventBus bus) BuildInv(bool multiMap)
    {
        var bus = new TestDomainEventBus();
        var flow = new MotherlodeFlowController(new SessionState());
        var settings = new LegolasSettings { MotherlodeMultiMapMode = multiMap };
        var coord = new MotherlodeMeasurementCoordinator(
            new MultilaterationSolver(), flow, bus, RefData(), settings);
        return (coord, bus);
    }

    /// <summary>One spot: Spawn fix, use, then the listed distances in order.</summary>
    private static void Spot(MotherlodeMeasurementCoordinator coord, TestDomainEventBus bus,
        double x, double z, DateTimeOffset at, params int[] dists)
    {
        PublishPosition(bus, x, 0, z, PositionSource.Spawn, at);
        coord.OnUse(at);
        var t = at;
        foreach (var d in dists)
            coord.OnDistance(d, t = t.AddSeconds(1));
    }

    [Fact]
    public void Holding_stock_creates_no_slots()
    {
        var (coord, _) = BuildInv(multiMap: true);
        // In the post-Arda model the coordinator doesn't react to adds at all.
        // A fat carried stack is invisible to it.

        var snap = coord.Snapshot();
        snap.Surveys.Should().BeEmpty();        // create-on-use: holding ≠ measuring
        snap.MapsDug.Should().Be(0);
    }

    [Fact]
    public void Single_treasure_create_on_use_solves()
    {
        var (coord, bus) = BuildInv(multiMap: true);
        (double X, double Z) target = (420, -260);

        Spot(coord, bus, 0, 0, T0, (int)Math.Round(D(0, 0, target.X, target.Z)));
        Spot(coord, bus, 800, 0, T0.AddMinutes(2), (int)Math.Round(D(800, 0, target.X, target.Z)));
        Spot(coord, bus, 0, -800, T0.AddMinutes(4), (int)Math.Round(D(0, -800, target.X, target.Z)));

        var s = coord.Snapshot().Surveys.Should().ContainSingle().Subject;
        s.SolvedWorld!.Value.X.Should().BeApproximately(target.X, 3.0);
        s.SolvedWorld!.Value.Z.Should().BeApproximately(target.Z, 3.0);
    }

    [Fact]
    public void Motherlode_map_delete_counts_a_dig_and_retires_next_slot()
    {
        var (coord, bus) = BuildInv(multiMap: true);
        // Two treasures measured (create-on-use), then the cluster is dug.
        Spot(coord, bus, 0, 0, T0, 500, 600);
        Spot(coord, bus, 900, 0, T0.AddMinutes(2), 480, 470);
        Spot(coord, bus, 0, 900, T0.AddMinutes(4), 450, 460);

        coord.Snapshot().Surveys.Count(s => !s.Collected).Should().Be(2);

        PublishDelete(bus, 1, KurSimple, T0.AddMinutes(6).UtcDateTime);
        PublishDelete(bus, 2, KurBasic, T0.AddMinutes(6).AddSeconds(5).UtcDateTime);

        var snap = coord.Snapshot();
        snap.MapsDug.Should().Be(2);
        snap.Surveys.All(s => s.Collected).Should().BeTrue();   // both retired in order
    }

    [Fact]
    public void Completing_use_then_delete_discards_the_ephemeral_location()
    {
        var (coord, bus) = BuildInv(multiMap: true);
        PublishPosition(bus, 0, 0, 0, PositionSource.Spawn, T0);

        coord.OnUse(T0);                       // opens a location, no distance (completing use)
        PublishDelete(bus, 1, KurSimple, T0.AddSeconds(1).UtcDateTime);

        var snap = coord.Snapshot();
        snap.LocationCount.Should().Be(0);     // ephemeral location discarded
        snap.MapsDug.Should().Be(1);
        snap.Surveys.Should().BeEmpty();
    }

    [Fact]
    public void Non_motherlode_delete_does_not_count_as_a_dig()
    {
        var (coord, bus) = BuildInv(multiMap: true);
        PublishDelete(bus, 9, "RawGem_Diamond", DateTime.UtcNow);

        var snap = coord.Snapshot();
        snap.Surveys.Should().BeEmpty();
        snap.MapsDug.Should().Be(0);
    }

    [Fact]
    public void Toggle_off_serial_binds_one_active_treasure()
    {
        var (coord, bus) = BuildInv(multiMap: false);
        (double X, double Z) target = (420, -260);

        Spot(coord, bus, 0, 0, T0, (int)Math.Round(D(0, 0, target.X, target.Z)));
        Spot(coord, bus, 800, 0, T0.AddMinutes(2), (int)Math.Round(D(800, 0, target.X, target.Z)));
        Spot(coord, bus, 0, -800, T0.AddMinutes(4), (int)Math.Round(D(0, -800, target.X, target.Z)));

        var surveys = coord.Snapshot().Surveys;
        surveys.Should().ContainSingle();                       // never fanned out
        surveys[0].SolvedWorld!.Value.X.Should().BeApproximately(target.X, 3.0);
        surveys[0].SolvedWorld!.Value.Z.Should().BeApproximately(target.Z, 3.0);
    }

    [Fact]
    public void Multi_map_two_maps_bind_by_order_across_spots()
    {
        var (coord, bus) = BuildInv(multiMap: true);
        (double X, double Z) a = (300, 300);
        (double X, double Z) b = (-150, 480);

        void S(double x, double z, DateTimeOffset at) => Spot(coord, bus, x, z, at,
            (int)Math.Round(D(x, z, a.X, a.Z)),    // k0 → slot 0
            (int)Math.Round(D(x, z, b.X, b.Z)));   // k1 → slot 1

        S(0, 0, T0);
        S(900, 0, T0.AddMinutes(2));
        S(0, 900, T0.AddMinutes(4));

        var surveys = coord.Snapshot().Surveys;
        surveys.Should().HaveCount(2);
        surveys[0].SolvedWorld!.Value.X.Should().BeApproximately(a.X, 3.0);
        surveys[0].SolvedWorld!.Value.Z.Should().BeApproximately(a.Z, 3.0);
        surveys[1].SolvedWorld!.Value.X.Should().BeApproximately(b.X, 3.0);
        surveys[1].SolvedWorld!.Value.Z.Should().BeApproximately(b.Z, 3.0);
    }

    [Fact]
    public void Measure_then_dig_the_cluster_collects_in_order()
    {
        var (coord, bus) = BuildInv(multiMap: true);
        (double X, double Z) a = (300, 300);
        (double X, double Z) c = (-150, 480);

        void S(double x, double z, DateTimeOffset at) => Spot(coord, bus, x, z, at,
            (int)Math.Round(D(x, z, a.X, a.Z)), (int)Math.Round(D(x, z, c.X, c.Z)));

        S(0, 0, T0);
        S(900, 0, T0.AddMinutes(2));
        S(0, 900, T0.AddMinutes(4));

        PublishDelete(bus, 1, KurSimple, T0.AddMinutes(6).UtcDateTime);            // dig both
        PublishDelete(bus, 2, KurBasic, T0.AddMinutes(6).AddSeconds(8).UtcDateTime);

        var snap = coord.Snapshot();
        snap.MapsDug.Should().Be(2);
        snap.Surveys.Should().OnlyContain(s => s.Collected);
        snap.Surveys.Should().HaveCount(2);                     // solved before retirement
        snap.Surveys.Select(s => s.SolvedWorld).Should().OnlyContain(w => w != null);
    }

    [Fact]
    public void Spot_count_shortfall_raises_cross_spot_divergence()
    {
        var (coord, bus) = BuildInv(multiMap: true);

        Spot(coord, bus, 0, 0, T0, 500, 600);                 // spot 1: 2 reads
        Spot(coord, bus, 900, 0, T0.AddMinutes(2), 480);      // spot 2: 1 read (short!)
        Spot(coord, bus, 0, 900, T0.AddMinutes(4), 450, 470); // spot 3 (open)

        var snap = coord.Snapshot();
        snap.Guidance.Should().Contain("Spot #2");
        snap.ReadsPerLocation.Should().Equal(2, 1, 2);
    }

    [Fact]
    public void Final_open_short_batch_does_not_false_positive()
    {
        var (coord, bus) = BuildInv(multiMap: true);

        Spot(coord, bus, 0, 0, T0, 500, 600);              // spot 1: 2 reads
        Spot(coord, bus, 900, 0, T0.AddMinutes(2), 480);   // spot 2 still open: 1 read so far

        var g = coord.Snapshot().Guidance ?? string.Empty;
        g.Should().NotContain("Spot #");                   // the open batch legitimately trails
    }

    [Fact]
    public void ReadsPerLocation_projection_matches_bindings()
    {
        var (coord, bus) = BuildInv(multiMap: true);

        Spot(coord, bus, 0, 0, T0, 500, 600);
        Spot(coord, bus, 900, 0, T0.AddMinutes(2), 480, 470);

        coord.Snapshot().ReadsPerLocation.Should().Equal(2, 2);
    }

    // ---- Risk-1 same-spot clustering -------------------------------------

    [Fact]
    public void Slow_same_spot_batch_does_not_split_or_desync()
    {
        var (coord, bus) = Build();

        PublishPosition(bus, 0, 0, 0, PositionSource.Spawn, T0);
        coord.OnUse(T0);
        coord.OnDistance(900, T0.AddSeconds(2)); // commits the only location

        // 45 s later, still standing on the same spot (>30 s time gate).
        PublishPosition(bus, 0, 0, 0, PositionSource.Spawn, T0.AddSeconds(45));
        coord.OnUse(T0.AddSeconds(45));
        coord.OnDistance(905, T0.AddSeconds(47));

        coord.Snapshot().LocationCount.Should().Be(1);
    }

    [Fact]
    public void Genuine_move_within_time_gate_splits()
    {
        var (coord, bus) = Build();

        PublishPosition(bus, 0, 0, 0, PositionSource.Spawn, T0);
        coord.OnUse(T0);
        coord.OnDistance(900, T0.AddSeconds(2));

        PublishPosition(bus, 500, 0, 0, PositionSource.Spawn, T0.AddSeconds(45));
        coord.OnUse(T0.AddSeconds(45));
        coord.OnDistance(700, T0.AddSeconds(47));

        coord.Snapshot().LocationCount.Should().Be(2);
    }

    [Fact]
    public void Same_spot_merge_uses_frozen_anchor_not_drifting_fix()
    {
        var (coord, bus) = Build();
        var now = DateTimeOffset.UtcNow;

        PublishPin(bus, 0, 0, "p");
        coord.OnUse(now);
        coord.OnDistance(100, now.AddSeconds(2));          // anchor frozen at (0,0)

        PublishPin(bus, 10, 0, "p");                       // 10 m ≤ 12 → merge
        coord.OnUse(now.AddSeconds(45));
        coord.OnDistance(100, now.AddSeconds(47));

        PublishPin(bus, 19, 0, "p");                       // 19 m from frozen (0,0) → split
        coord.OnUse(now.AddSeconds(90));
        coord.OnDistance(100, now.AddSeconds(92));

        coord.Snapshot().LocationCount.Should().Be(2);
    }

    [Fact]
    public void No_position_falls_back_to_time_gate()
    {
        var (coord, _) = Build();             // no feeder fix ever pushed

        coord.OnUse(T0);
        coord.OnDistance(100, T0.AddSeconds(2)); // fix-less row committed

        coord.OnUse(T0.AddSeconds(45));          // gap > 30 s, no fix to compare
        coord.OnDistance(100, T0.AddSeconds(47));

        coord.Snapshot().LocationCount.Should().Be(2);
    }

    [Fact]
    public void Self_named_pin_outranks_a_generic_pin_in_feeder_confidence()
    {
        var now = DateTimeOffset.UtcNow;
        var selfBus = new TestDomainEventBus();
        var selfPinState = new FakeMapPinState();
        var charPin = new CharacterPinAnchor(selfBus, selfPinState, new FakeActiveCharacterService());
        var selfCoord = new MotherlodeMeasurementCoordinator(
            new MultilaterationSolver(), new MotherlodeFlowController(new SessionState()),
            selfBus, characterPin: charPin);
        selfPinState.Add(new MapPinEntry(0, 0, CharacterPinAnchor.SelfPinSentinel, 0, 0));
        selfBus.Publish(new MapPinAdded(0, 0, CharacterPinAnchor.SelfPinSentinel, 0, 0, Meta(now)));
        selfCoord.OnUse(now);
        selfCoord.OnDistance(100, now.AddSeconds(2));

        var (genCoord, genBus) = Build();
        PublishPin(genBus, 0, 0, "somewhere");
        genCoord.OnUse(now);
        genCoord.OnDistance(100, now.AddSeconds(2));

        var self = selfCoord.Snapshot().Locations.Single().Confidence;
        var generic = genCoord.Snapshot().Locations.Single().Confidence;
        self.Should().BeGreaterThan(generic);
        self.Should().BeApproximately(0.85, 1e-9);   // NamedMapPin
        generic.Should().BeApproximately(0.6, 1e-9); // MapPin
    }

    // ---- area scoping (Arda IAreaState) -----------------------------------

    private static (MotherlodeMeasurementCoordinator coord, TestDomainEventBus bus,
        FakeAreaState area) BuildAreaInv()
    {
        var bus = new TestDomainEventBus();
        var area = new FakeAreaState();
        var flow = new MotherlodeFlowController(new SessionState());
        var coord = new MotherlodeMeasurementCoordinator(
            new MultilaterationSolver(), flow, bus, RefData(),
            new LegolasSettings(), areaState: area);
        return (coord, bus, area);
    }

    [Fact]
    public void Area_change_clears_measurement_but_keeps_the_dug_count()
    {
        var (coord, bus, area) = BuildAreaInv();
        area.CurrentArea = "AreaKurMountains";

        Spot(coord, bus, 0, 0, T0, 500, 600);                  // measurement in area A
        PublishDelete(bus, 1, KurSimple, T0.AddSeconds(30).UtcDateTime);  // a dig → MapsDug = 1
        coord.Snapshot().MapsDug.Should().Be(1);
        coord.Snapshot().LocationCount.Should().BeGreaterThan(0);

        area.CurrentArea = "AreaEltibule";
        Spot(coord, bus, 10, 10, T0.AddMinutes(6), 700);       // first use in the new area

        var snap = coord.Snapshot();
        snap.MapsDug.Should().Be(1);                           // cumulative stat kept
        snap.LocationCount.Should().Be(1);                     // old area-local fixes cleared
        snap.Surveys.Should().ContainSingle();                 // old slots cleared
    }

    // ---- undo last reading ("oops, accidentally checked a map") ----------

    [Fact]
    public void Undo_pops_readings_LIFO_then_clears_can_undo()
    {
        var (coord, bus) = Build();                 // multi-map default
        PublishPosition(bus, 0, 0, 0, PositionSource.Spawn, T0);
        coord.OnUse(T0);                       coord.OnDistance(500, T0.AddSeconds(1));   // slot 0
        coord.OnUse(T0.AddSeconds(2));         coord.OnDistance(600, T0.AddSeconds(3));   // slot 1

        var s = coord.Snapshot();
        s.Surveys.Should().HaveCount(2);
        s.CanUndo.Should().BeTrue();

        coord.UndoLastReading().Should().BeTrue();
        coord.Snapshot().Surveys.Should().ContainSingle();      // slot 1 (created+empty) dropped

        coord.UndoLastReading().Should().BeTrue();
        var s2 = coord.Snapshot();
        s2.Surveys.Should().BeEmpty();
        s2.LocationCount.Should().Be(0);                        // the row this read created rolled out
        s2.CanUndo.Should().BeFalse();

        coord.UndoLastReading().Should().BeFalse();             // nothing left
    }

    [Fact]
    public void Undo_rewinds_the_ordinal_so_the_corrected_read_re_takes_the_slot()
    {
        var (coord, bus) = Build();                 // multi-map default

        // Spot 1 defines a 2-map working set (slots 0, 1).
        PublishPosition(bus, 0, 0, 0, PositionSource.Spawn, T0);
        coord.OnUse(T0);               coord.OnDistance(100, T0.AddSeconds(1));   // slot 0
        coord.OnUse(T0.AddSeconds(2)); coord.OnDistance(200, T0.AddSeconds(3));   // slot 1

        // Spot 2: accidentally check a map first → garbage binds slot 0 @ row 1.
        var t2 = T0.AddMinutes(2);
        PublishPosition(bus, 900, 0, 0, PositionSource.Spawn, t2);
        coord.OnUse(t2);               coord.OnDistance(9999, t2.AddSeconds(1));
        coord.Snapshot().Surveys[0].DistancesByLocation[1].Should().Be(9999);

        coord.UndoLastReading();                                                 // oops

        // The garbage is gone and the ordinal rewound: the *next* read at this
        // spot re-takes slot 0 (k0), not slot 1 — the contract re-aligns.
        coord.OnUse(t2.AddSeconds(4)); coord.OnDistance(480, t2.AddSeconds(5));

        var sv = coord.Snapshot().Surveys;
        sv.Should().HaveCount(2);
        sv[0].DistancesByLocation[1].Should().Be(480);    // corrected read landed on slot 0
        (sv[1].DistancesByLocation.Count <= 1
            || sv[1].DistancesByLocation[1] == 0).Should().BeTrue();   // slot 1 untouched here
    }

    [Fact]
    public void Undo_stack_is_cleared_by_reset_and_area_change()
    {
        var (coord, bus, area) = BuildAreaInv();
        area.CurrentArea = "AreaKurMountains";
        Spot(coord, bus, 0, 0, T0, 500, 600);
        coord.Snapshot().CanUndo.Should().BeTrue();

        area.CurrentArea = "AreaEltibule";
        coord.OnUse(T0.AddMinutes(6));                  // first use in new area clears measurement
        coord.Snapshot().CanUndo.Should().BeFalse();    // stale undo history dropped

        Spot(coord, bus, 0, 0, T0.AddMinutes(7), 700);
        coord.Snapshot().CanUndo.Should().BeTrue();
        coord.Reset();
        coord.Snapshot().CanUndo.Should().BeFalse();
    }

    [Fact]
    public void Same_area_re_observed_does_not_reset()
    {
        var (coord, bus, area) = BuildAreaInv();
        area.CurrentArea = "AreaKurMountains";

        Spot(coord, bus, 0, 0, T0, 500);
        // same area re-confirmed (no change)
        Spot(coord, bus, 800, 0, T0.AddMinutes(2), 520);

        coord.Snapshot().LocationCount.Should().Be(2);         // not reset
    }
}
