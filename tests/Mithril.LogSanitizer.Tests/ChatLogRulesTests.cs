using FluentAssertions;
using Mithril.Tools.LogSanitizer;
using Xunit;

namespace Mithril.Tools.LogSanitizer.Tests;

public sealed class ChatLogRulesTests
{
    [Fact]
    public void ChatLine_registersPlayer()
    {
        var registry = new NameRegistry();
        var rules = new ChatLogRules();

        rules.DiscoverNames("26-05-19 12:34:56\t[Global] Alice: anyone selling moonstones?", registry);

        registry.TokenFor("Alice").Should().Be("<PLAYER_1>");
    }

    [Fact]
    public void ChatBanner_registersOwnCharacter()
    {
        // The chat-side banner is "**** Logged In As <Name>. Server <X>. ..." (Title Case).
        // Player.log uses "Logged in as character <Name>." (lowercase), so this is a separate signal.
        var registry = new NameRegistry();
        var rules = new ChatLogRules();

        rules.DiscoverNames("26-04-09 19:29:36\t**************************************** Logged In As Daedric. Server Laeth. Timezone Offset 01:00:00.", registry);

        registry.TokenFor("Daedric").Should().Be("<CHARACTER>");
    }

    [Fact]
    public void SystemLine_noOp()
    {
        // System lines are "****"-prefixed area announcements, never carry player speech.
        var registry = new NameRegistry();
        var rules = new ChatLogRules();

        rules.DiscoverNames("26-04-09 19:30:48\t******************** Entering Area: Serbule Hills", registry);

        registry.AllMappings.Should().BeEmpty();
    }

    [Fact]
    public void NonChatLine_noOp()
    {
        var registry = new NameRegistry();
        var rules = new ChatLogRules();

        rules.DiscoverNames("this is not a chat line at all", registry);

        registry.AllMappings.Should().BeEmpty();
    }
}
