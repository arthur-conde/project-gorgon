using FluentAssertions;
using Legolas.Domain;
using Legolas.Flow;
using Legolas.Services;
using Legolas.ViewModels;
using Mithril.GameState.Movement;

namespace Legolas.Tests.ViewModels;

/// <summary>
/// #497 end-to-end — a character-named / <c>@me</c> map pin drives the Survey
/// "you are here" through <see cref="MapOverlayViewModel"/>: it wins over a
/// stale tracker fix, is superseded by a genuinely newer one, follows the pin,
/// and falls back on removal. The unchanged <c>SurveyPlayerGpsTests</c> is the
/// no-pin regression guard.
/// </summary>
public class MapOverlayPinnedAnchorTests
{
    // Scale 1, no rotation, origin (0,0): ProjectWorld(x,_,z) → (x, -z).
    private static readonly AreaCalibration Calib = new(
        Scale: 1.0, RotationRadians: 0.0, OriginX: 0.0, OriginY: 0.0,
        ReferenceCount: 3, ResidualPixels: 0.5);

    private static (SessionState session, MapOverlayViewModel map,
        FakePlayerPositionTracker tracker, FakePlayerPinTracker pins,
        FakeActiveCharacterService chr, FakeAreaCalibrationService areaCal)
        BuildSut(bool calibrated)
    {
        var session = new SessionState();
        var settings = new LegolasSettings();
        var surveyFlow = new SurveyFlowController(session, settings);
        var projector = new CoordinateProjector();
        var brushes = new LegolasBrushes(settings);
        var tracker = new FakePlayerPositionTracker();
        var pins = new FakePlayerPinTracker();
        var chr = new FakeActiveCharacterService();
        chr.SetName("Arthas");
        var charPin = new CharacterPinAnchor(pins, chr);
        var areaCal = new FakeAreaCalibrationService();
        if (calibrated) areaCal.SetCalibration(Calib);

        var map = new MapOverlayViewModel(
            session, projector, new AdaptiveRouteOptimizer(
                new HeldKarpOptimizer(), new NearestNeighbourTwoOptOptimizer()),
            surveyFlow, brushes, settings,
            pinCalibration: null, positionTracker: tracker, areaCalibration: areaCal,
            motherlode: null, characterPin: charPin);
        return (session, map, tracker, pins, chr, areaCal);
    }

    [Fact]
    public void Dropping_a_character_named_pin_becomes_the_anchor()
    {
        var (session, map, _, pins, _, _) = BuildSut(calibrated: true);

        pins.Add(40, -25, "Arthas");

        session.SurveyPlayerIsPinned.Should().BeTrue();
        session.SurveyPlayerIsManual.Should().BeTrue();
        session.SurveyPlayerPixel.Should().Be(Calib.ProjectWorld(new WorldCoord(40, 0, -25)));
        map.PlayerAnchorStatus.Should().StartWith("You — pinned");
    }

    [Fact]
    public void The_at_me_sentinel_also_works()
    {
        var (session, _, _, pins, _, _) = BuildSut(calibrated: true);

        pins.Add(10, 10, "@me");

        session.SurveyPlayerIsPinned.Should().BeTrue();
        session.SurveyPlayerPixel.Should().Be(Calib.ProjectWorld(new WorldCoord(10, 0, 10)));
    }

    [Fact]
    public void The_pin_beats_a_stale_tracker_fix()
    {
        var (session, _, tracker, pins, _, _) = BuildSut(calibrated: true);
        tracker.Push(999, 0, 999, PlayerPositionSource.Spawn,
            DateTimeOffset.UtcNow.AddHours(-1));   // old auto fix

        pins.Add(40, -25, "Arthas");

        session.SurveyPlayerIsPinned.Should().BeTrue();
        session.SurveyPlayerPixel.Should().Be(Calib.ProjectWorld(new WorldCoord(40, 0, -25)));
    }

    [Fact]
    public void A_genuinely_newer_tracker_fix_supersedes_the_pin()
    {
        var (session, _, tracker, pins, _, _) = BuildSut(calibrated: true);
        pins.Add(40, -25, "Arthas");
        session.SurveyPlayerIsPinned.Should().BeTrue();

        tracker.Push(7, 0, 8, PlayerPositionSource.Movement,
            DateTimeOffset.UtcNow.AddMinutes(5));   // fresh zone-in/teleport

        session.SurveyPlayerIsPinned.Should().BeFalse();
        session.SurveyPlayerIsManual.Should().BeFalse();
        session.SurveyPlayerPixel.Should().Be(Calib.ProjectWorld(new WorldCoord(7, 0, 8)));
    }

    [Fact]
    public void Removing_the_pin_falls_back_to_the_auto_fix()
    {
        var (session, _, tracker, pins, _, _) = BuildSut(calibrated: true);
        tracker.Push(7, 0, 8, PlayerPositionSource.Spawn, DateTimeOffset.UtcNow.AddHours(-1));
        var pin = pins.Add(40, -25, "Arthas");
        session.SurveyPlayerIsPinned.Should().BeTrue();

        pins.Remove(pin);

        session.SurveyPlayerIsPinned.Should().BeFalse();
        session.SurveyPlayerIsManual.Should().BeFalse();
        session.SurveyPlayerPixel.Should().Be(Calib.ProjectWorld(new WorldCoord(7, 0, 8)));
    }

    [Fact]
    public void Uncalibrated_area_cannot_show_the_pinned_marker()
    {
        var (session, map, _, pins, _, _) = BuildSut(calibrated: false);

        pins.Add(40, -25, "Arthas");

        session.SurveyPlayerIsPinned.Should().BeFalse();
        session.SurveyPlayerPixel.Should().BeNull();
        map.PlayerAnchorStatus.Should().BeEmpty();
    }
}
