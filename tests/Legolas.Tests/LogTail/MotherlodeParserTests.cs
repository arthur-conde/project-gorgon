using FluentAssertions;
using Legolas.Domain;
using Legolas.Services;

namespace Legolas.Tests.LogTail;

/// <summary>
/// #488 — the Player.log ProcessDoDelayLoop use gesture. #604 — the
/// Player.log ProcessScreenText distance readout (motherlode coordinator pairs
/// request + response from a single source). #606 — chat-side parser retired
/// alongside the rest of the Legolas chat consumption.
/// </summary>
public class MotherlodeParserTests
{
    private static readonly DateTime FixedTime = new(2026, 4, 14, 12, 0, 0, DateTimeKind.Utc);
    private readonly PlayerLogParser _player = new();

    [Theory]
    // Live capture (US spelling, with trailing period) and the British spelling
    // accepted for robustness. The full LocalPlayer: actor envelope is stripped
    // by L0.5 upstream, so the parser receives the bare ProcessScreenText
    // payload — pin both shapes (with and without an envelope-style prefix) so
    // a future upstream tweak can't silently regress.
    [InlineData("""ProcessScreenText(ImportantInfo, "The treasure is 1285 meters from here.")""", 1285)]
    [InlineData("""ProcessScreenText(ImportantInfo, "The treasure is 67 metres from here.")""", 67)]
    [InlineData("""[09:03:31] LocalPlayer: ProcessScreenText(ImportantInfo, "The treasure is 940 meters from here.")""", 940)]
    public void Parses_motherlode_distance(string line, int expected)
    {
        var evt = _player.TryParse(line, FixedTime);

        evt.Should().BeOfType<MotherlodeDistance>()
           .Which.DistanceMetres.Should().Be(expected);
    }

    [Theory]
    // Other ProcessScreenText categories must NOT false-positive — the
    // discriminator anchors on ImportantInfo + the literal banner text.
    [InlineData("""ProcessScreenText(GeneralInfo, "You've already looted this chest! (It will refill 3 hours after you looted it.)")""")]
    [InlineData("""ProcessScreenText(ErrorMessage, "You've already milked Bessie in the past hour.")""")]
    // A "[Status]" chat-shaped line must NOT match the Player.log parser at
    // all — post-#606 the chat parser has been deleted; the survey grammar
    // now flows through ProcessMapFx (placement) and ProcessScreenText (collect).
    [InlineData("[Status] The Bloodstone is 528m west and 202m north.")]
    public void Other_screen_text_categories_are_not_a_motherlode_distance(string line)
    {
        var evt = _player.TryParse(line, FixedTime);

        (evt is null || evt is not MotherlodeDistance).Should().BeTrue();
    }

    [Theory]
    [InlineData("""[12:00:00] LocalPlayer: ProcessDoDelayLoop(3, UseItem, "Using Good Metal Slab Motherlode Map", 0, AbortIfAttacked, IsInteractorDelayLoop)""")]
    [InlineData("""LocalPlayer: ProcessDoDelayLoop(1.5, UseItem, "Using Motherlode Map", 4821, AbortIfAttacked)""")]
    public void Parses_motherlode_use_gesture(string line)
    {
        _player.TryParse(line, FixedTime).Should().BeOfType<MotherlodeUseDetected>();
    }

    [Theory]
    // Non-motherlode delay loops aren't a use gesture — the Player.log parser
    // returns null for them (it emits MapFx targets, the use gesture, the
    // #604 ProcessScreenText distance readout, and the #606 ProcessScreenText
    // item-collected readout).
    [InlineData("""LocalPlayer: ProcessDoDelayLoop(1.5, Eat, "Using Ranalon Salad", 5820, AbortIfAttacked)""")]
    [InlineData("""LocalPlayer: ProcessDoDelayLoop(3, Gather, "Collecting Fruit...", 0, AbortIfAttacked, IsInteractorDelayLoop)""")]
    public void Other_delay_loops_are_not_a_use_gesture(string line)
    {
        _player.TryParse(line, FixedTime).Should().BeNull();
    }
}
