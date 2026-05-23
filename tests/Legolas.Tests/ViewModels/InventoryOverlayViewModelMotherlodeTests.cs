using System.Collections;
using FluentAssertions;
using Legolas.Domain;
using Legolas.Flow;
using Legolas.Services;
using Legolas.ViewModels;
using Mithril.GameState.Movement;

namespace Legolas.Tests.ViewModels;

/// <summary>
/// #488 (locked model): in Motherlode mode the inventory overlay surfaces the
/// <b>create-on-use</b> working slots as a read-order list (use-line name +
/// 1-based ordinal), sourced from the coordinator via
/// <see cref="MotherlodeViewModel"/> and kept separate from Survey state. A
/// dug map (motherlode-map <c>Deleted</c>) retires its slot and the list
/// recompacts. Holding stock creates nothing. (No Application dispatcher in a
/// unit test ⇒ the VM rebuilds synchronously.)
/// </summary>
public class InventoryOverlayViewModelMotherlodeTests
{
    private static readonly DateTimeOffset T0 = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    private const string KurSimpleIN = "MiningSurveyKurMountains1X";
    private const string KurBasicIN = "MiningSurveyKurMountains2X";
    private const string KurSimple = "Kur Mountains Simple Metal Motherlode Map";
    private const string KurBasic = "Kur Mountains Basic Metal Motherlode Map";

    private static (InventoryOverlayViewModel overlay, SessionState session,
        MotherlodeMeasurementCoordinator coord, FakePlayerPositionTracker pos,
        FakeMotherlodePlayerWorld inv) Build()
    {
        var session = new SessionState();
        var pos = new FakePlayerPositionTracker();
        var pins = new FakePlayerPinTracker();
        var inv = new FakeMotherlodePlayerWorld();
        var refData = new FakeMotherlodeRefData(
            (KurSimpleIN, KurSimple), (KurBasicIN, KurBasic));
        var flow = new MotherlodeFlowController(new SessionState());
        var coord = new MotherlodeMeasurementCoordinator(
            new MultilaterationSolver(), flow, pos, pins, inv, refData,
            new LegolasSettings());                       // multi-map default
        var optimizer = new AdaptiveRouteOptimizer(
            new HeldKarpOptimizer(), new NearestNeighbourTwoOptOptimizer());
        var mlVm = new MotherlodeViewModel(coord, optimizer, flow, pins);
        var overlay = new InventoryOverlayViewModel(new InventoryGridSettings(), session, mlVm);
        return (overlay, session, coord, pos, inv);
    }

    /// <summary>One read = a named use + its distance (create-on-use).</summary>
    private static void Read(MotherlodeMeasurementCoordinator coord, FakePlayerPositionTracker pos,
        string mapName, int dist, DateTimeOffset at)
    {
        pos.Push(0, 0, 0, PlayerPositionSource.Spawn, at);
        coord.OnUse(at, mapName);
        coord.OnDistance(dist, at.AddSeconds(1));
    }

    private static List<SurveyItemViewModel> Items(InventoryOverlayViewModel o) =>
        ((IEnumerable)o.ActiveSlots).Cast<SurveyItemViewModel>().ToList();

    [Fact]
    public void Holding_stock_shows_nothing_in_motherlode_mode()
    {
        var (overlay, session, _, _, inv) = Build();
        session.Mode = SessionMode.Motherlode;
        for (var i = 1; i <= 50; i++) inv.Add(i, KurSimpleIN);   // carried, unused

        Items(overlay).Should().BeEmpty();                       // create-on-use
    }

    [Fact]
    public void Survey_mode_yields_the_survey_collection_not_motherlode()
    {
        var (overlay, session, coord, pos, _) = Build();
        Read(coord, pos, KurSimple, 500, T0);                    // a tracked slot exists

        session.Mode.Should().Be(SessionMode.Survey);
        Items(overlay).Should().BeEmpty();                       // _session.Surveys is empty
    }

    [Fact]
    public void Motherlode_mode_lists_created_slots_in_read_order()
    {
        var (overlay, session, coord, pos, _) = Build();
        session.Mode = SessionMode.Motherlode;

        Read(coord, pos, KurSimple, 500, T0);                    // slot 0
        Read(coord, pos, KurBasic, 600, T0.AddSeconds(3));       // slot 1 (same spot)

        var items = Items(overlay);
        items.Should().HaveCount(2);
        items[0].Name.Should().Be(KurSimple);
        items[0].GridIndex.Should().Be(0);                       // 1-based via the XAML converter
        items[1].Name.Should().Be(KurBasic);
        items[1].GridIndex.Should().Be(1);
    }

    [Fact]
    public void Dug_map_drops_out_and_the_list_recompacts()
    {
        var (overlay, session, coord, pos, inv) = Build();
        session.Mode = SessionMode.Motherlode;
        inv.Add(1, KurSimpleIN);                                 // register (no slot)
        inv.Add(2, KurBasicIN);
        Read(coord, pos, KurSimple, 500, T0);                    // slot 0
        Read(coord, pos, KurBasic, 600, T0.AddSeconds(3));       // slot 1
        Items(overlay).Should().HaveCount(2);

        inv.Delete(1, T0.AddSeconds(10).UtcDateTime);            // a dig → retire next-uncollected (slot 0)

        var items = Items(overlay);
        items.Should().ContainSingle().Which.Name.Should().Be(KurBasic);
        items[0].GridIndex.Should().Be(0);                       // ordinal recompacts
    }

    [Fact]
    public void Switching_back_to_survey_restores_the_survey_source()
    {
        var (overlay, session, coord, pos, _) = Build();
        session.Mode = SessionMode.Motherlode;
        Read(coord, pos, KurSimple, 500, T0);
        Items(overlay).Should().ContainSingle();

        session.Mode = SessionMode.Survey;
        Items(overlay).Should().BeEmpty();                       // back to the (empty) survey view
    }
}
