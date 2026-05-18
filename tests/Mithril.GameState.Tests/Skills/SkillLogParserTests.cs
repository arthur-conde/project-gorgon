using FluentAssertions;
using Mithril.GameState.Skills.Parsing;
using Xunit;

namespace Mithril.GameState.Tests.Skills;

public sealed class SkillLogParserTests
{
    private readonly SkillLogParser _parser = new();
    private static readonly DateTime Ts = new(2026, 5, 18, 10, 49, 51, DateTimeKind.Utc);

    // Real capture (trimmed to 5 representative rows incl. a capped skill and a
    // max=0 pseudo-skill) from Player.log's login dump.
    private const string LoadSkillsLine =
        "[08:22:21] LocalPlayer: ProcessLoadSkills(" +
        "{type=Toolcrafting,raw=15,bonus=0,xp=26,tnl=680,max=50}, " +
        "{type=Tanning,raw=50,bonus=3,xp=0,tnl=5280,max=50}, " +
        "{type=Augmentation,raw=0,bonus=2,xp=0,tnl=1,max=0}, " +
        "{type=Werewolf,raw=56,bonus=4,xp=13091,tnl=188650,max=70}, " +
        "{type=Anatomy_Bears,raw=27,bonus=0,xp=1435,tnl=1500,max=50})";

    private const string UpdateSkillLine =
        "[10:49:51] LocalPlayer: ProcessUpdateSkill(" +
        "{type=NatureAppreciation,raw=26,bonus=2,xp=315,tnl=1350,max=50}, True, 110, 0, 0)";

    [Fact]
    public void ProcessLoadSkills_yields_full_snapshot_in_log_order()
    {
        var evt = _parser.TryParse(LoadSkillsLine, Ts);

        var snap = evt.Should().BeOfType<SkillsSnapshotEvent>().Subject;
        snap.Timestamp.Should().Be(Ts);
        snap.Skills.Select(s => s.SkillKey).Should().Equal(
            "Toolcrafting", "Tanning", "Augmentation", "Werewolf", "Anatomy_Bears");
    }

    [Fact]
    public void ProcessLoadSkills_maps_every_field_1to1()
    {
        var snap = (SkillsSnapshotEvent)_parser.TryParse(LoadSkillsLine, Ts)!;

        // Underscore key parsed verbatim; large xp/tnl fit long.
        var bears = snap.Skills.Single(s => s.SkillKey == "Anatomy_Bears");
        bears.Level.Should().Be(27);
        bears.BonusLevels.Should().Be(0);
        bears.XpTowardNextLevel.Should().Be(1435);
        bears.XpNeededForNextLevel.Should().Be(1500);
        bears.MaxLevel.Should().Be(50);

        var werewolf = snap.Skills.Single(s => s.SkillKey == "Werewolf");
        werewolf.Level.Should().Be(56);
        werewolf.BonusLevels.Should().Be(4);
        werewolf.XpNeededForNextLevel.Should().Be(188650);
        werewolf.MaxLevel.Should().Be(70);
    }

    [Fact]
    public void ProcessUpdateSkill_yields_single_record_ignoring_trailing_args()
    {
        var evt = _parser.TryParse(UpdateSkillLine, Ts);

        var upd = evt.Should().BeOfType<SkillProgressUpdateEvent>().Subject;
        upd.Timestamp.Should().Be(Ts);
        upd.Skill.SkillKey.Should().Be("NatureAppreciation");
        upd.Skill.Level.Should().Be(26);
        upd.Skill.BonusLevels.Should().Be(2);
        upd.Skill.XpTowardNextLevel.Should().Be(315);
        upd.Skill.XpNeededForNextLevel.Should().Be(1350);
        upd.Skill.MaxLevel.Should().Be(50);
    }

    [Fact]
    public void Gardening_update_still_parses_independently_of_Samwise()
    {
        // Samwise's GardenLogParser also matches this line; SkillLogParser must
        // fold Gardening into skill state regardless — no ordering coupling.
        var line = "[10:00:00] LocalPlayer: ProcessUpdateSkill(" +
                   "{type=Gardening,raw=30,bonus=1,xp=100,tnl=900,max=50}, True, 50, 0, 0)";

        var upd = _parser.TryParse(line, Ts).Should().BeOfType<SkillProgressUpdateEvent>().Subject;
        upd.Skill.SkillKey.Should().Be("Gardening");
        upd.Skill.Level.Should().Be(30);
    }

    [Theory]
    [InlineData("[10:46:47] LocalPlayer: ProcessSetActiveSkills(Riding, Riding)")]
    [InlineData("[10:46:47] entity_23984278: OnAttackHitMe(Big Cat Claw). Evaded = False")]
    [InlineData("Loading preferences from C:/Users/x/GorgonSettings.txt")]
    public void Unrelated_lines_return_null(string line)
        => _parser.TryParse(line, Ts).Should().BeNull();

    [Fact]
    public void Degenerate_ProcessLoadSkills_with_no_struct_returns_null()
    {
        // Truncated / grammar-drift line: emit nothing rather than an empty
        // snapshot that would wipe live state.
        _parser.TryParse("[08:22:21] LocalPlayer: ProcessLoadSkills()", Ts).Should().BeNull();
    }
}
