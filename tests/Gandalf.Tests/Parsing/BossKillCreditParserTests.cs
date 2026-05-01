using FluentAssertions;
using Gandalf.Parsing;
using Xunit;

namespace Gandalf.Tests.Parsing;

public sealed class BossKillCreditParserTests
{
    private readonly BossKillCreditParser _parser = new();

    [Fact]
    public void Parses_olugax_kill_credit()
    {
        // Real capture: Player-prev.log:23498 (15:30:24).
        // Proper-noun form — no leading article.
        var line = "[15:30:24] LocalPlayer: ProcessScreenText(CombatInfo, "
                 + "\"You earned 12 Combat Wisdom: Killed Olugax the Ever-Pudding\")";
        var evt = (BossKillCreditEvent?)_parser.TryParse(line, DateTime.UtcNow);

        evt.Should().NotBeNull();
        evt!.NpcDisplayName.Should().Be("Olugax the Ever-Pudding");
    }

    [Fact]
    public void Parses_megaspider_kill_credit_strips_leading_a_article()
    {
        // Wiki capture: "Killed a Mega-Spider" — the wisdom line uses the
        // hyphenated form with a leading "a" article. Parser strips the
        // article so calibration entries don't have to care which kill
        // form prefixed the name.
        var line = "[12:00:00] LocalPlayer: ProcessScreenText(CombatInfo, "
                 + "\"You earned 12 Combat Wisdom: Killed a Mega-Spider\")";
        var evt = (BossKillCreditEvent?)_parser.TryParse(line, DateTime.UtcNow);

        evt.Should().NotBeNull();
        evt!.NpcDisplayName.Should().Be("Mega-Spider");
    }

    [Fact]
    public void Parses_den_mother_kill_credit_strips_leading_the_article()
    {
        // Real capture: Player.log:251739 (22:19:31). Common-noun form with
        // leading "the" article.
        var line = "[22:19:31] LocalPlayer: ProcessScreenText(CombatInfo, "
                 + "\"You earned 3 Combat Wisdom: Killed the Den Mother\")";
        var evt = (BossKillCreditEvent?)_parser.TryParse(line, DateTime.UtcNow);

        evt.Should().NotBeNull();
        evt!.NpcDisplayName.Should().Be("Den Mother");
    }

    [Fact]
    public void Parses_doctrine_keeper_kill_credit_strips_leading_the()
    {
        // Real capture: Player.log:248492 (22:13:38). Multi-word common-noun
        // form. The "Ranalon" prefix is part of the name (not an article)
        // and must be preserved.
        var line = "[22:13:38] LocalPlayer: ProcessScreenText(CombatInfo, "
                 + "\"You earned 5 Combat Wisdom: Killed the Ranalon Doctrine-Keeper\")";
        var evt = (BossKillCreditEvent?)_parser.TryParse(line, DateTime.UtcNow);

        evt.Should().NotBeNull();
        evt!.NpcDisplayName.Should().Be("Ranalon Doctrine-Keeper");
    }

    [Fact]
    public void Parses_decimal_wisdom_amount()
    {
        var line = "LocalPlayer: ProcessScreenText(CombatInfo, "
                 + "\"You earned 1.5 Combat Wisdom: Killed an Ancient Spider\")";
        var evt = (BossKillCreditEvent?)_parser.TryParse(line, DateTime.UtcNow);

        evt.Should().NotBeNull();
        evt!.NpcDisplayName.Should().Be("Ancient Spider");
    }

    [Fact]
    public void Returns_null_for_non_combat_wisdom_xp_line() =>
        // First Aid (or any non-Combat-Wisdom skill) doesn't gate a defeat
        // cooldown — substring guard rejects this line cheaply.
        _parser.TryParse(
            "LocalPlayer: ProcessScreenText(CombatInfo, \"You earned 5 First Aid: Healed Tyngyff\")",
            DateTime.UtcNow).Should().BeNull();

    [Fact]
    public void Returns_null_for_rejection_line() =>
        _parser.TryParse(
            "LocalPlayer: ProcessScreenText(GeneralInfo, \"You have already killed Megaspider too recently.\")",
            DateTime.UtcNow).Should().BeNull();

    [Fact]
    public void Returns_null_for_corpse_search_line() =>
        _parser.TryParse(
            "LocalPlayer: ProcessTalkScreen(9061771, \"Search Corpse of Megaspider\", \"\", \"\", [], System.String[], 0, Corpse)",
            DateTime.UtcNow).Should().BeNull();

    [Fact]
    public void Returns_null_for_unrelated_line() =>
        _parser.TryParse("LocalPlayer: ProcessAddItem(Sword(1234), -1, True)", DateTime.UtcNow).Should().BeNull();

    [Fact]
    public void Returns_null_for_empty_line() =>
        _parser.TryParse("", DateTime.UtcNow).Should().BeNull();
}
