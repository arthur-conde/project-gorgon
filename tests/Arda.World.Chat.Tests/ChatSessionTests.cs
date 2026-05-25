using Arda.Abstractions.Logs;
using Arda.Dispatch;
using Arda.World.Chat.Events;
using Arda.World.Chat.Internal;
using FluentAssertions;
using Xunit;

namespace Arda.World.Chat.Tests;

public class ChatSessionTests
{
    private readonly SpyEventBus _bus = new();
    private readonly ChatSession _session;

    public ChatSessionTests()
    {
        _session = new ChatSession(_bus);
    }

    private static LogLineMetadata Meta() =>
        new(DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, false);

    [Fact]
    public void ParsesBanner_WithStandardFormat()
    {
        var line = "**************************************** Logged In As Emraell. Server Laeth. Timezone Offset 01:00:00.";
        _session.Handle(line.AsSpan(), line, Meta());

        _session.Character.Should().Be("Emraell");
        _session.Server.Should().Be("Laeth");
        _session.TimezoneOffset.Should().Be(TimeSpan.FromHours(1));

        var evt = _bus.Published<ChatSessionIdentified>().Should().ContainSingle().Which;
        evt.Character.Should().Be("Emraell");
        evt.Server.Should().Be("Laeth");
        evt.TimezoneOffset.Should().Be(TimeSpan.FromHours(1));
    }

    [Fact]
    public void ParsesBanner_NegativeOffset()
    {
        var line = "**** Logged In As TestChar. Server Kemel. Timezone Offset -05:00:00.";
        _session.Handle(line.AsSpan(), line, Meta());

        _session.Character.Should().Be("TestChar");
        _session.Server.Should().Be("Kemel");
        _session.TimezoneOffset.Should().Be(TimeSpan.FromHours(-5));
    }

    [Fact]
    public void ParsesBanner_ZeroOffset()
    {
        var line = "**** Logged In As Someone. Server Laeth. Timezone Offset 00:00:00.";
        _session.Handle(line.AsSpan(), line, Meta());

        _session.TimezoneOffset.Should().Be(TimeSpan.Zero);
    }

    [Fact]
    public void SubsequentBanner_UpdatesState()
    {
        var line1 = "**** Logged In As CharA. Server Laeth. Timezone Offset 01:00:00.";
        _session.Handle(line1.AsSpan(), line1, Meta());

        var line2 = "**** Logged In As CharB. Server Kemel. Timezone Offset -03:00:00.";
        _session.Handle(line2.AsSpan(), line2, Meta());

        _session.Character.Should().Be("CharB");
        _session.Server.Should().Be("Kemel");
        _bus.Published<ChatSessionIdentified>().Should().HaveCount(2);
    }

    [Fact]
    public void MalformedBanner_NoLoggedIn_IsIgnored()
    {
        var line = "**** Some other starred line";
        _session.Handle(line.AsSpan(), line, Meta());

        _session.Character.Should().BeNull();
        _bus.Published<ChatSessionIdentified>().Should().BeEmpty();
    }
}
