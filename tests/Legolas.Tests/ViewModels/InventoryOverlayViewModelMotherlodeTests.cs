using System.Collections;
using Arda.Abstractions.Logs;
using Arda.World.Player.Events;
using FluentAssertions;
using Legolas.Domain;
using Legolas.Flow;
using Legolas.Services;
using Legolas.Tests.TestSupport;
using Legolas.ViewModels;

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

    private static LogLineMetadata Meta(DateTimeOffset at) => new(at, at, false);

    private static void PublishPosition(TestDomainEventBus bus, double x, double y, double z,
        PositionSource source, DateTimeOffset at)
        => bus.Publish(new PlayerPositionChanged(x, y, z, source, Meta(at)));

    private static void PublishDelete(TestDomainEventBus bus, long id, string internalName, DateTime at)
    {
        var ts = DateTime.SpecifyKind(at, DateTimeKind.Utc);
        var dto = new DateTimeOffset(ts, TimeSpan.Zero);
        bus.Publish(new InventoryItemRemoved(id, internalName, Meta(dto)));
    }

    private static (InventoryOverlayViewModel overlay, SessionState session,
        MotherlodeMeasurementCoordinator coord, TestDomainEventBus bus) Build()
    {
        var session = new SessionState();
        var bus = new TestDomainEventBus();
        var refData = new FakeMotherlodeRefData(
            (KurSimpleIN, KurSimple), (KurBasicIN, KurBasic));
        var flow = new MotherlodeFlowController(new SessionState());
        var coord = new MotherlodeMeasurementCoordinator(
            new MultilaterationSolver(), flow, bus, refData,
            new LegolasSettings());                       // multi-map default
        var optimizer = new AdaptiveRouteOptimizer(
            new HeldKarpOptimizer(), new NearestNeighbourTwoOptOptimizer());
        var mlVm = new MotherlodeViewModel(coord, optimizer, flow);
        var overlay = new InventoryOverlayViewModel(new InventoryGridSettings(), session, mlVm);
        return (overlay, session, coord, bus);
    }

    /// <summary>One read = a named use + its distance (create-on-use).</summary>
    private static void Read(MotherlodeMeasurementCoordinator coord, TestDomainEventBus bus,
        string mapName, int dist, DateTimeOffset at)
    {
        PublishPosition(bus, 0, 0, 0, PositionSource.Spawn, at);
        coord.OnUse(at, mapName);
        coord.OnDistance(dist, at.AddSeconds(1));
    }

    private static List<SurveyItemViewModel> Items(InventoryOverlayViewModel o) =>
        ((IEnumerable)o.ActiveSlots).Cast<SurveyItemViewModel>().ToList();

    [Fact]
    public void Holding_stock_shows_nothing_in_motherlode_mode()
    {
        var (overlay, session, _, _) = Build();
        session.Mode = SessionMode.Motherlode;
        // Post-Arda: the coordinator doesn't react to adds at all.
        // Holding stock is invisible.

        Items(overlay).Should().BeEmpty();                       // create-on-use
    }

    [Fact]
    public void Survey_mode_yields_the_survey_collection_not_motherlode()
    {
        var (overlay, session, coord, bus) = Build();
        Read(coord, bus, KurSimple, 500, T0);                    // a tracked slot exists

        session.Mode.Should().Be(SessionMode.Survey);
        Items(overlay).Should().BeEmpty();                       // _session.Surveys is empty
    }

    [Fact]
    public void Motherlode_mode_lists_created_slots_in_read_order()
    {
        var (overlay, session, coord, bus) = Build();
        session.Mode = SessionMode.Motherlode;

        Read(coord, bus, KurSimple, 500, T0);                    // slot 0
        Read(coord, bus, KurBasic, 600, T0.AddSeconds(3));       // slot 1 (same spot)

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
        var (overlay, session, coord, bus) = Build();
        session.Mode = SessionMode.Motherlode;
        Read(coord, bus, KurSimple, 500, T0);                    // slot 0
        Read(coord, bus, KurBasic, 600, T0.AddSeconds(3));       // slot 1
        Items(overlay).Should().HaveCount(2);

        PublishDelete(bus, 1, KurSimpleIN, T0.AddSeconds(10).UtcDateTime);

        var items = Items(overlay);
        items.Should().ContainSingle().Which.Name.Should().Be(KurBasic);
        items[0].GridIndex.Should().Be(0);                       // ordinal recompacts
    }

    [Fact]
    public void Switching_back_to_survey_restores_the_survey_source()
    {
        var (overlay, session, coord, bus) = Build();
        session.Mode = SessionMode.Motherlode;
        Read(coord, bus, KurSimple, 500, T0);
        Items(overlay).Should().ContainSingle();

        session.Mode = SessionMode.Survey;
        Items(overlay).Should().BeEmpty();                       // back to the (empty) survey view
    }
}
