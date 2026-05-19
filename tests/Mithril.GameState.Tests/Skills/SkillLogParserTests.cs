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
        upd.XpGained.Should().Be(110); // arg3
    }

    // arg3 triangulated against the authoritative chat "[Status] You earned N
    // XP in <Skill>." line for the same events across the Player.log (UTC) /
    // ChatLogs (local, +1h) offset. These three matched exactly in the real
    // logs; encoding them pins the semantic so a regression is visible.
    [Theory]
    [InlineData("{type=Endurance,raw=53,bonus=3,xp=11237,tnl=13140,max=60}", 26)]   // chat: "26 XP in Endurance"
    [InlineData("{type=Psychology,raw=48,bonus=3,xp=38347,tnl=74953,max=50}", 577)] // chat: "577 XP in Psychology"
    [InlineData("{type=Anatomy_Bears,raw=27,bonus=0,xp=1483,tnl=1500,max=50}", 48)] // chat: "48 XP in Bear and Bugbear Anatomy"
    public void ProcessUpdateSkill_XpGained_matches_chat_Status_oracle(string structText, long gained)
    {
        var line = $"[11:36:42] LocalPlayer: ProcessUpdateSkill({structText}, False, {gained}, 0, 0)";
        var upd = _parser.TryParse(line, Ts).Should().BeOfType<SkillProgressUpdateEvent>().Subject;
        upd.XpGained.Should().Be(gained);
    }

    [Fact]
    public void ProcessUpdateSkill_level_up_tick_parses_gross_arg3_and_post_rollover_struct()
    {
        // Real captured Tailoring level-up (raw 9→10). arg3 is the GROSS gain
        // (160, = chat "earned 160 XP and reached level 12"), NOT split across
        // the rollover; struct xp/tnl are the post-level-up values.
        var line = "[12:39:02] LocalPlayer: ProcessUpdateSkill(" +
                   "{type=Tailoring,raw=10,bonus=2,xp=149,tnl=420,max=50}, True, 160, 0, 0)";
        var upd = _parser.TryParse(line, Ts).Should().BeOfType<SkillProgressUpdateEvent>().Subject;
        upd.Skill.Level.Should().Be(10);
        upd.Skill.XpTowardNextLevel.Should().Be(149); // overflow into the new level
        upd.Skill.XpNeededForNextLevel.Should().Be(420); // new level's threshold
        upd.XpGained.Should().Be(160); // gross, chat-matched, not pre/post split
    }

    [Fact]
    public void ProcessUpdateSkill_with_no_tail_defaults_XpGained_to_zero()
    {
        // Grammar drift / truncation: struct is still authoritative for state.
        var line = "LocalPlayer: ProcessUpdateSkill({type=Sword,raw=2,bonus=0,xp=1,tnl=9,max=50})";
        var upd = _parser.TryParse(line, Ts).Should().BeOfType<SkillProgressUpdateEvent>().Subject;
        upd.Skill.Level.Should().Be(2);
        upd.XpGained.Should().Be(0);
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

    // ----- #525: per-row (int|long).TryParse guard (containment vs. snapshot loss) -----

    [Fact]
    public void ProcessLoadSkills_skips_oversized_field_row_and_keeps_the_rest()
    {
        // The middle struct's `xp` would overflow long (>= 2^63). Per #525
        // row-skip policy, the surrounding good rows still ship — only the
        // unparseable per-skill row drops.
        var line = "[08:22:21] LocalPlayer: ProcessLoadSkills(" +
                   "{type=Toolcrafting,raw=15,bonus=0,xp=26,tnl=680,max=50}, " +
                   "{type=Tanning,raw=50,bonus=3,xp=99999999999999999999,tnl=5280,max=50}, " +
                   "{type=Werewolf,raw=56,bonus=4,xp=13091,tnl=188650,max=70})";
        var snap = _parser.TryParse(line, Ts).Should().BeOfType<SkillsSnapshotEvent>().Subject;

        snap.Skills.Select(s => s.SkillKey).Should().Equal("Toolcrafting", "Werewolf");
    }

    [Fact]
    public void ProcessLoadSkills_with_all_rows_degenerate_returns_null()
    {
        // No struct survived row-parse — fall through to the same empty-payload
        // stance as line 92 (emit nothing, snapshot stands).
        var line = "LocalPlayer: ProcessLoadSkills(" +
                   "{type=A,raw=99999999999,bonus=0,xp=0,tnl=0,max=0}, " +
                   "{type=B,raw=0,bonus=0,xp=99999999999999999999,tnl=0,max=0})";
        _parser.TryParse(line, Ts).Should().BeNull();
    }

    [Fact]
    public void ProcessUpdateSkill_with_oversized_struct_field_returns_null()
    {
        // Single-record event: a malformed struct field is unrecoverable for
        // THIS event. Drop it; snapshot state stands.
        var line = "[10:49:51] LocalPlayer: ProcessUpdateSkill(" +
                   "{type=NatureAppreciation,raw=99999999999,bonus=2,xp=315,tnl=1350,max=50}, True, 110, 0, 0)";
        _parser.TryParse(line, Ts).Should().BeNull();
    }

    [Fact]
    public void ProcessUpdateSkill_with_oversized_arg3_defaults_XpGained_to_zero()
    {
        // arg3 overflowing long must not kill the event — the struct is the
        // authoritative state. Per #525, treat a malformed arg3 the same as
        // arg3-absent: emit the update with XpGained=0.
        var line = "[10:49:51] LocalPlayer: ProcessUpdateSkill(" +
                   "{type=Sword,raw=2,bonus=0,xp=1,tnl=9,max=50}, True, 99999999999999999999, 0, 0)";
        var upd = _parser.TryParse(line, Ts).Should().BeOfType<SkillProgressUpdateEvent>().Subject;
        upd.Skill.SkillKey.Should().Be("Sword");
        upd.XpGained.Should().Be(0);
    }

    [Fact]
    public void Oversized_token_does_not_throw_OverflowException()
    {
        // The whole point of #525: a single bad token must not poison the
        // parser. (Containment in PlayerSkillStateService catches throws too,
        // but a throw would still discard the entire snapshot.)
        var line = "LocalPlayer: ProcessLoadSkills(" +
                   "{type=A,raw=15,bonus=0,xp=99999999999999999999,tnl=680,max=50}, " +
                   "{type=B,raw=50,bonus=3,xp=0,tnl=5280,max=50})";
        Action act = () => _parser.TryParse(line, Ts);
        act.Should().NotThrow();
    }
}
