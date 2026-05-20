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

        rules.DiscoverNames("[20:01:14] Logged in as character Daedric. Time UTC=05/19/2026 20:01:14. Timezone Offset 01:00:00", registry);

        registry.TokenFor("Daedric").Should().Be("<CHARACTER>");
    }

    [Fact]
    public void ProcessAddPlayer_registersOtherPlayer()
    {
        var registry = new NameRegistry();
        var rules = new PlayerLogRules();

        // Real Player.log ProcessAddPlayer has shape: (entity_id, sub_id, "@appearance", "Name", "Description", ...).
        // The player name is the 4th argument — the second quoted string. The first quoted string is the
        // appearance blob (starts with "@"). The regex must skip past the appearance to capture the name.
        rules.DiscoverNames(@"[12:34:56] LocalPlayer: ProcessAddPlayer(-1107394649, 25042203, ""@Base2-m(sex=m)"", ""Alice"", ""A player!"", System.String[], (1,2,3))", registry);

        registry.TokenFor("Alice").Should().Be("<PLAYER_1>");
    }

    [Fact]
    public void ProcessAddPlayer_ownCharacter_isMappedAsCharacter()
    {
        var registry = new NameRegistry();
        var rules = new PlayerLogRules();

        rules.DiscoverNames("[20:01:14] Logged in as character Daedric. Time UTC=05/19/2026 20:01:14. Timezone Offset 01:00:00", registry);
        rules.DiscoverNames(@"[12:34:56] LocalPlayer: ProcessAddPlayer(-1107394649, 25042203, ""@Base2-m(sex=m)"", ""Daedric"", ""A player!"", System.String[], (1,2,3))", registry);

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

        rules.DiscoverNames(@"[12:34:56] LocalPlayer: ProcessAddPlayer(-1, 2, ""@a"", ""Alice"", ""x"")", registry);
        rules.DiscoverNames(@"[12:34:57] LocalPlayer: ProcessAddPlayer(-2, 2, ""@a"", ""Bob"", ""x"")", registry);
        rules.DiscoverNames(@"[12:34:58] LocalPlayer: ProcessAddPlayer(-1, 2, ""@a"", ""Alice"", ""x"")", registry);

        registry.TokenFor("Alice").Should().Be("<PLAYER_1>");
        registry.TokenFor("Bob").Should().Be("<PLAYER_2>");
        registry.AllMappings.Should().HaveCount(2);
    }

    [Fact]
    public void Banner_alreadySanitized_isNoOp()
    {
        var registry = new NameRegistry();
        var rules = new PlayerLogRules();

        rules.DiscoverNames("[20:01:14] Logged in as character <CHARACTER>. Time UTC=05/19/2026 20:01:14", registry);

        registry.AllMappings.Should().BeEmpty();
    }

    [Fact]
    public void ProcessAddPlayer_alreadySanitized_isNoOp()
    {
        var registry = new NameRegistry();
        var rules = new PlayerLogRules();

        rules.DiscoverNames(@"[12:34:56] LocalPlayer: ProcessAddPlayer(-1, 2, ""@a"", ""<PLAYER_1>"", ""x"")", registry);

        registry.AllMappings.Should().BeEmpty();
    }
}
