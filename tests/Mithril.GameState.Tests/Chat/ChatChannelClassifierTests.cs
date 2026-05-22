using FluentAssertions;
using Mithril.GameState.Chat;
using Xunit;

namespace Mithril.GameState.Tests.Chat;

public sealed class ChatChannelClassifierTests
{
    [Theory]
    [InlineData("Status", ChatChannelKind.Status)]
    [InlineData("NPC Chatter", ChatChannelKind.NpcChatter)]
    [InlineData("Help", ChatChannelKind.PlayerChat)]
    [InlineData("Trade", ChatChannelKind.PlayerChat)]
    [InlineData("Local", ChatChannelKind.PlayerChat)]
    [InlineData("Whisper", ChatChannelKind.PlayerChat)]
    [InlineData("Group", ChatChannelKind.PlayerChat)]
    [InlineData("Party", ChatChannelKind.PlayerChat)]
    [InlineData("Global", ChatChannelKind.PlayerChat)]
    [InlineData("woptraders", ChatChannelKind.PlayerChat)] // user-created room
    [InlineData("SomeNewChannelPGAdded", ChatChannelKind.PlayerChat)] // unknown → catch-all
    public void Classify_maps_channel_name_to_bucket(string channel, ChatChannelKind expected)
    {
        ChatChannelClassifier.Classify(channel).Should().Be(expected);
    }

    [Fact]
    public void TryParse_extracts_channel_speaker_text_from_canonical_line()
    {
        var ok = ChatChannelClassifier.TryParse(
            "26-04-22 02:39:57\t[Nearby] Hikaratu: PRYSGWIMLIK", out var parts);
        ok.Should().BeTrue();
        parts.Channel.Should().Be("Nearby");
        parts.Speaker.Should().Be("Hikaratu");
        parts.Text.Should().Be("PRYSGWIMLIK");
    }

    [Fact]
    public void TryParse_handles_user_room_channel_name()
    {
        // User-created rooms (whose names can be arbitrary words) still parse.
        var ok = ChatChannelClassifier.TryParse(
            "26-04-23 00:07:51\t[woptraders] Endracos: WTS", out var parts);
        ok.Should().BeTrue();
        parts.Channel.Should().Be("woptraders");
        parts.Speaker.Should().Be("Endracos");
        parts.Text.Should().Be("WTS");
    }

    [Fact]
    public void TryParse_returns_false_for_continuation_line_without_prefix()
    {
        // Embedded entity-reference continuation; producer aggregates these
        // into the parent message, not the classifier.
        var ok = ChatChannelClassifier.TryParse("[Item: Bee Lover's Bouquet]", out _);
        ok.Should().BeFalse();
    }

    [Fact]
    public void TryParse_returns_false_for_blank_line()
    {
        ChatChannelClassifier.TryParse("", out _).Should().BeFalse();
        ChatChannelClassifier.TryParse("   ", out _).Should().BeFalse();
    }

    [Fact]
    public void TryParse_falls_back_to_no_speaker_form_when_colon_missing()
    {
        // Some channels emit bare-header lines without a speaker payload.
        var ok = ChatChannelClassifier.TryParse(
            "26-04-22 00:00:01\t[Status] The Citrine is 1731m east.", out var parts);
        ok.Should().BeTrue();
        parts.Channel.Should().Be("Status");
        // The regex variant matches Speaker:text first; "The Citrine is 1731m east." has no colon
        // so the fallback fires with speaker="" and text=full body.
        parts.Speaker.Should().Be(string.Empty);
        parts.Text.Should().Be("The Citrine is 1731m east.");
    }
}
