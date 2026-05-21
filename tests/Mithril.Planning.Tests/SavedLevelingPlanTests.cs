using System.Text.Json;
using FluentAssertions;
using Mithril.Leveling;
using Mithril.Shared.Character;
using Mithril.GameReports;
using Xunit;

namespace Mithril.Planning.Tests;

/// <summary>
/// #228 (post-review): the leveling plan is a self-contained, portable
/// <see cref="SavedLevelingPlan"/> artifact in Mithril.Planning — full embedded
/// multi-skill state + a weak (identity-only) character ref. Pins From() mapping,
/// the source-gen round-trip, and the content-diff staleness check.
/// </summary>
public class SavedLevelingPlanTests
{
    private static readonly System.Text.Json.Serialization.Metadata.JsonTypeInfo<SavedLevelingPlanLibrary> Ti
        = SavedLevelingPlanJsonContext.Default.SavedLevelingPlanLibrary;

    private static SkillState State((string skill, int lvl, long xp, long need)[] skills)
        => new(skills.ToDictionary(s => s.skill,
            s => new SkillProgress(s.lvl, 0, s.xp, s.need), StringComparer.Ordinal));

    private static LevelingPlan SamplePlan() => new(
        Skill: "Smithing", StartLevel: 10, GoalLevel: 0, TotalXpNeeded: 1234, TotalCrafts: 8,
        Phases: [new PlanPhase(0, "recipe_forgebar", "ForgeBar", "Forge Bar", 11, 5, 30, false, 0, 10, 18, 4)],
        Unlocks: [new SkillSourceUnlock(18, "recipe_forgeplate", "ForgePlate", "Forge Plate", 90, "Reaches Smithing 18")],
        FinalState: SkillState.Empty);

    [Fact]
    public void From_EmbedsFullMultiSkillState_TargetGoal_CharRefAndSourcing()
    {
        var initial = State([("Smithing", 10, 50, 100), ("Mining", 30, 0, 1)]);
        var history = new RecipeHistory(new Dictionary<string, int> { ["ForgeBar"] = 2 });
        var sourcing = new SourcingPolicy(new Dictionary<string, SourcingMode> { ["Coal"] = SourcingMode.SupplyExternally });
        var cref = new PlanCharacterRef { Name = "Borg", Server = "Alpha", SnapshotExportedAt = DateTimeOffset.UnixEpoch };

        var saved = SavedLevelingPlan.From(SamplePlan(), new SkillTarget("Smithing", 25), initial, history, sourcing, cref);

        saved.Skill.Should().Be("Smithing");
        saved.GoalLevel.Should().Be(25, because: "goal comes from the SkillTarget, not the (transient) plan");
        saved.InitialSkills.Should().ContainKey("Smithing").And.ContainKey("Mining");
        saved.InitialSkills["Mining"].Level.Should().Be(30);
        saved.InitialRecipeCompletions["ForgeBar"].Should().Be(2);
        saved.Phases.Should().ContainSingle().Which.IntermediateReuseXpPerCraft.Should().Be(4);
        saved.Unlocks.Should().ContainSingle().Which.AtLevel.Should().Be(18);
        saved.Character!.Name.Should().Be("Borg");
        saved.ToSkillTarget().Should().Be(new SkillTarget("Smithing", 25));
        saved.ToSourcingPolicy().For("Coal").Should().Be(SourcingMode.SupplyExternally);
        saved.ToInitialSkillState().LevelOf("Mining").Should().Be(30);
        saved.ToInitialRecipeHistory().CompletionCount("ForgeBar").Should().Be(2);
        saved.Id.Should().NotBeNullOrEmpty();
    }

    [Fact]
    public void Library_RoundTripsThroughSourceGenContext()
    {
        var lib = new SavedLevelingPlanLibrary
        {
            Plans =
            {
                SavedLevelingPlan.From(SamplePlan(), new SkillTarget("Smithing", 25),
                    State([("Smithing", 10, 0, 100)]),
                    new RecipeHistory(new Dictionary<string, int> { ["ForgeBar"] = 1 }),
                    new SourcingPolicy(new Dictionary<string, SourcingMode> { ["Coal"] = SourcingMode.Ignore }),
                    new PlanCharacterRef { Name = "Borg", Server = "Alpha", SnapshotExportedAt = DateTimeOffset.UnixEpoch }),
            },
        };

        var json = JsonSerializer.Serialize(lib, Ti);
        var back = JsonSerializer.Deserialize(json, Ti)!;

        back.SchemaVersion.Should().Be(SavedLevelingPlanLibrary.CurrentVersion);
        back.Plans.Should().ContainSingle();
        var p = back.Plans[0];
        p.Skill.Should().Be("Smithing");
        p.InitialSkills["Smithing"].XpNeededForNextLevel.Should().Be(100);
        p.Character!.Server.Should().Be("Alpha");
        p.ToSourcingPolicy().For("Coal").Should().Be(SourcingMode.Ignore);
    }

    // ── Staleness (content diff) ─────────────────────────────────────────

    private static SavedLevelingPlan PlanFor(CharacterSnapshot snap)
        => SavedLevelingPlan.From(
            SamplePlan(), new SkillTarget("Smithing", 25),
            new SkillState(snap.Skills.ToDictionary(
                kv => kv.Key,
                kv => new SkillProgress(kv.Value.Level, kv.Value.BonusLevels, kv.Value.XpTowardNextLevel, kv.Value.XpNeededForNextLevel),
                StringComparer.Ordinal)),
            new RecipeHistory(snap.RecipeCompletions),
            SourcingPolicy.CraftEverything,
            PlanCharacterRef.FromSnapshot(snap));

    private static CharacterSnapshot Snap(int smithing, params (string r, int c)[] recipes)
        => new("Borg", "Alpha", DateTimeOffset.UnixEpoch,
            new Dictionary<string, CharacterSkill> { ["Smithing"] = new(smithing, 0, 0, 100) },
            recipes.ToDictionary(x => x.r, x => x.c, StringComparer.Ordinal),
            new Dictionary<string, string>());

    [Fact]
    public void IsStale_FalseWhenIdentical()
    {
        var snap = Snap(10, ("ForgeBar", 1));
        PlanFor(snap).IsInitialStateStaleAgainst(snap).Should().BeFalse();
    }

    [Fact]
    public void IsStale_TrueWhenSkillLevelAdvanced_OrRecipeLearned()
    {
        var plan = PlanFor(Snap(10, ("ForgeBar", 1)));
        plan.IsInitialStateStaleAgainst(Snap(11, ("ForgeBar", 1)))
            .Should().BeTrue(because: "Smithing leveled since the plan");
        plan.IsInitialStateStaleAgainst(Snap(10, ("ForgeBar", 1), ("ForgePlate", 0)))
            .Should().BeTrue(because: "a new recipe was learned");
    }

    [Fact]
    public void IsStale_FalseForDifferentCharacterIdentity_OrNullRef()
    {
        var plan = PlanFor(Snap(10, ("ForgeBar", 1)));
        var otherChar = new CharacterSnapshot("Alt", "Alpha", DateTimeOffset.UnixEpoch,
            new Dictionary<string, CharacterSkill> { ["Smithing"] = new(99, 0, 0, 100) },
            new Dictionary<string, int>(), new Dictionary<string, string>());
        plan.IsInitialStateStaleAgainst(otherChar)
            .Should().BeFalse(because: "not this plan's character — no refresh offer");

        plan.Character = null;
        plan.IsInitialStateStaleAgainst(Snap(99)).Should().BeFalse(because: "hypothetical/arbitrary plan, no subject");
    }
}
