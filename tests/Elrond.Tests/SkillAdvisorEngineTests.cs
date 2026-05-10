using Elrond.Services;
using FluentAssertions;
using Mithril.Shared.Character;
using Mithril.Shared.Reference;
using Xunit;

namespace Elrond.Tests;

public class SkillAdvisorEngineTests
{
    /// <summary>
    /// Real-world test case: Meditation at level 24, 469/990 XP, recipe gives 96 base / 384 first-time.
    /// </summary>
    [Fact]
    public void Analyze_Meditation_Level24_CorrectXpRemaining()
    {
        var refData = new FakeRefData();
        refData.AddSkill("Meditation", 1, false, "TypicalNoncombatSkill", 25);
        refData.AddXpTable("TypicalNoncombatSkill", [
            10, 50, 50, 50, 50,         // levels 1-5
            210, 210, 210, 210, 210,     // levels 6-10
            420, 420, 420, 420, 420,     // levels 11-15
            680, 680, 680, 680, 680,     // levels 16-20
            990, 990, 990, 990, 990,     // levels 21-25
            1350, 1350, 1350, 1350, 1350 // levels 26-30
        ]);
        refData.AddRecipe("recipe_7024", "Meditation #24", "Meditation24", "Meditation", 24,
            "Meditation", 96, 384, null, null, null);
        refData.AddRecipe("recipe_7023", "Meditation #23", "Meditation23", "Meditation", 23,
            "Meditation", 92, 368, null, null, null);

        var character = MakeCharacter(
            skills: new Dictionary<string, CharacterSkill>
            {
                ["Meditation"] = new(24, 2, 469, 990),
            },
            recipes: new Dictionary<string, int>
            {
                ["Meditation23"] = 8,  // already done
                ["Meditation24"] = 1,  // already done once
            });

        var engine = new SkillAdvisorEngine(refData);
        var result = engine.Analyze("Meditation", character)!;

        result.Should().NotBeNull();
        result.CurrentLevel.Should().Be(24);
        result.CurrentXp.Should().Be(469);
        result.XpNeededForNextLevel.Should().Be(990);
        result.XpRemaining.Should().Be(521); // 990 - 469

        // Both recipes are known (present in RecipeCompletions)
        result.Recipes.Should().OnlyContain(r => r.IsKnown);
    }

    [Fact]
    public void Analyze_IsKnown_DistinguishesLearnedFromUnlearned()
    {
        var refData = new FakeRefData();
        refData.AddSkill("Cooking", 1, false, "TestTable", 25);
        refData.AddXpTable("TestTable", [100, 200]);
        refData.AddRecipe("recipe_1", "Butter", "Butter", "Cooking", 1, "Cooking", 10, 40, null, null, null);
        refData.AddRecipe("recipe_2", "Bacon", "Bacon", "Cooking", 5, "Cooking", 20, 80, null, null, null);

        var character = MakeCharacter(
            skills: new Dictionary<string, CharacterSkill>
            {
                ["Cooking"] = new(1, 0, 0, 100),
            },
            recipes: new Dictionary<string, int>
            {
                ["Butter"] = 3, // known and completed
                                // Bacon is NOT in RecipeCompletions = not learned
            });

        var engine = new SkillAdvisorEngine(refData);
        var result = engine.Analyze("Cooking", character)!;

        var butter = result.Recipes.Single(r => r.InternalName == "Butter");
        var bacon = result.Recipes.Single(r => r.InternalName == "Bacon");

        butter.IsKnown.Should().BeTrue();
        bacon.IsKnown.Should().BeFalse();
    }

    [Fact]
    public void Analyze_FirstTimeBonusAvailable_CorrectCompletionsToLevel()
    {
        var refData = new FakeRefData();
        refData.AddSkill("Meditation", 1, false, "TestTable", 25);
        refData.AddXpTable("TestTable", [100, 200, 300, 400, 500]);
        refData.AddRecipe("recipe_1", "Med #1", "Med1", "Meditation", 1,
            "Meditation", 50, 200, null, null, null);

        // Character has 150 XP remaining (300 needed, 150 earned)
        // First-time bonus: 200 XP would cover it in 1 completion
        var character = MakeCharacter(
            skills: new Dictionary<string, CharacterSkill>
            {
                ["Meditation"] = new(3, 0, 150, 300),
            },
            recipes: new Dictionary<string, int> { ["Med1"] = 0 }); // known but never completed

        var engine = new SkillAdvisorEngine(refData);
        var result = engine.Analyze("Meditation", character)!;

        var recipe = result.Recipes.Single();
        recipe.FirstTimeBonusAvailable.Should().BeTrue();
        recipe.EffectiveXp.Should().Be(50);
        recipe.CompletionsToLevel.Should().Be(1); // 200 first-time XP >= 150 remaining
    }

