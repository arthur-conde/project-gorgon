using FluentAssertions;
using Legolas.Domain;
using Legolas.Services;
using Xunit;

namespace Legolas.Tests.LogTail;

/// <summary>
/// Post-Arda migration: <see cref="PlayerLogParser"/> is now a static helper
/// class. The Arda world driver handles the primary log-line verb parsing;
/// these tests cover the module-side secondary pattern extraction:
/// <list type="bullet">
///   <item><see cref="PlayerLogParser.TryParseMapFxRelativeOffset"/> —
///   directional offset from <c>MapFxObserved.Message</c>.</item>
///   <item><see cref="PlayerLogParser.TryParseItemCollected"/> —
///   survey collect readout from <c>ScreenTextObserved.Text</c>.</item>
///   <item><see cref="PlayerLogParser.TryParseMotherlodeDistance"/> —
///   distance readout from <c>ScreenTextObserved.Text</c>.</item>
///   <item><see cref="PlayerLogParser.IsMotherlodeMapText"/> —
///   motherlode-map discriminator for <c>DelayLoopStarted.Text</c>.</item>
///   <item><see cref="PlayerLogParser.NormalizeMapName"/> —
///   "Using X" → "X" name normalizer.</item>
/// </list>
/// </summary>
public class PlayerLogParserTests
{
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

    // ---- TryParseItemCollected (ScreenTextObserved.Text) -----------------
    //
    // Post-#824: the parser carries both the primary item count and the
    // speed-bonus count (extracted from the literal "xN" tail emitted by the
    // game). Implicit count is 1 when "xN" is absent. ScreenText is the
    // single source of truth for survey-mode attribution — the inventory
    // bus drops counts on stack-onto-existing and always emits StackSize = 1
    // on fresh-instance adds.

    [Theory]
    [InlineData("Rubywall Crystal collected!", "Rubywall Crystal", 1, null, 0)]
    [InlineData("Diamond collected!", "Diamond", 1, null, 0)]
    [InlineData("Expert-Quality Metal Slab collected!", "Expert-Quality Metal Slab", 1, null, 0)]
    [InlineData("Citrine collected!", "Citrine", 1, null, 0)]
    public void TryParseItemCollected_extracts_name(
        string text, string expectedName, int expectedCount, string? expectedBonus, int expectedBonusCount)
    {
        var result = PlayerLogParser.TryParseItemCollected(text);
        result.Should().NotBeNull();
        result!.Value.Name.Should().Be(expectedName);
        result.Value.Count.Should().Be(expectedCount);
        result.Value.BonusName.Should().Be(expectedBonus);
        result.Value.BonusCount.Should().Be(expectedBonusCount);
    }

    [Theory]
    [InlineData("Rubywall Crystal collected! Also found Amethyst x2 (speed bonus!)",
        "Rubywall Crystal", 1, "Amethyst", 2)]
    [InlineData("Sapphire x3 collected! Also found Lapis Lazuli x3 (speed bonus!)",
        "Sapphire", 3, "Lapis Lazuli", 3)]
    [InlineData("Garnet collected! Also found Fluorite (speed bonus!)",
        "Garnet", 1, "Fluorite", 1)]
    [InlineData("Simple Metal Slab collected! Also found Simple Metal Slab x3 (speed bonus!)",
        "Simple Metal Slab", 1, "Simple Metal Slab", 3)]
    [InlineData("Piece of Green Glass x2 collected!",
        "Piece of Green Glass", 2, null, 0)]
    public void TryParseItemCollected_extracts_counts(
        string text, string expectedName, int expectedCount, string? expectedBonus, int expectedBonusCount)
    {
        var result = PlayerLogParser.TryParseItemCollected(text);
        result.Should().NotBeNull();
        result!.Value.Name.Should().Be(expectedName);
        result.Value.Count.Should().Be(expectedCount);
        result.Value.BonusName.Should().Be(expectedBonus);
        result.Value.BonusCount.Should().Be(expectedBonusCount);
    }

    [Theory]
    [InlineData("Wax Sculpture collected!", "Wax Sculpture", 1)]
    [InlineData("Wax Sculpture x2 collected!", "Wax Sculpture", 2)]
    public void TryParseItemCollected_handles_item_names_containing_x(
        string text, string expectedName, int expectedCount)
    {
        var result = PlayerLogParser.TryParseItemCollected(text);
        result.Should().NotBeNull();
        result!.Value.Name.Should().Be(expectedName);
        result.Value.Count.Should().Be(expectedCount);
    }

    [Theory]
    [InlineData("You've already looted this chest!")]
    [InlineData("You've already milked Bessie in the past hour.")]
    [InlineData("The treasure is 1285 meters from here.")]
    [InlineData("")]
    public void TryParseItemCollected_returns_null_for_non_matching(string text)
    {
        PlayerLogParser.TryParseItemCollected(text).Should().BeNull();
    }

    // ---- TryParseMotherlodeDistance (ScreenTextObserved.Text) ------------

    [Theory]
    [InlineData("The treasure is 1285 meters from here.", 1285)]
    [InlineData("The treasure is 67 metres from here.", 67)]
    [InlineData("The treasure is 940 meters from here.", 940)]
    public void TryParseMotherlodeDistance_extracts_metres(string text, int expected)
    {
        PlayerLogParser.TryParseMotherlodeDistance(text).Should().Be(expected);
    }

    [Theory]
    [InlineData("You've already looted this chest!")]
    [InlineData("Diamond collected!")]
    [InlineData("")]
    public void TryParseMotherlodeDistance_returns_null_for_non_matching(string text)
    {
        PlayerLogParser.TryParseMotherlodeDistance(text).Should().BeNull();
    }

    // ---- IsMotherlodeMapText (DelayLoopStarted.Text) --------------------

    [Theory]
    [InlineData("Using Kur Mountains Simple Metal Motherlode Map")]
    [InlineData("Using Good Metal Slab Motherlode Map")]
    [InlineData("Using Motherlode Map")]
    public void IsMotherlodeMapText_matches_motherlode_maps(string text)
    {
        PlayerLogParser.IsMotherlodeMapText(text.AsSpan()).Should().BeTrue();
    }

    [Theory]
    [InlineData("Using Eltibule Good Mining Survey")]
    [InlineData("Using Ranalon Salad")]
    [InlineData("Collecting Fruit...")]
    [InlineData("")]
    public void IsMotherlodeMapText_rejects_non_motherlode(string text)
    {
        PlayerLogParser.IsMotherlodeMapText(text.AsSpan()).Should().BeFalse();
    }

    // ---- NormalizeMapName ------------------------------------------------

    [Theory]
    [InlineData("Using Kur Mountains Simple Metal Motherlode Map", "Kur Mountains Simple Metal Motherlode Map")]
    [InlineData("Kur Mountains Simple Metal Motherlode Map", "Kur Mountains Simple Metal Motherlode Map")]
    [InlineData("  Using  Good Map  ", "Good Map")]
    public void NormalizeMapName_strips_using_prefix(string raw, string expected)
    {
        PlayerLogParser.NormalizeMapName(raw).Should().Be(expected);
    }
}
