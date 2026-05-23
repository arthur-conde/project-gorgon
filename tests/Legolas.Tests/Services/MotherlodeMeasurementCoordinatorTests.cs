using FluentAssertions;
using Legolas.Domain;
using Legolas.Flow;
using Legolas.Services;
using Legolas.ViewModels;
using Mithril.GameState.Areas;
using Mithril.GameState.Areas.Parsing;
using Mithril.GameState.Movement;

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

    private static (MotherlodeMeasurementCoordinator coord, FakePlayerPositionTracker pos,
        FakePlayerPinTracker pins) Build()
    {
        var pos = new FakePlayerPositionTracker();
        var pins = new FakePlayerPinTracker();
        var flow = new MotherlodeFlowController(new SessionState());
        var coord = new MotherlodeMeasurementCoordinator(
            new MultilaterationSolver(), flow, pos, pins);
        return (coord, pos, pins);
    }

    private static double D(double x, double z, double tx, double tz) =>
        Math.Sqrt((x - tx) * (x - tx) + (z - tz) * (z - tz));

    /// <summary>One spot: push a Spawn fix, fire the use, then the distance.</summary>
    private static void Measure(MotherlodeMeasurementCoordinator coord, FakePlayerPositionTracker pos,
        double x, double z, int metres, DateTimeOffset at)
    {
        pos.Push(x, 0, z, PlayerPositionSource.Spawn, at);
        coord.OnUse(at);
        coord.OnDistance(metres, at.AddSeconds(2));
    }

    [Fact]
    public void Three_located_spots_solve_the_treasure_in_world_space()
    {
        var (coord, pos, _) = Build();
        (double X, double Z) target = (420, -260);

        Measure(coord, pos, 0, 0, (int)Math.Round(D(0, 0, target.X, target.Z)), T0);
        Measure(coord, pos, 800, 0, (int)Math.Round(D(800, 0, target.X, target.Z)), T0.AddMinutes(2));
        Measure(coord, pos, 0, -800, (int)Math.Round(D(0, -800, target.X, target.Z)), T0.AddMinutes(4));

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
        var (coord, _, _) = Build();

        coord.OnDistance(500, T0);

        coord.Snapshot().LocationCount.Should().Be(0);
        coord.Snapshot().Surveys.Should().BeEmpty();
    }

    [Fact]
    public void Distance_outside_the_use_window_is_dropped()
    {
        var (coord, pos, _) = Build();
        pos.Push(0, 0, 0, PlayerPositionSource.Spawn, T0);
        coord.OnUse(T0);

        coord.OnDistance(500, T0.AddMinutes(5));   // far past DistanceWindow

        coord.Snapshot().Surveys.Should().BeEmpty();
    }

    [Fact]
    public void Use_with_a_stale_feeder_fix_records_no_position_and_warns()
    {
        var (coord, pos, _) = Build();
        // Fix is 30 min older than the use → beyond MaxFeederGap.
        pos.Push(10, 0, 10, PlayerPositionSource.Spawn, T0);
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
        var (coord, pos, _) = Build();
        pos.Push(0, 0, 0, PlayerPositionSource.Spawn, T0);
        coord.OnUse(T0, "Kur Mountains Simple Metal Motherlode Map");
        coord.OnDistance(500, T0.AddSeconds(2));

        coord.Snapshot().Surveys.Should().ContainSingle()
             .Which.MapName.Should().Be("Kur Mountains Simple Metal Motherlode Map");
    }

    [Fact]
    public void Multiple_maps_at_one_spot_fan_out_to_independent_slots()
    {
        var (coord, pos, _) = Build();          // multi-map default
        (double X, double Z) a = (300, 300);
        (double X, double Z) b = (-150, 480);

        void Spot(double x, double z, DateTimeOffset at)
        {
            pos.Push(x, 0, z, PlayerPositionSource.Spawn, at);
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
        var (coord, _, pins) = Build();
        (double X, double Z) target = (100, 100);
        var now = DateTimeOffset.UtcNow;

        void PinSpot(double x, double z, DateTimeOffset at)
        {
            pins.Add(x, z, "spot");                 // feeder #2
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
        var pos = new FakePlayerPositionTracker();
        var pins = new FakePlayerPinTracker();
        var chr = new FakeActiveCharacterService();
        chr.SetName("Arthas");
        var charPin = new CharacterPinAnchor(pins, chr);
        var coord = new MotherlodeMeasurementCoordinator(
            new MultilaterationSolver(), new MotherlodeFlowController(new SessionState()),
            pos, pins, characterPin: charPin);

        (double X, double Z) target = (100, 100);
        var now = DateTimeOffset.UtcNow;
        void PinSpot(double x, double z, DateTimeOffset at)
        {
            pins.Add(x, z, "Arthas");               // the player's "I am here" pin
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
        var (coord, pos, _) = Build();
        Measure(coord, pos, 0, 0, 300, T0);

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

    private static (MotherlodeMeasurementCoordinator coord, FakePlayerPositionTracker pos,
        FakeMotherlodePlayerWorld inv) BuildInv(bool multiMap)
    {
        var pos = new FakePlayerPositionTracker();
        var pins = new FakePlayerPinTracker();
        var inv = new FakeMotherlodePlayerWorld();
        var flow = new MotherlodeFlowController(new SessionState());
        var settings = new LegolasSettings { MotherlodeMultiMapMode = multiMap };
        var coord = new MotherlodeMeasurementCoordinator(
            new MultilaterationSolver(), flow, pos, pins, inv, RefData(), settings);
        return (coord, pos, inv);
    }

    /// <summary>One spot: Spawn fix, use, then the listed distances in order.</summary>
    private static void Spot(MotherlodeMeasurementCoordinator coord, FakePlayerPositionTracker pos,
        double x, double z, DateTimeOffset at, params int[] dists)
    {
        pos.Push(x, 0, z, PlayerPositionSource.Spawn, at);
        coord.OnUse(at);
        var t = at;
        foreach (var d in dists)
            coord.OnDistance(d, t = t.AddSeconds(1));
    }

    [Fact]
    public void Holding_stock_creates_no_slots()
    {
        var (coord, _, inv) = BuildInv(multiMap: true);
        for (var i = 1; i <= 120; i++) inv.Add(i, KurSimple);   // a fat carried stack

        var snap = coord.Snapshot();
        snap.Surveys.Should().BeEmpty();        // create-on-use: holding ≠ measuring
        snap.MapsDug.Should().Be(0);
    }

    [Fact]
    public void Single_treasure_create_on_use_solves()
    {
        var (coord, pos, inv) = BuildInv(multiMap: true);
        inv.Add(100, KurSimple);                                // held — irrelevant
        (double X, double Z) target = (420, -260);

        Spot(coord, pos, 0, 0, T0, (int)Math.Round(D(0, 0, target.X, target.Z)));
        Spot(coord, pos, 800, 0, T0.AddMinutes(2), (int)Math.Round(D(800, 0, target.X, target.Z)));
        Spot(coord, pos, 0, -800, T0.AddMinutes(4), (int)Math.Round(D(0, -800, target.X, target.Z)));

        var s = coord.Snapshot().Surveys.Should().ContainSingle().Subject;
        s.SolvedWorld!.Value.X.Should().BeApproximately(target.X, 3.0);
        s.SolvedWorld!.Value.Z.Should().BeApproximately(target.Z, 3.0);
    }

    [Fact]
    public void Motherlode_map_delete_counts_a_dig_and_retires_next_slot()
    {
        var (coord, pos, inv) = BuildInv(multiMap: true);
        inv.Add(1, KurSimple);                                  // register (no slot — Added ignored)
        inv.Add(2, KurBasic);
        // Two treasures measured (create-on-use), then the cluster is dug.
        Spot(coord, pos, 0, 0, T0, 500, 600);
        Spot(coord, pos, 900, 0, T0.AddMinutes(2), 480, 470);
        Spot(coord, pos, 0, 900, T0.AddMinutes(4), 450, 460);

        coord.Snapshot().Surveys.Count(s => !s.Collected).Should().Be(2);

        inv.Delete(1, T0.AddMinutes(6).UtcDateTime);
        inv.Delete(2, T0.AddMinutes(6).AddSeconds(5).UtcDateTime);

        var snap = coord.Snapshot();
        snap.MapsDug.Should().Be(2);
        snap.Surveys.All(s => s.Collected).Should().BeTrue();   // both retired in order
    }

    [Fact]
    public void Completing_use_then_delete_discards_the_ephemeral_location()
    {
        var (coord, pos, inv) = BuildInv(multiMap: true);
        inv.Add(1, KurSimple);                 // register (no slot)
        pos.Push(0, 0, 0, PlayerPositionSource.Spawn, T0);

        coord.OnUse(T0);                       // opens a location, no distance (completing use)
        inv.Delete(1, T0.AddSeconds(1).UtcDateTime);

        var snap = coord.Snapshot();
        snap.LocationCount.Should().Be(0);     // ephemeral location discarded
        snap.MapsDug.Should().Be(1);
        snap.Surveys.Should().BeEmpty();
    }

    [Fact]
    public void Non_motherlode_delete_does_not_count_as_a_dig()
    {
        var (coord, _, inv) = BuildInv(multiMap: true);
        inv.Add(9, "RawGem_Diamond");
        inv.Delete(9, DateTime.UtcNow);

        var snap = coord.Snapshot();
        snap.Surveys.Should().BeEmpty();
        snap.MapsDug.Should().Be(0);
    }

    [Fact]
    public void Toggle_off_serial_binds_one_active_treasure()
    {
        var (coord, pos, _) = BuildInv(multiMap: false);
        (double X, double Z) target = (420, -260);

        // Serial = one read per position (re-read while moving); even with the
        // toggle off the single active treasure solves across the 3 spots.
        Spot(coord, pos, 0, 0, T0, (int)Math.Round(D(0, 0, target.X, target.Z)));
        Spot(coord, pos, 800, 0, T0.AddMinutes(2), (int)Math.Round(D(800, 0, target.X, target.Z)));
        Spot(coord, pos, 0, -800, T0.AddMinutes(4), (int)Math.Round(D(0, -800, target.X, target.Z)));

        var surveys = coord.Snapshot().Surveys;
        surveys.Should().ContainSingle();                       // never fanned out
        surveys[0].SolvedWorld!.Value.X.Should().BeApproximately(target.X, 3.0);
        surveys[0].SolvedWorld!.Value.Z.Should().BeApproximately(target.Z, 3.0);
    }

    [Fact]
    public void Multi_map_two_maps_bind_by_order_across_spots()
    {
        var (coord, pos, _) = BuildInv(multiMap: true);
        (double X, double Z) a = (300, 300);
        (double X, double Z) b = (-150, 480);

        void S(double x, double z, DateTimeOffset at) => Spot(coord, pos, x, z, at,
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
        // The real multi-map workflow: triangulate the whole stack across all
        // spots first, *then* walk and dig — completion is post-measurement.
        var (coord, pos, inv) = BuildInv(multiMap: true);
        inv.Add(1, KurSimple);                                  // register (no slot)
        inv.Add(2, KurBasic);
        (double X, double Z) a = (300, 300);
        (double X, double Z) c = (-150, 480);

        void S(double x, double z, DateTimeOffset at) => Spot(coord, pos, x, z, at,
            (int)Math.Round(D(x, z, a.X, a.Z)), (int)Math.Round(D(x, z, c.X, c.Z)));

        S(0, 0, T0);
        S(900, 0, T0.AddMinutes(2));
        S(0, 900, T0.AddMinutes(4));

        inv.Delete(1, T0.AddMinutes(6).UtcDateTime);            // dig both
        inv.Delete(2, T0.AddMinutes(6).AddSeconds(8).UtcDateTime);

        var snap = coord.Snapshot();
        snap.MapsDug.Should().Be(2);
        snap.Surveys.Should().OnlyContain(s => s.Collected);
        snap.Surveys.Should().HaveCount(2);                     // solved before retirement
        snap.Surveys.Select(s => s.SolvedWorld).Should().OnlyContain(w => w != null);
    }

    [Fact]
    public void Spot_count_shortfall_raises_cross_spot_divergence()
    {
        var (coord, pos, _) = BuildInv(multiMap: true);

        Spot(coord, pos, 0, 0, T0, 500, 600);                 // spot 1: 2 reads
        Spot(coord, pos, 900, 0, T0.AddMinutes(2), 480);      // spot 2: 1 read (short!)
        Spot(coord, pos, 0, 900, T0.AddMinutes(4), 450, 470); // spot 3 (open)

        var snap = coord.Snapshot();
        snap.Guidance.Should().Contain("Spot #2");
        snap.ReadsPerLocation.Should().Equal(2, 1, 2);
    }

    [Fact]
    public void Final_open_short_batch_does_not_false_positive()
    {
        var (coord, pos, _) = BuildInv(multiMap: true);

        Spot(coord, pos, 0, 0, T0, 500, 600);              // spot 1: 2 reads
        Spot(coord, pos, 900, 0, T0.AddMinutes(2), 480);   // spot 2 still open: 1 read so far

        var g = coord.Snapshot().Guidance ?? string.Empty;
        g.Should().NotContain("Spot #");                   // the open batch legitimately trails
    }

    [Fact]
    public void ReadsPerLocation_projection_matches_bindings()
    {
        var (coord, pos, _) = BuildInv(multiMap: true);

        Spot(coord, pos, 0, 0, T0, 500, 600);
        Spot(coord, pos, 900, 0, T0.AddMinutes(2), 480, 470);

        coord.Snapshot().ReadsPerLocation.Should().Equal(2, 2);
    }

    // ---- Risk-1 same-spot clustering -------------------------------------

    [Fact]
    public void Slow_same_spot_batch_does_not_split_or_desync()
    {
        var (coord, pos, _) = Build();

        pos.Push(0, 0, 0, PlayerPositionSource.Spawn, T0);
        coord.OnUse(T0);
        coord.OnDistance(900, T0.AddSeconds(2)); // commits the only location

        // 45 s later, still standing on the same spot (>30 s time gate).
        pos.Push(0, 0, 0, PlayerPositionSource.Spawn, T0.AddSeconds(45));
        coord.OnUse(T0.AddSeconds(45));
        coord.OnDistance(905, T0.AddSeconds(47));

        coord.Snapshot().LocationCount.Should().Be(1);
    }

    [Fact]
    public void Genuine_move_within_time_gate_splits()
    {
        var (coord, pos, _) = Build();

        pos.Push(0, 0, 0, PlayerPositionSource.Spawn, T0);
        coord.OnUse(T0);
        coord.OnDistance(900, T0.AddSeconds(2));

        pos.Push(500, 0, 0, PlayerPositionSource.Spawn, T0.AddSeconds(45));
        coord.OnUse(T0.AddSeconds(45));
        coord.OnDistance(700, T0.AddSeconds(47));

        coord.Snapshot().LocationCount.Should().Be(2);
    }

    [Fact]
    public void Same_spot_merge_uses_frozen_anchor_not_drifting_fix()
    {
        var (coord, _, pins) = Build();
        var now = DateTimeOffset.UtcNow;

        pins.Add(0, 0, "p");
        coord.OnUse(now);
        coord.OnDistance(100, now.AddSeconds(2));          // anchor frozen at (0,0)

        pins.Add(10, 0, "p");                               // 10 m ≤ 12 → merge
        coord.OnUse(now.AddSeconds(45));
        coord.OnDistance(100, now.AddSeconds(47));

        pins.Add(19, 0, "p");                               // 19 m from frozen (0,0) → split
        coord.OnUse(now.AddSeconds(90));
        coord.OnDistance(100, now.AddSeconds(92));

        coord.Snapshot().LocationCount.Should().Be(2);
    }

    [Fact]
    public void No_position_falls_back_to_time_gate()
    {
        var (coord, _, _) = Build();             // no feeder fix ever pushed

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
        var selfPins = new FakePlayerPinTracker();
        var charPin = new CharacterPinAnchor(selfPins, new FakeActiveCharacterService());
        var selfCoord = new MotherlodeMeasurementCoordinator(
            new MultilaterationSolver(), new MotherlodeFlowController(new SessionState()),
            new FakePlayerPositionTracker(), selfPins, characterPin: charPin);
        selfPins.Add(0, 0, CharacterPinAnchor.SelfPinSentinel);
        selfCoord.OnUse(now);
        selfCoord.OnDistance(100, now.AddSeconds(2));

        var (genCoord, _, genPins) = Build();
        genPins.Add(0, 0, "somewhere");
        genCoord.OnUse(now);
        genCoord.OnDistance(100, now.AddSeconds(2));

        var self = selfCoord.Snapshot().Locations.Single().Confidence;
        var generic = genCoord.Snapshot().Locations.Single().Confidence;
        self.Should().BeGreaterThan(generic);
        self.Should().BeApproximately(0.85, 1e-9);   // NamedMapPin
        generic.Should().BeApproximately(0.6, 1e-9); // MapPin
    }

    // ---- area scoping (GameState PlayerAreaTracker) ----------------------

    private static (MotherlodeMeasurementCoordinator coord, FakePlayerPositionTracker pos,
        FakeMotherlodePlayerWorld inv, PlayerAreaTracker area) BuildAreaInv()
    {
        var pos = new FakePlayerPositionTracker();
        var pins = new FakePlayerPinTracker();
        var inv = new FakeMotherlodePlayerWorld();
        var area = new PlayerAreaTracker(new AreaTransitionParser());
        var flow = new MotherlodeFlowController(new SessionState());
        var coord = new MotherlodeMeasurementCoordinator(
            new MultilaterationSolver(), flow, pos, pins, inv, RefData(),
            new LegolasSettings(), null, area);
        return (coord, pos, inv, area);
    }

    [Fact]
    public void Area_change_clears_measurement_but_keeps_the_dug_count()
    {
        var (coord, pos, inv, area) = BuildAreaInv();
        area.Observe("LOADING LEVEL AreaKurMountains", T0.UtcDateTime);
        inv.Add(1, KurSimple);

        Spot(coord, pos, 0, 0, T0, 500, 600);                  // measurement in area A
        inv.Delete(1, T0.AddSeconds(30).UtcDateTime);          // a dig → MapsDug = 1
        coord.Snapshot().MapsDug.Should().Be(1);
        coord.Snapshot().LocationCount.Should().BeGreaterThan(0);

        area.Observe("LOADING LEVEL AreaEltibule", T0.AddMinutes(5).UtcDateTime);
        Spot(coord, pos, 10, 10, T0.AddMinutes(6), 700);       // first use in the new area

        var snap = coord.Snapshot();
        snap.MapsDug.Should().Be(1);                           // cumulative stat kept
        snap.LocationCount.Should().Be(1);                     // old area-local fixes cleared
        snap.Surveys.Should().ContainSingle();                 // old slots cleared
    }

    // ---- undo last reading ("oops, accidentally checked a map") ----------

    [Fact]
    public void Undo_pops_readings_LIFO_then_clears_can_undo()
    {
        var (coord, pos, _) = Build();                 // multi-map default
        pos.Push(0, 0, 0, PlayerPositionSource.Spawn, T0);
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
        var (coord, pos, _) = Build();                 // multi-map default

        // Spot 1 defines a 2-map working set (slots 0, 1).
        pos.Push(0, 0, 0, PlayerPositionSource.Spawn, T0);
        coord.OnUse(T0);               coord.OnDistance(100, T0.AddSeconds(1));   // slot 0
        coord.OnUse(T0.AddSeconds(2)); coord.OnDistance(200, T0.AddSeconds(3));   // slot 1

        // Spot 2: accidentally check a map first → garbage binds slot 0 @ row 1.
        var t2 = T0.AddMinutes(2);
        pos.Push(900, 0, 0, PlayerPositionSource.Spawn, t2);
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
        var (coord, pos, inv, area) = BuildAreaInv();
        area.Observe("LOADING LEVEL AreaKurMountains", T0.UtcDateTime);
        Spot(coord, pos, 0, 0, T0, 500, 600);
        coord.Snapshot().CanUndo.Should().BeTrue();

        area.Observe("LOADING LEVEL AreaEltibule", T0.AddMinutes(5).UtcDateTime);
        coord.OnUse(T0.AddMinutes(6));                  // first use in new area clears measurement
        coord.Snapshot().CanUndo.Should().BeFalse();    // stale undo history dropped

        Spot(coord, pos, 0, 0, T0.AddMinutes(7), 700);
        coord.Snapshot().CanUndo.Should().BeTrue();
        coord.Reset();
        coord.Snapshot().CanUndo.Should().BeFalse();
    }

    [Fact]
    public void Same_area_re_observed_does_not_reset()
    {
        var (coord, pos, _, area) = BuildAreaInv();
        area.Observe("LOADING LEVEL AreaKurMountains", T0.UtcDateTime);

        Spot(coord, pos, 0, 0, T0, 500);
        area.Observe("LOADING LEVEL AreaKurMountains", T0.AddMinutes(1).UtcDateTime); // same area
        Spot(coord, pos, 800, 0, T0.AddMinutes(2), 520);

        coord.Snapshot().LocationCount.Should().Be(2);         // not reset
    }
}
