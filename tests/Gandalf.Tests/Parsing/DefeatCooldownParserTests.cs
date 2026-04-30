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
    public void Parses_real_olugax_corpse_capture()
    {
        // Real capture: Player-prev.log:23553 (Olugax kill at 15:30:28).
        // Confirms Olugax shares the corpse-search positive signal with
        // Megaspider — the wiki's earlier two-class split was wrong.
        var line = "[15:30:28] LocalPlayer: ProcessTalkScreen(8297026, \"Search Corpse of Olugax The Ever-Pudding\", "
                 + "\"<analytics body>\", \"\", [], System.String[], 0, Corpse)";
        var evt = (DefeatCooldownObservedEvent?)_parser.TryParse(line, DateTime.UtcNow);

        evt.Should().NotBeNull();
        evt!.NpcDisplayName.Should().Be("Olugax The Ever-Pudding");
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
        // rejection text fires for Olugax too — the discriminator that
        // motivated PR #81's two-parser split was illusory.
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
        // LootSource filters by catalog so non-bosses like Snail are no-ops
        // downstream, but the parser still fires.
        var line = "[15:21:35] LocalPlayer: ProcessTalkScreen(8245618, \"Search Corpse of Snail\", "
                 + "\"\", \"\", [801,], System.String[], 0, Corpse)";
        var evt = (DefeatCooldownObservedEvent?)_parser.TryParse(line, DateTime.UtcNow);

        evt.Should().NotBeNull();
        evt!.NpcDisplayName.Should().Be("Snail");
    }

    [Fact]
    public void Returns_null_for_kill_credit_line() =>
        // The CombatInfo wisdom line is no longer load-bearing — the
        // corpse-search line is the canonical positive signal for both
        // Megaspider and Olugax classes.
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