    [Fact]
    public void Analyze_NoFirstTimeBonus_CompletionsCalculatedFromBaseXp()
    {
        var refData = new FakeRefData();
        refData.AddSkill("Cooking", 1, false, "TestTable", 25);
        refData.AddXpTable("TestTable", [100, 200, 300]);
        refData.AddRecipe("recipe_1", "Butter", "Butter", "Cooking", 1,
            "Cooking", 10, 40, null, null, null);

        // 200 XP remaining, base XP = 10, already done once
        var character = MakeCharacter(
            skills: new Dictionary<string, CharacterSkill>
            {
                ["Cooking"] = new(2, 0, 0, 200),
            },
            recipes: new Dictionary<string, int> { ["Butter"] = 5 });

        var engine = new SkillAdvisorEngine(refData);
        var result = engine.Analyze("Cooking", character)!;

        var recipe = result.Recipes.Single();
        recipe.FirstTimeBonusAvailable.Should().BeFalse();
        recipe.TimesCompleted.Should().Be(5);
        recipe.CompletionsToLevel.Should().Be(20); // ceil(200 / 10)
    }

    [Fact]
    public void Analyze_RecipesFilteredToRewardSkill()
    {
        var refData = new FakeRefData();
        refData.AddSkill("Cooking", 1, false, "TestTable", 25);
        refData.AddXpTable("TestTable", [100]);
        refData.AddRecipe("recipe_1", "Butter", "Butter", "Cooking", 1, "Cooking", 10, 40, null, null, null);
        refData.AddRecipe("recipe_2", "Med #1", "Med1", "Meditation", 1, "Meditation", 50, 200, null, null, null);

        var character = MakeCharacter(
            skills: new Dictionary<string, CharacterSkill>
            {
                ["Cooking"] = new(1, 0, 0, 100),
            },
            recipes: new Dictionary<string, int>());

        var engine = new SkillAdvisorEngine(refData);
        var result = engine.Analyze("Cooking", character)!;

        result.Recipes.Should().HaveCount(1);
        result.Recipes[0].RecipeName.Should().Be("Butter");
    }

    [Fact]
    public void Analyze_UnknownSkill_ReturnsNull()
    {
        var refData = new FakeRefData();
        var character = MakeCharacter(
            skills: new Dictionary<string, CharacterSkill>(),
            recipes: new Dictionary<string, int>());

        var engine = new SkillAdvisorEngine(refData);
        engine.Analyze("NonExistent", character).Should().BeNull();
    }

    [Fact]
    public void GetCookbookSections_ReturnsOnlySectionsWithXpRecipes()
    {
        var refData = new FakeRefData();
        refData.AddRecipe("recipe_1", "Butter", "Butter", "Cooking", 1, "Cooking", 10, 40, null, null, null);
        refData.AddRecipe("recipe_2", "Med #1", "Med1", "Meditation", 1, "Meditation", 50, 200, null, null, null);
        refData.AddRecipe("recipe_3", "NoXp", "NoXp", "Alchemy", 1, "Alchemy", 0, 0, null, null, null);

        var engine = new SkillAdvisorEngine(refData);
        var sections = engine.GetCookbookSections();

        sections.Should().BeEquivalentTo(["Cooking", "Meditation"]);
    }

    [Fact]
    public void GetCookbookSections_PrefersSortSkillOverRewardSkill()
    {
        // Fish-based food: rewards Fishing XP but files under Cooking in the in-game cookbook.
        // GetCookbookSections should surface "Cooking" (the filing), not "Fishing" (the reward).
        var refData = new FakeRefData();
        refData.AddRecipe("recipe_stew", "Fish Stew", "FishStew", "Fishing", 30, "Fishing", 60, 240,
            null, null, null, sortSkill: "Cooking");
        refData.AddRecipe("recipe_bread", "Bread", "Bread", "Cooking", 1, "Cooking", 20, 80,
            null, null, null);

        var engine = new SkillAdvisorEngine(refData);
        engine.GetCookbookSections().Should().BeEquivalentTo(["Cooking"],
            because: "all recipes file under Cooking — the standalone Fishing recipe is filed there too via SortSkill");
    }

