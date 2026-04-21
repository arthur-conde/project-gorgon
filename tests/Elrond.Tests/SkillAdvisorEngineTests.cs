using Elrond.Services;
using FluentAssertions;
using Gorgon.Shared.Character;
using Gorgon.Shared.Reference;
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
    public void GetSkillsWithRecipes_ReturnsOnlySkillsWithXpRecipes()
    {
        var refData = new FakeRefData();
        refData.AddRecipe("recipe_1", "Butter", "Butter", "Cooking", 1, "Cooking", 10, 40, null, null, null);
        refData.AddRecipe("recipe_2", "Med #1", "Med1", "Meditation", 1, "Meditation", 50, 200, null, null, null);
        refData.AddRecipe("recipe_3", "NoXp", "NoXp", "Alchemy", 1, "Alchemy", 0, 0, null, null, null);

        var engine = new SkillAdvisorEngine(refData);
        var skills = engine.GetSkillsWithRecipes();

        skills.Should().BeEquivalentTo(["Cooking", "Meditation"]);
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
        public IReadOnlyDictionary<string, RecipeEntry> Recipes => _recipes;
        public IReadOnlyDictionary<string, RecipeEntry> RecipesByInternalName => _recipesByName;
        public IReadOnlyDictionary<string, SkillEntry> Skills => _skills;
        public IReadOnlyDictionary<string, XpTableEntry> XpTables => _xpTables;
        public IReadOnlyDictionary<string, NpcEntry> Npcs { get; } = new Dictionary<string, NpcEntry>();
        public ReferenceFileSnapshot GetSnapshot(string key) => new(key, ReferenceFileSource.Bundled, "test", null, 0);
        public Task RefreshAsync(string key, CancellationToken ct = default) => Task.CompletedTask;
        public Task RefreshAllAsync(CancellationToken ct = default) => Task.CompletedTask;
        public void BeginBackgroundRefresh() { }
        public event EventHandler<string>? FileUpdated { add { } remove { } }

        public void AddRecipe(string key, string name, string internalName, string skill, int skillLevelReq,
            string rewardSkill, int rewardXp, int rewardXpFirstTime,
            int? dropOffLevel, float? dropOffPct, int? dropOffRate, string? prereqRecipe = null)
        {
            var entry = new RecipeEntry(key, name, internalName, 0, skill, skillLevelReq,
                rewardSkill, rewardXp, rewardXpFirstTime, dropOffLevel, dropOffPct, dropOffRate, [], [], prereqRecipe);
            _recipes[key] = entry;
            _recipesByName[internalName] = entry;
        }

        public void AddSkill(string name, int id, bool combat, string xpTable, int maxBonusLevels)
            => _skills[name] = new SkillEntry(name, id, combat, xpTable, maxBonusLevels);

        public void AddXpTable(string internalName, long[] xpAmounts)
            => _xpTables[internalName] = new XpTableEntry(internalName, xpAmounts);
    }
}
