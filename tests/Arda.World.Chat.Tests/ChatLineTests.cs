using Arda.Abstractions.Logs;
using Arda.Contracts;
using Arda.Dispatch;
using Arda.World.Chat.Events;
using Arda.World.Chat.Internal;
using FluentAssertions;
using Xunit;

namespace Arda.World.Chat.Tests;

public class ChatLineTests
{
    private readonly SpyEventBus _bus = new();
    private readonly ChatLine _handler;

    public ChatLineTests()
    {
        _handler = new ChatLine(_bus);
    }

    private static LogLineMetadata Meta() =>
        new(DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, false);

    [Fact]
    public void ParsesStandardChatLine()
    {
        var line = "[Trade] Emraell: WTS something";
        _handler.Handle(line.AsSpan(), default, line, Meta());

        var evt = _bus.Published<PlayerChatLine>().Should().ContainSingle().Which;
        evt.Channel.Should().Be("Trade");
        evt.Speaker.Should().Be("Emraell");
        evt.Text.ToString().Should().Be("WTS something");
    }

    [Fact]
    public void ParsesGlobalChannel()
    {
        var line = "[Global] SomePlayer: hi everyone";
        _handler.Handle(line.AsSpan(), default, line, Meta());

        var evt = _bus.Published<PlayerChatLine>().Should().ContainSingle().Which;
        evt.Channel.Should().Be("Global");
        evt.Speaker.Should().Be("SomePlayer");
        evt.Text.ToString().Should().Be("hi everyone");
    }

    [Fact]
    public void TextWithColons_PreservesFullText()
    {
        var line = "[Guild] Leader: Meetup at 8:00 PM: bring food";
        _handler.Handle(line.AsSpan(), default, line, Meta());

        var evt = _bus.Published<PlayerChatLine>().Should().ContainSingle().Which;
        evt.Speaker.Should().Be("Leader");
        evt.Text.ToString().Should().Be("Meetup at 8:00 PM: bring food");
    }

    [Fact]
    public void NoSpeakerSeparator_IsIgnored()
    {
        var line = "[Status] Some status message without colon-space";
        _handler.Handle(line.AsSpan(), default, line, Meta());

        _bus.Published<PlayerChatLine>().Should().BeEmpty();
    }

    [Fact]
    public void EmptyChannel_IsIgnored()
    {
        var line = "[] Someone: text";
        _handler.Handle(line.AsSpan(), default, line, Meta());

        _bus.Published<PlayerChatLine>().Should().BeEmpty();
    }
}