    [Fact]
    public void Analyze_UmbrellaSection_FlagsAnalysisAsUmbrella()
    {
        // Phrenology is the canonical umbrella: XpTable="None", recipes file under it
        // but reward per-race sub-skills. The view degrades the section header's
        // XP fraction / progress bar / remaining-line to "—" when this flag is set;
        // CurrentLevel and CurrentBonusLevels still come from the export and render.
        var refData = new FakeRefData();
        refData.AddSkill("Phrenology", id: 86, combat: false, xpTable: "None", maxBonusLevels: 125);
        refData.AddSkill("Phrenology_Humans", id: 87, combat: false, xpTable: "TypicalNoncombatSkill", maxBonusLevels: 25);
        refData.AddXpTable("TypicalNoncombatSkill", [10L, 50L, 50L, 50L, 50L]);
        refData.AddRecipe("recipe_phren_human", "Human Phrenology Research", "HumanPhrenologyResearch",
            "Phrenology_Humans", 1, "Phrenology_Humans", 20, 100, null, null, null, sortSkill: "Phrenology");

        var character = MakeCharacter(
            skills: new Dictionary<string, CharacterSkill>
            {
                ["Phrenology"] = new(0, 0, 0, 0),
                ["Phrenology_Humans"] = new(2, 0, 10, 50),
            },
            recipes: new Dictionary<string, int>());

        var engine = new SkillAdvisorEngine(refData);
        var analysis = engine.Analyze("Phrenology", character)!;

        analysis.IsUmbrellaSection.Should().BeTrue(
            because: "Phrenology has XpTable=None — the section can't be directly leveled");
    }

    [Fact]
    public void Analyze_UmbrellaSection_HeaderHasRealLevelFromExport()
    {
        // Regression for #150: umbrella sections used to render Level "—" in the
        // header even though the export carries a real Level (and BonusLevels).
        // Phrenology with Level=9, BonusLevels=9 (the issue's repro fixture) — the
        // export's 0/1 sentinel for XpTowardNextLevel/XpNeededForNextLevel still
        // means the XP fraction is meaningless, but the level number itself isn't.
        var refData = new FakeRefData();
        refData.AddSkill("Phrenology", id: 86, combat: false, xpTable: "None", maxBonusLevels: 125);
        refData.AddSkill("Phrenology_Humans", id: 87, combat: false, xpTable: "TypicalNoncombatSkill", maxBonusLevels: 25);
        refData.AddXpTable("TypicalNoncombatSkill", [10L, 50L, 50L, 50L, 50L]);
        refData.AddRecipe("recipe_phren_human", "Human Phrenology Research", "HumanPhrenologyResearch",
            "Phrenology_Humans", 1, "Phrenology_Humans", 20, 100, null, null, null, sortSkill: "Phrenology");

        var character = MakeCharacter(
            skills: new Dictionary<string, CharacterSkill>
            {
                ["Phrenology"] = new(9, 9, 0, 1),
                ["Phrenology_Humans"] = new(2, 0, 10, 50),
            },
            recipes: new Dictionary<string, int>());

        var engine = new SkillAdvisorEngine(refData);
        var analysis = engine.Analyze("Phrenology", character)!;

        analysis.CurrentLevel.Should().Be(9,
            because: "umbrella sections still have a meaningful Level in the export — only the XP curve is missing");
        analysis.CurrentBonusLevels.Should().Be(9,
            because: "BonusLevels comes straight from the export and should flow through to the section header");
    }

    [Fact]
    public void Analyze_PopulatesCurrentBonusLevelsFromCharacterExport()
    {
        // Normal (non-umbrella) section: BonusLevels must still be carried through
        // SkillAnalysis so the header can render "Alchemy Level 26 (3 from bonuses)".
        var refData = new FakeRefData();
        refData.AddSkill("Alchemy", id: 2, combat: false, xpTable: "TypicalNoncombatSkill", maxBonusLevels: 25);
        refData.AddXpTable("TypicalNoncombatSkill", new long[] { 10L, 50L, 50L, 50L, 50L, 50L, 50L, 50L, 50L, 50L,
            50L, 50L, 50L, 50L, 50L, 50L, 50L, 50L, 50L, 50L, 50L, 50L, 50L, 50L, 50L, 50L, 50L, 50L, 50L, 50L });
        refData.AddRecipe("recipe_potion", "Potion", "Potion", "Alchemy", 1, "Alchemy", 10, 40, null, null, null);

        var character = MakeCharacter(
            skills: new Dictionary<string, CharacterSkill> { ["Alchemy"] = new(26, 3, 100, 500) },
            recipes: new Dictionary<string, int>());

        var engine = new SkillAdvisorEngine(refData);
        var analysis = engine.Analyze("Alchemy", character)!;

        analysis.CurrentLevel.Should().Be(26);
        analysis.CurrentBonusLevels.Should().Be(3,
            because: "the engine must denormalise CharacterSkill.BonusLevels onto SkillAnalysis so the VM can render '(N from bonuses)'");
        analysis.IsUmbrellaSection.Should().BeFalse(
            because: "Alchemy has a normal XpTable — bonus-level rendering must work for non-umbrella sections too");
    }

