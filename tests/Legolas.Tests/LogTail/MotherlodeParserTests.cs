using FluentAssertions;
using Legolas.Services;

namespace Legolas.Tests.LogTail;

/// <summary>
/// Post-Arda migration: motherlode distance and use-gesture parsing is now
/// covered by the static helpers on <see cref="PlayerLogParser"/>. This file
/// tests the same patterns the old full-line regex tests covered, but against
/// the text-only helpers that operate on Arda event payloads.
/// </summary>
public class MotherlodeParserTests
{
    [Theory]
    [InlineData("The treasure is 1285 meters from here.", 1285)]
    [InlineData("The treasure is 67 metres from here.", 67)]
    [InlineData("The treasure is 940 meters from here.", 940)]
    public void Parses_motherlode_distance(string text, int expected)
    {
        PlayerLogParser.TryParseMotherlodeDistance(text).Should().Be(expected);
    }

    [Theory]
    [InlineData("You've already looted this chest!")]
    [InlineData("You've already milked Bessie in the past hour.")]
    [InlineData("The Bloodstone is 528m west and 202m north.")]
    public void Other_texts_are_not_a_motherlode_distance(string text)
    {
        PlayerLogParser.TryParseMotherlodeDistance(text).Should().BeNull();
    }

    [Theory]
    [InlineData("Using Good Metal Slab Motherlode Map")]
    [InlineData("Using Motherlode Map")]
    public void Detects_motherlode_use_gesture(string text)
    {
        PlayerLogParser.IsMotherlodeMapText(text.AsSpan()).Should().BeTrue();
    }

    [Theory]
    [InlineData("Using Ranalon Salad")]
    [InlineData("Collecting Fruit...")]
    public void Other_delay_loops_are_not_a_use_gesture(string text)
    {
        PlayerLogParser.IsMotherlodeMapText(text.AsSpan()).Should().BeFalse();
    }
}
