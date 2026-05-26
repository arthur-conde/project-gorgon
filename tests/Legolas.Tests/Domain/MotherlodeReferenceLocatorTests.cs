using Arda.World.Player;
using FluentAssertions;
using Legolas.Domain;

namespace Legolas.Tests.Domain;

public class MotherlodeReferenceLocatorTests
{
    private static MotherlodePositionSample Spot(double x, double z, double conf = 1.0) =>
        new(new WorldCoord(x, 0, z), MotherlodePositionSource.LogPosition, conf, DateTimeOffset.UnixEpoch);

    private static MapPinEntry Pin(double x, double z, string label) =>
        new(x, z, label, 0, 0);

    private static CalibrationReference Ref(double x, double z, string name) =>
        new(name, "NPC", new WorldCoord(x, 0, z));

    private static readonly IReadOnlyList<MotherlodePositionSample> NoSpots = Array.Empty<MotherlodePositionSample>();
    private static readonly IReadOnlyList<MapPinEntry> NoPins = Array.Empty<MapPinEntry>();
    private static readonly IReadOnlyList<CalibrationReference> NoGaz = Array.Empty<CalibrationReference>();

    [Fact]
    public void Returns_null_when_no_reference_of_any_tier()
    {
        MotherlodeReferenceLocator.Nearest(new WorldCoord(0, 0, 0), NoSpots, NoPins, NoGaz)
            .Should().BeNull();
    }

    [Fact]
    public void Picks_globally_nearest_across_tiers_not_the_most_reliable()
    {
        // A far measured spot (tier 0) vs a near gazetteer point (tier 2):
        // proximity wins — a far "reliable" reference is a useless phrase.
        var solved = new WorldCoord(100, 0, 100);
        var spots = new[] { Spot(0, 0) };                   // ~141 m away
        var gaz = new[] { Ref(110, 100, "Serbule Keep") };  //  ~10 m away

        var b = MotherlodeReferenceLocator.Nearest(solved, spots, NoPins, gaz);

        b.Should().NotBeNull();
        b!.Tier.Should().Be(MotherlodeReferenceTier.Gazetteer);
        b.ReferenceName.Should().Be("Serbule Keep");
        b.DistanceMetres.Should().BeApproximately(10, 0.001);
    }

    [Fact]
    public void Exact_tie_resolves_to_more_reliable_tier()
    {
        var solved = new WorldCoord(0, 0, 50);
        var spots = new[] { Spot(0, 0) };          // 50 m, tier MeasuredSpot
        var gaz = new[] { Ref(0, 0, "A Statue") }; // 50 m, tier Gazetteer

        var b = MotherlodeReferenceLocator.Nearest(solved, spots, NoPins, gaz);

        b!.Tier.Should().Be(MotherlodeReferenceTier.MeasuredSpot);
        b.ReferenceName.Should().Be("your spot #1");
    }

    [Fact]
    public void Fixless_spot_rows_are_skipped_but_keep_the_index()
    {
        // Row 0 has no fix; row 1 is the only usable spot — it must still read
        // "spot #2" so the label matches the readings the player took.
        var solved = new WorldCoord(0, 0, 10);
        var spots = new[] { Spot(999, 999, conf: 0), Spot(0, 0, conf: 1) };

        var b = MotherlodeReferenceLocator.Nearest(solved, spots, NoPins, NoGaz);

        b!.ReferenceName.Should().Be("your spot #2");
    }

    [Fact]
    public void Bearing_is_reference_to_treasure_direction()
    {
        // Treasure due +North (+Z) of the pin.
        var b = MotherlodeReferenceLocator.Nearest(
            new WorldCoord(0, 0, 300), NoSpots, new[] { Pin(0, 0, "home") }, NoGaz);

        b!.Direction.Should().Be(CardinalDirection.N);
        b.ToDisplayString().Should().Be("≈ 300 m N of \"home\"");
    }

    [Fact]
    public void Unlabeled_pin_falls_back_to_appearance_phrase()
    {
        var b = MotherlodeReferenceLocator.Nearest(
            new WorldCoord(0, 0, 20), NoSpots, new[] { Pin(0, 0, "  ") }, NoGaz);

        b!.ReferenceName.Should().StartWith("your ");
        b.ReferenceName.Should().EndWith(" pin");
    }

    [Fact]
    public void Coincident_reference_drops_the_bearing()
    {
        var b = MotherlodeReferenceLocator.Nearest(
            new WorldCoord(5, 0, 5), NoSpots, NoPins, new[] { Ref(5, 5, "Marna") });

        b!.ToDisplayString().Should().Be("at Marna");
    }

    [Fact]
    public void Distance_rounds_to_nearest_ten_metres()
    {
        var b = MotherlodeReferenceLocator.Nearest(
            new WorldCoord(0, 0, 337), NoSpots, NoPins, new[] { Ref(0, 0, "X") });

        b!.ToDisplayString().Should().Be("≈ 340 m N of X");
    }
}