    [Fact]
    public void Analyze_NormalSection_IsNotUmbrella()
    {
        var refData = new FakeRefData();
        refData.AddSkill("Cooking", id: 1, combat: false, xpTable: "TypicalNoncombatSkill", maxBonusLevels: 25);
        refData.AddXpTable("TypicalNoncombatSkill", [10L, 50L, 50L]);
        refData.AddRecipe("recipe_bread", "Bread", "Bread", "Cooking", 1, "Cooking", 10, 40, null, null, null);

        var character = MakeCharacter(
            skills: new Dictionary<string, CharacterSkill> { ["Cooking"] = new(2, 0, 0, 50) },
            recipes: new Dictionary<string, int>());

        var engine = new SkillAdvisorEngine(refData);
        var analysis = engine.Analyze("Cooking", character)!;

        analysis.IsUmbrellaSection.Should().BeFalse();
    }

    [Fact]
    public void Analyze_RecipeRewardsSection_DiffersFromSectionFlagIsFalse()
    {
        var refData = new FakeRefData();
        refData.AddSkill("Cooking", id: 1, combat: false, xpTable: "TypicalNoncombatSkill", maxBonusLevels: 25);
        refData.AddXpTable("TypicalNoncombatSkill", [10L, 50L, 50L]);
        refData.AddRecipe("recipe_bread", "Bread", "Bread", "Cooking", 1, "Cooking", 10, 40, null, null, null);

        var character = MakeCharacter(
            skills: new Dictionary<string, CharacterSkill> { ["Cooking"] = new(2, 0, 0, 50) },
            recipes: new Dictionary<string, int>());

        var engine = new SkillAdvisorEngine(refData);
        var analysis = engine.Analyze("Cooking", character)!;

        analysis.Recipes[0].RewardSkillDiffersFromSection.Should().BeFalse(
            because: "Bread rewards Cooking and is filed in Cooking — no need for the target-skill panel");
    }

    [Fact]
    public void Analyze_FishRecipeFiledUnderCooking_ReportsFishingXpAndCompletions()
    {
        // Cookbook view: Fish Stew is filed under Cooking, but its metrics use Fishing.
        var refData = new FakeRefData();
        refData.AddSkill("Cooking", id: 1, combat: false, xpTable: "TestTable", maxBonusLevels: 0);
        refData.AddSkill("Fishing", id: 2, combat: false, xpTable: "TestTable", maxBonusLevels: 0);
        refData.AddXpTable("TestTable", [100L, 200L, 300L, 400L, 500L]);
        refData.AddRecipe("recipe_stew", "Fish Stew", "FishStew", "Fishing", 1, "Fishing", 60, 240,
            null, null, null, sortSkill: "Cooking");

        var character = MakeCharacter(
            skills: new Dictionary<string, CharacterSkill>
            {
                ["Cooking"] = new(2, 0, 0, 200),
                ["Fishing"] = new(3, 0, 50, 300),
            },
            recipes: new Dictionary<string, int>
            {
                ["FishStew"] = 0, // known but not yet crafted → first-time bonus available
            });

        var engine = new SkillAdvisorEngine(refData);
        var analysis = engine.Analyze("Cooking", character)!;

        analysis.SkillName.Should().Be("Cooking");
        analysis.Recipes.Should().ContainSingle();
        var stew = analysis.Recipes[0];
        stew.RewardSkill.Should().Be("Fishing", because: "recipe row carries its own RewardSkill for the pill/column");
        stew.RewardSkillDiffersFromSection.Should().BeTrue(
            because: "Fish Stew rewards Fishing but files under Cooking — the target-skill panel should appear");
        stew.RewardSkillCurrentLevel.Should().Be(3, because: "denormalised from the active character's Fishing level");
        stew.RewardSkillCurrentXp.Should().Be(50);
        stew.RewardSkillXpNeededForNextLevel.Should().Be(300);
        stew.EffectiveXp.Should().Be(60);
        // Completions-to-level uses Fishing's xpRemaining (300 - 50 = 250), NOT Cooking's
        // (200 - 0 = 200). One first-time craft delivers 240, a second craft at 60 XP
        // closes the remaining 10 → 2 completions.
        stew.CompletionsToLevel.Should().Be(2,
            because: "completions-to-level uses the recipe's own RewardSkill (Fishing), not the section (Cooking)");
    }

