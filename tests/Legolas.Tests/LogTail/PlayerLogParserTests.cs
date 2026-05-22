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

    // #606: ProcessScreenText survey-collect readout — replaces the retired
    // chat-side "[Status] X collected!" parser. The Player.log payload is
    // byte-identical to the chat line minus the "[Status] " prefix.
    [Theory]
    [InlineData("""ProcessScreenText(ImportantInfo, "Rubywall Crystal collected!")""", "Rubywall Crystal", null)]
    [InlineData("""ProcessScreenText(ImportantInfo, "Diamond collected!")""", "Diamond", null)]
    [InlineData("""ProcessScreenText(ImportantInfo, "Expert-Quality Metal Slab collected!")""", "Expert-Quality Metal Slab", null)]
    [InlineData("""[01:59:39] LocalPlayer: ProcessScreenText(ImportantInfo, "Citrine collected!")""", "Citrine", null)]
    public void Parses_item_collected_from_ProcessScreenText(string line, string expectedName, string? expectedBonus)
    {
        var evt = _parser.TryParse(line, DateTime.UtcNow);

        var ic = evt.Should().BeOfType<ItemCollected>().Subject;
        ic.Name.Should().Be(expectedName);
        ic.SpeedBonusItem.Should().Be(expectedBonus);
        ic.Count.Should().Be(1, "the Player.log collected! line never carries a count for the primary item");
    }

    [Theory]
    // Speed-bonus tail — "Also found X x<N> (speed bonus!)" parsed into the
    // SpeedBonusItem field with the count stripped. The primary item shape is
    // identical to the bonus-free case; the optional tail kicks in only when
    // PG fires it.
    [InlineData("""ProcessScreenText(ImportantInfo, "Rubywall Crystal collected! Also found Azurite x2 (speed bonus!)")""",
        "Rubywall Crystal", "Azurite")]
    [InlineData("""ProcessScreenText(ImportantInfo, "Garnet collected! Also found Fluorite (speed bonus!)")""",
        "Garnet", "Fluorite")]
    [InlineData("""ProcessScreenText(ImportantInfo, "Simple Metal Slab collected! Also found Simple Metal Slab x3 (speed bonus!)")""",
        "Simple Metal Slab", "Simple Metal Slab")]
    public void Parses_item_collected_with_speed_bonus_from_ProcessScreenText(string line, string expectedName, string expectedBonus)
    {
        var evt = _parser.TryParse(line, DateTime.UtcNow);

        var ic = evt.Should().BeOfType<ItemCollected>().Subject;
        ic.Name.Should().Be(expectedName);
        ic.SpeedBonusItem.Should().Be(expectedBonus);
    }

    [Theory]
    // Discriminator guards: other ProcessScreenText categories must NOT
    // false-positive — the regex anchors on ImportantInfo + the literal
    // "collected!" so unrelated banners (GeneralInfo / ErrorMessage) fall
    // through to null.
    [InlineData("""ProcessScreenText(GeneralInfo, "You've already looted this chest!")""")]
    [InlineData("""ProcessScreenText(ErrorMessage, "You've already milked Bessie in the past hour.")""")]
    public void Other_screen_text_categories_are_not_an_item_collected(string line)
    {
        _parser.TryParse(line, DateTime.UtcNow).Should().BeNull();
    }

    [Theory]
    // Item-collected and motherlode-distance share the ProcessScreenText
    // prefix; pin both ways so the in-branch ordering can't silently flip a
    // motherlode banner into an ItemCollected or vice versa.
    [InlineData(
        """ProcessScreenText(ImportantInfo, "The treasure is 1285 meters from here.")""")]
    public void Treasure_distance_is_not_an_item_collected(string line)
    {
        _parser.TryParse(line, DateTime.UtcNow).Should().NotBeOfType<ItemCollected>();
    }

    // #606: relative-offset helper — extracts the inline "The X is Nm DIR and
    // Mm DIR." readout from a ProcessMapFx trailing message string. Mirrors
    // the chat-retired survey-offset semantics; consumed by
    // PlayerLogIngestionService.HandleMapTarget to drive the calibration
    // verify-mode NoteSurvey hook.
    [Theory]
    [InlineData("The Bloodstone is 528m west and 202m north.", -528, 202)]
    [InlineData("The Diamond is 20m east and 14m south.", 20, -14)]
    [InlineData("The Star Sapphire is 137m north and 88m west.", -88, 137)]
    [InlineData("The Foo is 5m north and 12m east.", 12, 5)]
    public void TryParseMapFxRelativeOffset_extracts_signed_directional_offset(
        string message, int expectedEast, int expectedNorth)
    {
        var offset = PlayerLogParser.TryParseMapFxRelativeOffset(message);
        offset.Should().NotBeNull();
        offset!.Value.East.Should().Be(expectedEast);
        offset.Value.North.Should().Be(expectedNorth);
    }

    [Theory]
    [InlineData("")]
    [InlineData("random banner with no DIR tokens")]
    [InlineData("The treasure is 1285 meters from here.")]
    public void TryParseMapFxRelativeOffset_returns_null_for_non_matching_messages(string message)
    {
        PlayerLogParser.TryParseMapFxRelativeOffset(message).Should().BeNull();
    }
}
