using FluentAssertions;
using Gandalf.Parsing;
using Xunit;

namespace Gandalf.Tests.Parsing;

public sealed class DefeatCooldownParserTests
{
    private readonly DefeatCooldownParser _parser = new();

    [Fact]
    public void Parses_real_megaspider_corpse_capture()
    {
        // Real capture: Player.log:30986 (2026-04-30 12:18:06).
        var line = "[12:18:06] LocalPlayer: ProcessTalkScreen(9061771, \"Search Corpse of Megaspider\", "
                 + "\"\\n<em>Cause of death:</em> Mauled by a wild animal\\n<em>Killer:</em> Emraell\\n\\n"
                 + "<h2>Detailed Analysis:</h2>\\nEmraell: 580 health dmg 302 armor dmg. Aggro (at death): 100%\\n\", "
                 + "\"\", [], System.String[], 0, Corpse)";
        var evt = (DefeatCooldownObservedEvent?)_parser.TryParse(line, DateTime.UtcNow);

        evt.Should().NotBeNull();
        evt!.NpcDisplayName.Should().Be("Megaspider");
    }

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
        // rejection text fires for both DefeatCooldown and ScriptedEvent classes;
        // LootSource (not the parser) gates by class.
        var line = "[14:08:02] LocalPlayer: ProcessScreenText(GeneralInfo, "
                 + "\"You have already killed Olugax The Ever-Pudding too recently. Strekios, god of Self Improvement, "
                 + "frowns upon your laziness and steals all the good loot.\")";
        var evt = (DefeatCooldownActiveEvent?)_parser.TryParse(line, DateTime.UtcNow);

        evt.Should().NotBeNull();
        evt!.NpcDisplayName.Should().Be("Olugax The Ever-Pudding");
    }

    [Fact]
    public void Parses_regular_mob_corpse_emits_observed_event()
    {
        // Permissive parser — every mob's corpse emits a Search Corpse line.
        // LootSource filters by catalog (DefeatClass.DefeatCooldown) so non-bosses
        // like Snail are no-ops downstream, but the parser still fires.
        var line = "[15:21:35] LocalPlayer: ProcessTalkScreen(8245618, \"Search Corpse of Snail\", "
                 + "\"\", \"\", [801,], System.String[], 0, Corpse)";
        var evt = (DefeatCooldownObservedEvent?)_parser.TryParse(line, DateTime.UtcNow);

        evt.Should().NotBeNull();
        evt!.NpcDisplayName.Should().Be("Snail");
    }

    [Fact]
    public void Returns_null_for_kill_credit_line() =>
        // Kill credit belongs to ScriptedEventBossParser; this parser ignores it.
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