    [Fact]
    public void Analyze_UmbrellaSection_RecipeCarriesGatingSkillFromCharacter()
    {
        // Regression for #148: under an umbrella section like Phrenology, the recipe's gating
        // skill (recipe.Skill, paired with SkillLevelReq) is a sub-skill — not the umbrella.
        // The "Craftable only" filter must compare LevelRequired against the player's level
        // in that gating skill, not the umbrella's level (which may be lower or zero).
        var refData = new FakeRefData();
        refData.AddSkill("Phrenology", id: 86, combat: false, xpTable: "None", maxBonusLevels: 125);
        refData.AddSkill("Phrenology_Goblins", id: 88, combat: false, xpTable: "TypicalNoncombatSkill", maxBonusLevels: 25);
        refData.AddXpTable("TypicalNoncombatSkill", [10L, 50L, 50L, 50L, 50L, 210L, 210L, 210L, 210L, 210L, 420L, 420L]);
        refData.AddRecipe("recipe_23052", "Goblin Phrenology Research 2", "GoblinPhrenologyResearch2",
            "Phrenology_Goblins", 12, "Phrenology_Goblins", 50, 250, null, null, null, sortSkill: "Phrenology");

        var character = MakeCharacter(
            skills: new Dictionary<string, CharacterSkill>
            {
                ["Phrenology"] = new(9, 0, 0, 0),               // umbrella — section header
                ["Phrenology_Goblins"] = new(12, 0, 0, 420),    // gating skill — at the level the recipe requires
            },
            recipes: new Dictionary<string, int>());

        var engine = new SkillAdvisorEngine(refData);
        var analysis = engine.Analyze("Phrenology", character)!;

        var recipe = analysis.Recipes.Single();
        recipe.LevelRequired.Should().Be(12);
        recipe.GatingSkill.Should().Be("Phrenology_Goblins",
            because: "the recipe's Skill field is the gating skill — not the umbrella section");
        recipe.GatingSkillCurrentLevel.Should().Be(12,
            because: "denormalised from the character's Phrenology_Goblins level so the Craftable filter can compare against it");
        // Sanity: the umbrella section level is 9, which would have failed a section-level comparison.
        analysis.CurrentLevel.Should().Be(9);
    }

    [Fact]
    public void Analyze_BuildsMilestones()
    {
        var refData = new FakeRefData();
        refData.AddSkill("Cooking", 1, false, "TestTable", 25);
        refData.AddXpTable("TestTable", [100, 200, 300, 400, 500]);

        var character = MakeCharacter(
            skills: new Dictionary<string, CharacterSkill>
            {
                ["Cooking"] = new(2, 0, 50, 200),
            },
            recipes: new Dictionary<string, int>());

        var engine = new SkillAdvisorEngine(refData);
        var result = engine.Analyze("Cooking", character)!;

        result.Milestones.Should().HaveCountGreaterThan(0);
        result.Milestones[0].Level.Should().Be(3);
        result.Milestones[0].XpRequired.Should().Be(300);
        result.Milestones[0].CumulativeXpFromCurrent.Should().Be(150); // 200 - 50 = 150 remaining
    }

    // ── Goal Level Tests ─────────────────────────────────────────────────

    [Fact]
    public void Analyze_WithGoalLevel_XpRemainingSpansMultipleLevels()
    {
        var refData = new FakeRefData();
        refData.AddSkill("Cooking", 1, false, "TestTable", 25);
        refData.AddXpTable("TestTable", [100, 200, 300, 400, 500]);
        refData.AddRecipe("recipe_1", "Butter", "Butter", "Cooking", 1, "Cooking", 10, 40, null, null, null);

        // Level 2, 50/200 XP. Goal = 5.
        // Remaining: (200-50) + 300 + 400 = 850
        var character = MakeCharacter(
            skills: new Dictionary<string, CharacterSkill>
            {
                ["Cooking"] = new(2, 0, 50, 200),
            },
            recipes: new Dictionary<string, int> { ["Butter"] = 3 });

        var engine = new SkillAdvisorEngine(refData);
        var result = engine.Analyze("Cooking", character, goalLevel: 5)!;

        result.GoalLevel.Should().Be(5);
        result.XpRemaining.Should().Be(850); // (200-50) + 300 + 400
    }

