using FluentAssertions;
using Legolas.Domain;
using Legolas.Services;

namespace Legolas.Tests.LogTail;

/// <summary>
/// #488 — the ChatLog motherlode-distance discriminator (no DIR token ⇒
/// motherlode) and the Player.log ProcessDoDelayLoop use gesture.
/// </summary>
public class MotherlodeParserTests
{
    private static readonly DateTime FixedTime = new(2026, 4, 14, 12, 0, 0, DateTimeKind.Utc);
    private readonly ChatLogParser _chat = new();
    private readonly PlayerLogParser _player = new();

    [Theory]
    [InlineData("The treasure is 1285 meters from here.", 1285)]   // live capture (US spelling)
    [InlineData("The treasure is 67 metres from here", 67)]        // British spelling
    [InlineData("[Status] The treasure is 940 meters from here.", 940)]
    public void Parses_motherlode_distance(string line, int expected)
    {
        var evt = _chat.TryParse(line, FixedTime);

        evt.Should().BeOfType<MotherlodeDistance>()
           .Which.DistanceMetres.Should().Be(expected);
    }

    [Theory]
    // A regular survey [Status] line carries DIR tokens — it must NOT be
    // mistaken for a motherlode distance (the no-DIR discriminator).
    [InlineData("[Status] The Bloodstone is 528m west and 202m north.")]
    [InlineData("[Status] The Diamond is 20m east and 14m south.")]
    public void Survey_status_line_is_not_a_motherlode_distance(string line)
    {
        var evt = _chat.TryParse(line, FixedTime);

        evt.Should().BeOfType<SurveyDetected>();
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
    // returns null for them (it only emits MapFx targets + the use gesture).
    [InlineData("""LocalPlayer: ProcessDoDelayLoop(1.5, Eat, "Using Ranalon Salad", 5820, AbortIfAttacked)""")]
    [InlineData("""LocalPlayer: ProcessDoDelayLoop(3, Gather, "Collecting Fruit...", 0, AbortIfAttacked, IsInteractorDelayLoop)""")]
    public void Other_delay_loops_are_not_a_use_gesture(string line)
    {
        _player.TryParse(line, FixedTime).Should().BeNull();
    }
}
