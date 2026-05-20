using FluentAssertions;
using Mithril.Tools.LogSanitizer;
using Xunit;

namespace Mithril.Tools.LogSanitizer.Tests;

public sealed class PlayerLogRulesTests
{
    [Fact]
    public void Banner_registersOwnCharacter()
    {
        var registry = new NameRegistry();
        var rules = new PlayerLogRules();

        rules.DiscoverNames("Logged in as character Daedric", registry);

        registry.TokenFor("Daedric").Should().Be("<CHARACTER>");
    }

    [Fact]
    public void ProcessAddPlayer_registersOtherPlayer()
    {
        var registry = new NameRegistry();
        var rules = new PlayerLogRules();

        rules.DiscoverNames("[12:34:56] LocalPlayer: ProcessAddPlayer(Alice, 12345, 50.0, 30.0)", registry);

        registry.TokenFor("Alice").Should().Be("<PLAYER_1>");
    }

    [Fact]
    public void ProcessAddPlayer_ownCharacter_isMappedAsCharacter()
    {
        var registry = new NameRegistry();
        var rules = new PlayerLogRules();

        rules.DiscoverNames("Logged in as character Daedric", registry);
        rules.DiscoverNames("[12:34:56] LocalPlayer: ProcessAddPlayer(Daedric, 12345, 50.0, 30.0)", registry);

        registry.TokenFor("Daedric").Should().Be("<CHARACTER>");
        registry.AllMappings.Should().HaveCount(1);
    }

    [Fact]
    public void IrrelevantLine_noOp()
    {
        var registry = new NameRegistry();
        var rules = new PlayerLogRules();

        rules.DiscoverNames("[12:34:56] LocalPlayer: ProcessUpdateRecipe(Cooking, 5)", registry);

        registry.AllMappings.Should().BeEmpty();
    }

    [Fact]
    public void MultiplePlayers_numberedInSeenOrder()
    {
        var registry = new NameRegistry();
        var rules = new PlayerLogRules();

        rules.DiscoverNames("[12:34:56] LocalPlayer: ProcessAddPlayer(Alice, 1, 0, 0)", registry);
        rules.DiscoverNames("[12:34:57] LocalPlayer: ProcessAddPlayer(Bob, 2, 0, 0)", registry);
        rules.DiscoverNames("[12:34:58] LocalPlayer: ProcessAddPlayer(Alice, 1, 0, 0)", registry);

        registry.TokenFor("Alice").Should().Be("<PLAYER_1>");
        registry.TokenFor("Bob").Should().Be("<PLAYER_2>");
        registry.AllMappings.Should().HaveCount(2);
    }
}