    [Fact]
    public void Analyze_WithGoalLevel_CompletionsReflectsFullGap()
    {
        var refData = new FakeRefData();
        refData.AddSkill("Cooking", 1, false, "TestTable", 25);
        refData.AddXpTable("TestTable", [100, 200, 300, 400, 500]);
        refData.AddRecipe("recipe_1", "Butter", "Butter", "Cooking", 1, "Cooking", 100, 0, null, null, null);

        // Level 2, 0/200 XP. Goal = 4.
        // Remaining: 200 + 300 = 500. At 100 XP/craft = 5 completions.
        var character = MakeCharacter(
            skills: new Dictionary<string, CharacterSkill>
            {
                ["Cooking"] = new(2, 0, 0, 200),
            },
            recipes: new Dictionary<string, int> { ["Butter"] = 3 });

        var engine = new SkillAdvisorEngine(refData);
        var result = engine.Analyze("Cooking", character, goalLevel: 4)!;

        var recipe = result.Recipes.Single();
        recipe.CompletionsToLevel.Should().Be(5);
    }

    [Fact]
    public void Analyze_NullGoalLevel_BehaviorUnchanged()
    {
        var refData = new FakeRefData();
        refData.AddSkill("Cooking", 1, false, "TestTable", 25);
        refData.AddXpTable("TestTable", [100, 200, 300]);
        refData.AddRecipe("recipe_1", "Butter", "Butter", "Cooking", 1, "Cooking", 10, 0, null, null, null);

        var character = MakeCharacter(
            skills: new Dictionary<string, CharacterSkill>
            {
                ["Cooking"] = new(2, 0, 50, 200),
            },
            recipes: new Dictionary<string, int> { ["Butter"] = 3 });

        var engine = new SkillAdvisorEngine(refData);
        var withNull = engine.Analyze("Cooking", character, goalLevel: null)!;
        var without = engine.Analyze("Cooking", character)!;

        withNull.GoalLevel.Should().BeNull();
        withNull.XpRemaining.Should().Be(without.XpRemaining);
        withNull.XpRemaining.Should().Be(150); // 200 - 50
    }

    // ── Drop-off regression (#159) ───────────────────────────────────────
    // Pins the three real-world numbers from Arthur's character that exposed
    // the off-by-one in ComputeEffectiveXp: RewardSkillXpDropOffLevel is the
    // level AT WHICH the first reduction already applies (not the level after
    // which drop-off starts). The boundary case nails the single-reduction
    // tier at playerLevel == dropOffLevel.

    [Fact]
    public void ComputeEffectiveXp_RoughLeatherPracticeAtLevel15_AppliesTwoReductions()
    {
        // Repro from #159: Leatherworking 15, base XP 20, dropOff 10, rate 5, pct 0.1.
        // (15-10)/5 + 1 = 2 reductions → 20 × 0.9² = 16.2 → 16. Game tooltip shows 16.
        var refData = new FakeRefData();
        refData.AddSkill("Leatherworking", 1, false, "TestTable", 25);
        refData.AddXpTable("TestTable", [100L, 200L, 300L]);
        refData.AddRecipe("recipe_11002", "Rough Leather Practice", "RoughLeatherPractice",
            "Leatherworking", 1, "Leatherworking", 20, 0, dropOffLevel: 10, dropOffPct: 0.1f, dropOffRate: 5);

        var character = MakeCharacter(
            skills: new Dictionary<string, CharacterSkill> { ["Leatherworking"] = new(15, 0, 0, 100) },
            recipes: new Dictionary<string, int> { ["RoughLeatherPractice"] = 5 });

        var engine = new SkillAdvisorEngine(refData);
        var result = engine.Analyze("Leatherworking", character)!;

        result.Recipes.Single(r => r.InternalName == "RoughLeatherPractice").EffectiveXp.Should().Be(16);
    }

