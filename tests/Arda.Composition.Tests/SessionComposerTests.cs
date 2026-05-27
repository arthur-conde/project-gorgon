using Arda.Abstractions.Logs;
using Arda.Composition.Internal;
using Arda.Dispatch;
using Arda.World.Chat.Events;
using Arda.World.Player.Events;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Arda.Composition.Tests;

public class SessionComposerTests : IDisposable
{
    private readonly DomainEventBus _bus = new(NullLogger<DomainEventBus>.Instance);
    private readonly SessionComposer _composer;
    private readonly List<SessionEstablished> _established = [];

    private static readonly DateTimeOffset T0 = new(2026, 5, 26, 12, 0, 0, TimeSpan.Zero);

    public SessionComposerTests()
    {
        _composer = new SessionComposer(_bus);
        _bus.Subscribe<SessionEstablished>(e => _established.Add(e));
    }

    public void Dispose() => _composer.Dispose();

    private static LogLineMetadata Meta(DateTimeOffset ts) =>
        new(Timestamp: ts, ReadOn: ts, IsReplay: false);

    // ── SessionStarted alone ──────────────────────────────────────────────

    [Fact]
    public void SessionStarted_Alone_BuildsSessionWithoutServer()
    {
        _bus.Publish(new SessionStarted("Alice", Meta(T0)));

        _composer.Current.Should().NotBeNull();
        _composer.Current!.Value.CharacterName.Should().Be("Alice");
        _composer.Current!.Value.Server.Should().BeNull();
        _established.Should().ContainSingle();
    }

    [Fact]
    public void SessionStarted_Alone_GeneratesSessionId()
    {
        _bus.Publish(new SessionStarted("Alice", Meta(T0)));

        _composer.Current!.Value.SessionId.Should().Be("Alice:20260526120000");
    }

    // ── ChatSessionIdentified alone ───────────────────────────────────────

    [Fact]
    public void ChatSessionIdentified_Alone_DoesNotBuild()
    {
        _bus.Publish(new ChatSessionIdentified("Alice", "TestServer", TimeSpan.FromHours(-5), Meta(T0)));

        _composer.Current.Should().BeNull("character+loggedInAt is only set by SessionStarted");
        _established.Should().BeEmpty();
    }

    // ── Fusion: SessionStarted + ChatSessionIdentified ────────────────────

    [Fact]
    public void SessionStarted_ThenChat_FusesServerAndTimezone()
    {
        _bus.Publish(new SessionStarted("Alice", Meta(T0)));
        _bus.Publish(new ChatSessionIdentified("Alice", "TestServer", TimeSpan.FromHours(-5), Meta(T0.AddSeconds(1))));

        _composer.Current.Should().NotBeNull();
        _composer.Current!.Value.CharacterName.Should().Be("Alice");
        _composer.Current!.Value.Server.Should().Be("TestServer");
        _composer.Current!.Value.TimezoneOffset.Should().Be(TimeSpan.FromHours(-5));
        _established.Should().HaveCount(2, "each source triggers a build attempt");
    }

    [Fact]
    public void ChatSessionIdentified_ThenSession_FusesCorrectly()
    {
        _bus.Publish(new ChatSessionIdentified("Alice", "TestServer", TimeSpan.FromHours(-5), Meta(T0)));
        _bus.Publish(new SessionStarted("Alice", Meta(T0.AddSeconds(1))));

        _composer.Current.Should().NotBeNull();
        _composer.Current!.Value.CharacterName.Should().Be("Alice");
        _composer.Current!.Value.Server.Should().Be("TestServer");
        _established.Should().ContainSingle("only the second event triggers build (first has no loggedInAt)");
    }

    // ── Server fallback ──────────────────────────────────────────────────

    [Fact]
    public void ServerFallback_UsedWhenNoChatBanner()
    {
        using var composer = new SessionComposer(_bus, serverFallback: () => "FallbackServer");
        _bus.Publish(new SessionStarted("Bob", Meta(T0)));

        composer.Current.Should().NotBeNull();
        composer.Current!.Value.Server.Should().Be("FallbackServer");
    }

    [Fact]
    public void ServerFallback_IgnoredWhenChatBannerPresent()
    {
        using var composer = new SessionComposer(_bus, serverFallback: () => "FallbackServer");
        _bus.Publish(new ChatSessionIdentified("Bob", "RealServer", TimeSpan.Zero, Meta(T0)));
        _bus.Publish(new SessionStarted("Bob", Meta(T0.AddSeconds(1))));

        composer.Current!.Value.Server.Should().Be("RealServer");
    }

    // ── Chat provides character fallback ──────────────────────────────────

    [Fact]
    public void ChatSession_ProvidesCharacterFallback_WhenSessionStartedHasNot()
    {
        _bus.Publish(new ChatSessionIdentified("ChatAlice", "TestServer", TimeSpan.Zero, Meta(T0)));

        _composer.Current.Should().BeNull("loggedInAt is required");
    }

    // ── StateChanged event ───────────────────────────────────────────────

    [Fact]
    public void StateChanged_FiredOnBuild()
    {
        var fired = false;
        _composer.StateChanged += () => fired = true;

        _bus.Publish(new SessionStarted("Alice", Meta(T0)));

        fired.Should().BeTrue();
    }

    // ── Metadata forwarding ──────────────────────────────────────────────

    [Fact]
    public void SessionEstablished_CarriesMetadata()
    {
        var meta = new LogLineMetadata(T0, T0, IsReplay: true);
        _bus.Publish(new SessionStarted("Alice", meta));

        _established.Should().ContainSingle();
        _established[0].Metadata.IsReplay.Should().BeTrue();
        _established[0].Metadata.Timestamp.Should().Be(T0);
    }

    // ── Dispose ──────────────────────────────────────────────────────────

    [Fact]
    public void Dispose_StopsSubscriptions()
    {
        _composer.Dispose();

        _bus.Publish(new SessionStarted("Alice", Meta(T0)));

        _composer.Current.Should().BeNull();
        _established.Should().BeEmpty();
    }
}
