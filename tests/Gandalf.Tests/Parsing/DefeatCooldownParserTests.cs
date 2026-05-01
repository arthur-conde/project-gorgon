using FluentAssertions;
using Gandalf.Parsing;
using Xunit;

namespace Gandalf.Tests.Parsing;

public sealed class DefeatCooldownParserTests
{
    private readonly DefeatCooldownParser _parser = new();

    [Fact]
    public void Parses_real_megaspider_rejection_capture()
    {
        // Real capture: Player.log:35636 (2026-04-30 12:28:09).
        var line = "[12:28:09] LocalPlayer: ProcessScreenText(GeneralInfo, "
                 + "\"You have already killed Megaspider too recently. Strekios, god of Self Improvement, "
                 + "frowns upon your laziness and steals all the good loot.\")";
        var evt = (DefeatCooldownActiveEvent?)_parser.TryParse(line, DateTime.UtcNow);

        evt.Should().NotBeNull();
        evt!.NpcDisplayName.Should().Be("Megaspider");
    }

    [Fact]
    public void Parses_real_olugax_rejection_capture()
    {
        // Real capture: Player.log:123694 (2026-04-30 14:08:02). Confirms the
        // rejection text fires for both prototype bosses.
        var line = "[14:08:02] LocalPlayer: ProcessScreenText(GeneralInfo, "
                 + "\"You have already killed Olugax The Ever-Pudding too recently. Strekios, god of Self Improvement, "
                 + "frowns upon your laziness and steals all the good loot.\")";
        var evt = (DefeatCooldownActiveEvent?)_parser.TryParse(line, DateTime.UtcNow);

        evt.Should().NotBeNull();
        evt!.NpcDisplayName.Should().Be("Olugax The Ever-Pudding");
    }

    [Fact]
    public void Returns_null_for_corpse_search_line() =>
        // Corpse-search is no longer a positive signal — wisdom credit is the
        // canonical anchor. Only the rejection text is parsed here.
        _parser.TryParse(
            "[12:18:06] LocalPlayer: ProcessTalkScreen(9061771, \"Search Corpse of Megaspider\", \"\", \"\", [], System.String[], 0, Corpse)",
            DateTime.UtcNow).Should().BeNull();

    [Fact]
    public void Returns_null_for_kill_credit_line() =>
        _parser.TryParse(
            "LocalPlayer: ProcessScreenText(CombatInfo, \"You earned 12 Combat Wisdom: Killed Olugax the Ever-Pudding\")",
            DateTime.UtcNow).Should().BeNull();

    [Fact]
    public void Returns_null_for_unrelated_line() =>
        _parser.TryParse("LocalPlayer: ProcessAddItem(Sword(1234), -1, True)", DateTime.UtcNow).Should().BeNull();

    [Fact]
    public void Returns_null_for_empty_line() =>
        _parser.TryParse("", DateTime.UtcNow).Should().BeNull();
}