    [Fact]
    public void ComputeEffectiveXp_QuarterHoopAtLevel25_AppliesThreeReductions()
    {
        // Repro from #159: Blacksmithing 25, base XP 10, dropOff 11, rate 5, pct 0.1.
        // (25-11)/5 + 1 = 3 reductions → 10 × 0.9³ = 7.29 → 7. Game tooltip shows 7.
        var refData = new FakeRefData();
        refData.AddSkill("Blacksmithing", 1, false, "TestTable", 25);
        refData.AddXpTable("TestTable", [100L, 200L, 300L]);
        refData.AddRecipe("recipe_19016", "Quarter Hoop", "QuarterHoop",
            "Blacksmithing", 1, "Blacksmithing", 10, 0, dropOffLevel: 11, dropOffPct: 0.1f, dropOffRate: 5);

        var character = MakeCharacter(
            skills: new Dictionary<string, CharacterSkill> { ["Blacksmithing"] = new(25, 0, 0, 100) },
            recipes: new Dictionary<string, int> { ["QuarterHoop"] = 5 });

        var engine = new SkillAdvisorEngine(refData);
        var result = engine.Analyze("Blacksmithing", character)!;

        result.Recipes.Single(r => r.InternalName == "QuarterHoop").EffectiveXp.Should().Be(7);
    }

    [Fact]
    public void ComputeEffectiveXp_RoughCowShoesAtLevel25_AppliesOneReduction()
    {
        // Repro from #159: Blacksmithing 25, base XP 52, dropOff 23, rate 5, pct 0.1.
        // (25-23)/5 + 1 = 1 reduction → 52 × 0.9 = 46.8 → 46. Game tooltip shows 46.
        var refData = new FakeRefData();
        refData.AddSkill("Blacksmithing", 1, false, "TestTable", 25);
        refData.AddXpTable("TestTable", [100L, 200L, 300L]);
        refData.AddRecipe("recipe_19102", "Rough Cow Shoes", "CraftedCowShoes2",
            "Blacksmithing", 1, "Blacksmithing", 52, 0, dropOffLevel: 23, dropOffPct: 0.1f, dropOffRate: 5);

        var character = MakeCharacter(
            skills: new Dictionary<string, CharacterSkill> { ["Blacksmithing"] = new(25, 0, 0, 100) },
            recipes: new Dictionary<string, int> { ["CraftedCowShoes2"] = 5 });

        var engine = new SkillAdvisorEngine(refData);
        var result = engine.Analyze("Blacksmithing", character)!;

        result.Recipes.Single(r => r.InternalName == "CraftedCowShoes2").EffectiveXp.Should().Be(46);
    }

    [Fact]
    public void ComputeEffectiveXp_AtDropOffLevelBoundary_AppliesOneReduction()
    {
        // Boundary: playerLevel == dropOffLevel. Old code returned full XP here
        // (0/5 = 0 reductions); the spec says one reduction already applies at
        // the threshold. (10-10)/5 + 1 = 1 → 10 × 0.9 = 9.
        var refData = new FakeRefData();
        refData.AddSkill("Blacksmithing", 1, false, "TestTable", 25);
        refData.AddXpTable("TestTable", [100L, 200L, 300L]);
        refData.AddRecipe("recipe_at_threshold", "At Threshold", "AtThreshold",
            "Blacksmithing", 1, "Blacksmithing", 10, 0, dropOffLevel: 10, dropOffPct: 0.1f, dropOffRate: 5);

        var character = MakeCharacter(
            skills: new Dictionary<string, CharacterSkill> { ["Blacksmithing"] = new(10, 0, 0, 100) },
            recipes: new Dictionary<string, int> { ["AtThreshold"] = 5 });

        var engine = new SkillAdvisorEngine(refData);
        var result = engine.Analyze("Blacksmithing", character)!;

        result.Recipes.Single(r => r.InternalName == "AtThreshold").EffectiveXp.Should().Be(9);
    }

    [Fact]
    public void ComputeEffectiveXp_BelowDropOffLevel_ReturnsFullXp()
    {
        // Negative-control: playerLevel < dropOffLevel still returns full XP
        // (the early-out wasn't touched by the fix, but pin it so a future
        // refactor can't silently regress it).
        var refData = new FakeRefData();
        refData.AddSkill("Blacksmithing", 1, false, "TestTable", 25);
        refData.AddXpTable("TestTable", [100L, 200L, 300L]);
        refData.AddRecipe("recipe_below", "Below Threshold", "BelowThreshold",
            "Blacksmithing", 1, "Blacksmithing", 10, 0, dropOffLevel: 11, dropOffPct: 0.1f, dropOffRate: 5);

        var character = MakeCharacter(
            skills: new Dictionary<string, CharacterSkill> { ["Blacksmithing"] = new(10, 0, 0, 100) },
            recipes: new Dictionary<string, int> { ["BelowThreshold"] = 5 });

        var engine = new SkillAdvisorEngine(refData);
        var result = engine.Analyze("Blacksmithing", character)!;

        result.Recipes.Single(r => r.InternalName == "BelowThreshold").EffectiveXp.Should().Be(10);
    }

    // ── Helpers ──────────────────────────────────────────────────────────

