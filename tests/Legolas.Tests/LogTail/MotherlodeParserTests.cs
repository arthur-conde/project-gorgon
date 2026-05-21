using FluentAssertions;
using Legolas.Domain;
using Legolas.Services;

namespace Legolas.Tests.LogTail;

/// <summary>
/// #488 — the Player.log ProcessDoDelayLoop use gesture. #604 — the
/// Player.log ProcessScreenText distance readout (migrated here from
/// ChatLogParser so the motherlode coordinator pairs request + response from a
/// single source).
/// </summary>
public class MotherlodeParserTests
{
    private static readonly DateTime FixedTime = new(2026, 4, 14, 12, 0, 0, DateTimeKind.Utc);
    private readonly ChatLogParser _chat = new();
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
    // A survey [Status] line in chat that mentions "treasure" must NOT match
    // the Player.log parser at all — that grammar lives in ChatLogParser and
    // the regex literal of the motherlode banner here doesn't even share a
    // prefix with chat lines. Pinned so a refactor can't widen accidentally.
    [InlineData("[Status] The Bloodstone is 528m west and 202m north.")]
    public void Other_screen_text_categories_are_not_a_motherlode_distance(string line)
    {
        var evt = _player.TryParse(line, FixedTime);

        // Player.log parser returns null on non-matches; chat ChatLogParser
        // produces an UnknownLine. Either way, never a MotherlodeDistance.
        (evt is null || evt is not MotherlodeDistance).Should().BeTrue();
    }

    [Theory]
    // A regular survey [Status] line carries DIR tokens — chat parser routes
    // it to SurveyDetected; pinning here so the chat-side regex isn't
    // confused for a motherlode emitter post-#604.
    [InlineData("[Status] The Bloodstone is 528m west and 202m north.")]
    [InlineData("[Status] The Diamond is 20m east and 14m south.")]
    public void Survey_status_line_is_a_survey_not_a_motherlode_distance(string line)
    {
        var evt = _chat.TryParse(line, FixedTime);

        evt.Should().BeOfType<SurveyDetected>();
        evt.Should().NotBeOfType<MotherlodeDistance>();
    }

    [Theory]
    // Post-#604: the chat parser no longer emits MotherlodeDistance — the
    // grammar moved to PlayerLogParser. A "[Status] The treasure is N meters
    // from here." line now falls through to UnknownLine on the chat side.
    [InlineData("[Status] The treasure is 1285 meters from here.")]
    [InlineData("The treasure is 67 metres from here.")]
    public void Chat_treasure_line_no_longer_emits_motherlode_distance(string line)
    {
        var evt = _chat.TryParse(line, FixedTime);

        evt.Should().NotBeOfType<MotherlodeDistance>();
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
    // returns null for them (it only emits MapFx targets + the use gesture
    // and the #604 ProcessScreenText distance readout).
    [InlineData("""LocalPlayer: ProcessDoDelayLoop(1.5, Eat, "Using Ranalon Salad", 5820, AbortIfAttacked)""")]
    [InlineData("""LocalPlayer: ProcessDoDelayLoop(3, Gather, "Collecting Fruit...", 0, AbortIfAttacked, IsInteractorDelayLoop)""")]
    public void Other_delay_loops_are_not_a_use_gesture(string line)
    {
        _player.TryParse(line, FixedTime).Should().BeNull();
    }
}
