using FluentAssertions;
using Mithril.Tools.LogSanitizer;
using Xunit;

namespace Mithril.Tools.LogSanitizer.Tests;

public sealed class NameRegistryTests
{
    [Fact]
    public void OwnCharacter_alwaysMapsToCharacterToken()
    {
        var registry = new NameRegistry();
        registry.RegisterOwnCharacter("Daedric");

        registry.TokenFor("Daedric").Should().Be("<CHARACTER>");
    }

    [Fact]
    public void OtherPlayers_numberedInFirstSeenOrder()
    {
        var registry = new NameRegistry();
        registry.RegisterOtherPlayer("Alice");
        registry.RegisterOtherPlayer("Bob");
        registry.RegisterOtherPlayer("Carol");

        registry.TokenFor("Alice").Should().Be("<PLAYER_1>");
        registry.TokenFor("Bob").Should().Be("<PLAYER_2>");
        registry.TokenFor("Carol").Should().Be("<PLAYER_3>");
    }

    [Fact]
    public void RegisteringSamePlayerTwice_reusesToken()
    {
        var registry = new NameRegistry();
        registry.RegisterOtherPlayer("Alice");
        registry.RegisterOtherPlayer("Alice");

        registry.TokenFor("Alice").Should().Be("<PLAYER_1>");
        registry.AllMappings.Should().HaveCount(1);
    }

    [Fact]
    public void OwnCharacter_takesPrecedenceOverPlayer()
    {
        var registry = new NameRegistry();
        registry.RegisterOtherPlayer("Daedric");
        registry.RegisterOwnCharacter("Daedric");

        registry.TokenFor("Daedric").Should().Be("<CHARACTER>");
    }

    [Fact]
    public void TokenFor_unknownName_returnsNull()
    {
        var registry = new NameRegistry();

        registry.TokenFor("Stranger").Should().BeNull();
    }
}