    private static CharacterSnapshot MakeCharacter(
        Dictionary<string, CharacterSkill> skills,
        Dictionary<string, int> recipes)
        => new("TestChar", "TestServer", DateTimeOffset.UtcNow, skills, recipes, new Dictionary<string, string>());

    private sealed class FakeRefData : IReferenceDataService
    {
        private readonly Dictionary<string, RecipeEntry> _recipes = new(StringComparer.Ordinal);
        private readonly Dictionary<string, RecipeEntry> _recipesByName = new(StringComparer.Ordinal);
        private readonly Dictionary<string, SkillEntry> _skills = new(StringComparer.Ordinal);
        private readonly Dictionary<string, XpTableEntry> _xpTables = new(StringComparer.Ordinal);

        public IReadOnlyList<string> Keys { get; } = ["items", "recipes", "skills", "xptables"];
        public IReadOnlyDictionary<long, ItemEntry> Items { get; } = new Dictionary<long, ItemEntry>();
        public IReadOnlyDictionary<string, ItemEntry> ItemsByInternalName { get; } = new Dictionary<string, ItemEntry>();
        public ItemKeywordIndex KeywordIndex { get; } = ItemKeywordIndex.Empty;
        public IReadOnlyDictionary<string, RecipeEntry> Recipes => _recipes;
        public IReadOnlyDictionary<string, RecipeEntry> RecipesByInternalName => _recipesByName;
        public IReadOnlyDictionary<string, SkillEntry> Skills => _skills;
        public IReadOnlyDictionary<string, XpTableEntry> XpTables => _xpTables;
        public IReadOnlyDictionary<string, NpcEntry> Npcs { get; } = new Dictionary<string, NpcEntry>();
        public IReadOnlyDictionary<string, AreaEntry> Areas { get; } = new Dictionary<string, AreaEntry>(StringComparer.Ordinal);
        public IReadOnlyDictionary<string, IReadOnlyList<ItemSource>> ItemSources { get; } = new Dictionary<string, IReadOnlyList<ItemSource>>();
        public IReadOnlyDictionary<string, AttributeEntry> Attributes { get; } = new Dictionary<string, AttributeEntry>();
        public IReadOnlyDictionary<string, PowerEntry> Powers { get; } = new Dictionary<string, PowerEntry>();
        public IReadOnlyDictionary<string, IReadOnlyList<string>> Profiles { get; } = new Dictionary<string, IReadOnlyList<string>>();
        public IReadOnlyDictionary<string, QuestEntry> Quests { get; } = new Dictionary<string, QuestEntry>();
        public IReadOnlyDictionary<string, QuestEntry> QuestsByInternalName { get; } = new Dictionary<string, QuestEntry>();
        public IReadOnlyDictionary<string, string> Strings { get; } = new Dictionary<string, string>(StringComparer.Ordinal);
        public ReferenceFileSnapshot GetSnapshot(string key) => new(key, ReferenceFileSource.Bundled, "test", null, 0);
        public Task RefreshAsync(string key, CancellationToken ct = default) => Task.CompletedTask;
        public Task RefreshAllAsync(CancellationToken ct = default) => Task.CompletedTask;
        public void BeginBackgroundRefresh() { }
        public event EventHandler<string>? FileUpdated { add { } remove { } }

        public void AddRecipe(string key, string name, string internalName, string skill, int skillLevelReq,
            string rewardSkill, int rewardXp, int rewardXpFirstTime,
            int? dropOffLevel, float? dropOffPct, int? dropOffRate, string? prereqRecipe = null,
            string? sortSkill = null)
        {
            var entry = new RecipeEntry(key, name, internalName, 0, skill, skillLevelReq,
                rewardSkill, rewardXp, rewardXpFirstTime, dropOffLevel, dropOffPct, dropOffRate, [], [],
                prereqRecipe, ProtoResultItems: null, ResultEffects: null, SortSkill: sortSkill);
            _recipes[key] = entry;
            _recipesByName[internalName] = entry;
        }

        public void AddSkill(string name, int id, bool combat, string xpTable, int maxBonusLevels)
            => _skills[name] = new SkillEntry(
                Key: name, DisplayName: name, Id: id, Combat: combat, XpTable: xpTable,
                MaxBonusLevels: maxBonusLevels, Parents: [],
                Rewards: new Dictionary<string, SkillRewardEntry>());

        public void AddXpTable(string internalName, long[] xpAmounts)
            => _xpTables[internalName] = new XpTableEntry(internalName, xpAmounts);
    }
}
