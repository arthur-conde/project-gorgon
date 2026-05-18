using FluentAssertions;
using Legolas.Domain;
using Legolas.Services;
using Xunit;

namespace Legolas.Tests.LogTail;

public class PlayerLogParserTests
{
    private readonly PlayerLogParser _parser = new();

    // Real captured lines (live Player.log, 2026-05-18). Two middle ints (25, 1)
    // vary across uses; the regex skips them.
    [Theory]
    [InlineData(
        "[08:25:39] LocalPlayer: ProcessMapFx((1236.00, 38.17, 2528.00), 25, 1, \"Good Metal Slab is here\", ImportantInfo, \"The Good Metal Slab is 67m west and 1181m south.\")",
        1236.00, 38.17, 2528.00)]
    [InlineData(
        "[08:33:17] LocalPlayer: ProcessMapFx((1666.00, 36.95, 2620.00), 25, 1, \"Good Metal Slab is here\", ImportantInfo, \"The Good Metal Slab is 604m west and 1073m south.\")",
        1666.00, 36.95, 2620.00)]
    public void Parses_ProcessMapFx_absolute_world_coord(string line, double x, double y, double z)
    {
        var evt = (MapTargetDetected?)_parser.TryParse(line, DateTime.UtcNow);
        evt.Should().NotBeNull();
        evt!.World.X.Should().Be(x);
        evt.World.Y.Should().Be(y);
        evt.World.Z.Should().Be(z);
        evt.Short.Should().Be("Good Metal Slab is here");
        evt.Category.Should().Be("ImportantInfo");
        evt.Message.Should().Contain("south.");
    }

    [Fact]
    public void Parses_signed_negative_coords()
    {
        // Negative X/Z are common (Myconian Cave, Sun Vale, …) — a positive-only
        // assumption silently misprojects whole zones (see WorldCoord).
        var line =
            "[09:10:00] LocalPlayer: ProcessMapFx((-521.96, 12.00, -322.68), 7, 1, \"Iron Vein is here\", ImportantInfo, \"The Iron Vein is 5m east and 9m north.\")";
        var evt = (MapTargetDetected?)_parser.TryParse(line, DateTime.UtcNow);
        evt.Should().NotBeNull();
        evt!.World.X.Should().Be(-521.96);
        evt.World.Z.Should().Be(-322.68);
    }

    [Fact]
    public void Preserves_event_timestamp()
    {
        var ts = new DateTime(2026, 5, 18, 8, 25, 39, DateTimeKind.Utc);
        var evt = (MapTargetDetected?)_parser.TryParse(
            "LocalPlayer: ProcessMapFx((1.0, 2.0, 3.0), 1, 1, \"X is here\", Info, \"msg\")", ts);
        evt!.Timestamp.Should().Be(ts);
    }

    [Theory]
    // Motherlode is distance-only ProcessScreenText / ProcessDoDelayLoop —
    // never ProcessMapFx, so it's excluded with no special-casing.
    [InlineData("[09:03:31] LocalPlayer: ProcessScreenText(ImportantInfo, \"The treasure is 1285 meters from here.\")")]
    [InlineData("[09:03:30] LocalPlayer: ProcessDoDelayLoop(1, Unset, \"Using Kur Mountains Simple Metal Motherlode Map\", 5305, AbortIfAttacked)")]
    [InlineData("[08:25:38] LocalPlayer: ProcessDoDelayLoop(0.5, Unset, \"Using Eltibule Good Mining Survey\", 5305, AbortIfAttacked)")]
    [InlineData("LOADING LEVEL AreaEltibule")]
    [InlineData("")]
    public void Returns_null_for_unrecognised_lines(string line)
    {
        _parser.TryParse(line, DateTime.UtcNow).Should().BeNull();
    }

    // Real captured ProcessMapPinAdd lines (live Player.log, 2026-05-18).
    [Theory]
    [InlineData(
        "[08:22:22] LocalPlayer: ProcessMapPinAdd(1, 0, 0, (-521.96, 0.00, 368.39), \"\")",
        -521.96, 368.39, "")]
    [InlineData(
        "[08:32:20] LocalPlayer: ProcessMapPinAdd(1, 0, 0, (1145.39, 0.00, 1323.40), \"Calib 1\")",
        1145.39, 1323.40, "Calib 1")]
    // A label that looks like an area/verb must STILL just be a label —
    // pairing is turn-order, never by name (hard rule #454).
    [InlineData(
        "[08:32:38] LocalPlayer: ProcessMapPinAdd(1, 0, 0, (-355.16, 0.00, -392.82), \"AreaEltibule Check Survey\")",
        -355.16, -392.82, "AreaEltibule Check Survey")]
    public void Parses_ProcessMapPinAdd_world_coord_and_label(
        string line, double x, double z, string label)
    {
        var evt = (MapPinAdded?)_parser.TryParse(line, DateTime.UtcNow);
        evt.Should().NotBeNull();
        evt!.World.X.Should().Be(x);
        evt.World.Z.Should().Be(z);
        evt.Label.Should().Be(label); // captured for diagnostics only
    }
}
