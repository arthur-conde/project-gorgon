using FluentAssertions;
using Gandalf.Parsing;
using Xunit;

namespace Gandalf.Tests.Parsing;

public sealed class DefeatRewardParserTests
{
    private readonly DefeatRewardParser _parser = new();

    [Fact]
    public void Parses_wiki_sample_olugax_kill_credit()
    {
        // From wiki: Player-Log-Signals § Scripted-event bosses § Kill credit.
        var line = "[15:30:24] LocalPlayer: ProcessScreenText(CombatInfo, \"You earned 12 Combat Wisdom: Killed Olugax the Ever-Pudding\")";
        var evt = _parser.TryParse(line, DateTime.UtcNow);

        evt.Should().BeOfType<DefeatRewardEvent>();
        ((DefeatRewardEvent)evt!).NpcDisplayName.Should().Be("Olugax the Ever-Pudding");
    }

    [Fact]
    public void Parses_kill_credit_with_decimal_xp()
    {
        var line = "LocalPlayer: ProcessScreenText(CombatInfo, \"You earned 1.5 Combat Wisdom: Killed Some Rare NPC\")";
        var evt = (DefeatRewardEvent?)_parser.TryParse(line, DateTime.UtcNow);

        evt.Should().NotBeNull();
        evt!.NpcDisplayName.Should().Be("Some Rare NPC");
    }

    [Fact]
    public void Returns_null_for_unrelated_combat_info()
    {
        // No "Killed" suffix — this isn't a kill credit.
        var line = "LocalPlayer: ProcessScreenText(CombatInfo, \"You earned 5 First Aid\")";
        _parser.TryParse(line, DateTime.UtcNow).Should().BeNull();
    }

    [Fact]
    public void Returns_null_for_unrelated_line() =>
        _parser.TryParse("LocalPlayer: ProcessAddItem(Sword(1234), -1, True)", DateTime.UtcNow).Should().BeNull();

    [Fact]
    public void Returns_null_for_empty_line() =>
        _parser.TryParse("", DateTime.UtcNow).Should().BeNull();
}
