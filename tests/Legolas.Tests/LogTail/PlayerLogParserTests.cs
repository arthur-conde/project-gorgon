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
    // A non-motherlode survey delay-loop, a level marker, and the empty line
    // are all ignored by this parser. Pre-#604 the motherlode distance
    // ProcessScreenText also returned null here (it was a chat-only signal);
    // it now produces a MotherlodeDistance and is asserted in
    // MotherlodeParserTests.
    [InlineData("[08:25:38] LocalPlayer: ProcessDoDelayLoop(0.5, Unset, \"Using Eltibule Good Mining Survey\", 5305, AbortIfAttacked)")]
    [InlineData("LOADING LEVEL AreaEltibule")]
    [InlineData("")]
    public void Returns_null_for_unrecognised_lines(string line)
    {
        _parser.TryParse(line, DateTime.UtcNow).Should().BeNull();
    }

    [Fact]
    // #488: the real captured Motherlode-map use gesture. Previously ignored
    // (the parser was MapFx-only); now recognized as the pairing anchor.
    public void Recognizes_the_motherlode_map_use_gesture()
    {
        var evt = _parser.TryParse(
            "[09:03:30] LocalPlayer: ProcessDoDelayLoop(1, Unset, \"Using Kur Mountains Simple Metal Motherlode Map\", 5305, AbortIfAttacked)",
            DateTime.UtcNow);

        evt.Should().BeOfType<MotherlodeUseDetected>();
    }

    // Map-pin lifecycle parsing was promoted to the GameState-tier
    // MapPinParser (#468); this parser is MapFx-only now and must ignore
    // ProcessMapPin{Add,Remove}.
    [Theory]
    [InlineData("[08:22:22] LocalPlayer: ProcessMapPinAdd(1, 0, 0, (-521.96, 0.00, 368.39), \"\")")]
    [InlineData("[10:30:15] LocalPlayer: ProcessMapPinRemove(1, 0, 0, (784.74, 0.00, 3429.94), \"\")")]
    public void Ignores_map_pin_lines(string line)
        => _parser.TryParse(line, DateTime.UtcNow).Should().BeNull();
}
