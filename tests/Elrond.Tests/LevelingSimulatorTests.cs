using Elrond.Services;
using FluentAssertions;
using Mithril.Shared.Character;
using Mithril.Shared.Reference;
using Xunit;

namespace Elrond.Tests;

public class LevelingSimulatorTests
{
    [Fact]
    public void Simulate_SingleRecipe_CorrectCompletions()
    {
        var refData = new FakeRefData();
        refData.AddSkill("Cooking", 1, false, "TestTable", 25);
        refData.AddXpTable("TestTable", [100, 200, 300, 400, 500]);
        refData.AddRecipe("recipe_1", "Butter", "Butter", "Cooking", 1, "Cooking", 50, 0, null, null, null);

        // Level 1, 0/100 XP. Goal = 3.
        // Level 1 needs 100 XP = 2 completions. Level 2 needs 200 XP = 4 completions. Total = 6.
        var character = MakeCharacter(
            skills: new Dictionary<string, CharacterSkill>
            {
                ["Cooking"] = new(1, 0, 0, 100),
            },
            recipes: new Dictionary<string, int> { ["Butter"] = 5 });

        var engine = new SkillAdvisorEngine(refData);
        var simulator = new LevelingSimulator(refData, engine);
        var result = simulator.Simulate("Cooking", character, 3)!;

        result.Should().NotBeNull();
        result.TotalCompletions.Should().Be(6);
        result.GoalLevel.Should().Be(3);
        result.StartLevel.Should().Be(1);
    }

    [Fact]
    public void Simulate_UsesFirstTimeBonusesFirst()
    {
        var refData = new FakeRefData();
        refData.AddSkill("Cooking", 1, false, "TestTable", 25);
        refData.AddXpTable("TestTable", [100, 200, 300]);
        refData.AddRecipe("recipe_1", "Butter", "Butter", "Cooking", 1, "Cooking", 10, 80, null, null, null);
        refData.AddRecipe("recipe_2", "Bread", "Bread", "Cooking", 1, "Cooking", 10, 0, null, null, null);

        // Level 1, 0/100 XP. First-time bonus on Butter = 80 XP.
        // After bonus: 80 XP, remaining 20. Then 2 completions of best (10 XP each) = total 3 completions for level 2.
        var character = MakeCharacter(
            skills: new Dictionary<string, CharacterSkill>
            {
                ["Cooking"] = new(1, 0, 0, 100),
            },
            recipes: new Dictionary<string, int>
            {
                ["Butter"] = 0, // known, never completed
                ["Bread"] = 5,
            });

        var engine = new SkillAdvisorEngine(refData);
        var simulator = new LevelingSimulator(refData, engine);
        var result = simulator.Simulate("Cooking", character, 2)!;

        result.Should().NotBeNull();
        // First step should use first-time bonus
        result.Steps[0].UsesFirstTimeBonus.Should().BeTrue();
        result.Steps[0].RecipeName.Should().Be("Butter");
        result.Steps[0].Completions.Should().Be(1);
        // Total should be 1 (bonus) + 2 (grind) = 3
        result.TotalCompletions.Should().Be(3);
    }

    [Fact]
    public void Simulate_FirstTimeBonusAlone_ReachesGoal()
    {
        var refData = new FakeRefData();
        refData.AddSkill("Cooking", 1, false, "TestTable", 25);
        refData.AddXpTable("TestTable", [100, 200, 300]);
        refData.AddRecipe("recipe_1", "Butter", "Butter", "Cooking", 1, "Cooking", 10, 150, null, null, null);

        // Level 1, 50/100 XP. First-time bonus = 150 > 50 remaining. One craft reaches level 2 (and beyond).
        var character = MakeCharacter(
            skills: new Dictionary<string, CharacterSkill>
            {
                ["Cooking"] = new(1, 0, 50, 100),
            },
            recipes: new Dictionary<string, int> { ["Butter"] = 0 });

        var engine = new SkillAdvisorEngine(refData);
        var simulator = new LevelingSimulator(refData, engine);
        var result = simulator.Simulate("Cooking", character, 2)!;

        result.TotalCompletions.Should().Be(1);
        result.Steps.Should().HaveCount(1);
        result.Steps[0].UsesFirstTimeBonus.Should().BeTrue();
    }

    [Fact]
    public void Simulate_RecipeUnlocksAtHigherLevel_Switches()
    {
        var refData = new FakeRefData();
        refData.AddSkill("Cooking", 1, false, "TestTable", 25);
        refData.AddXpTable("TestTable", [100, 100, 100, 100, 100]);
        refData.AddRecipe("recipe_1", "Butter", "Butter", "Cooking", 1, "Cooking", 10, 0, null, null, null);
        refData.AddRecipe("recipe_2", "Steak", "Steak", "Cooking", 3, "Cooking", 50, 0, null, null, null);

        // Level 1, 0/100 XP. Goal = 5. Steak unlocks at level 3 and is much better.
        var character = MakeCharacter(
            skills: new Dictionary<string, CharacterSkill>
            {
                ["Cooking"] = new(1, 0, 0, 100),
            },
            recipes: new Dictionary<string, int>
            {
                ["Butter"] = 5,
                ["Steak"] = 5,
            });

        var engine = new SkillAdvisorEngine(refData);
        var simulator = new LevelingSimulator(refData, engine);
        var result = simulator.Simulate("Cooking", character, 5)!;

        // Levels 1-2: grind Butter (10 XP each). Levels 3-4: grind Steak (50 XP each).
        result.Steps.Should().Contain(s => s.RecipeName == "Butter");
        result.Steps.Should().Contain(s => s.RecipeName == "Steak");

        // After Steak unlocks, it should be used preferentially
        var steakStep = result.Steps.First(s => s.RecipeName == "Steak");
        steakStep.LevelAtStart.Should().BeGreaterThanOrEqualTo(3);
    }

    [Fact]
    public void Simulate_MultipleFirstTimeBonuses_AllConsumed()
    {
        var refData = new FakeRefData();
        refData.AddSkill("Cooking", 1, false, "TestTable", 25);
        refData.AddXpTable("TestTable", [1000, 1000, 1000]);
        refData.AddRecipe("recipe_1", "Butter", "Butter", "Cooking", 1, "Cooking", 10, 200, null, null, null);
        refData.AddRecipe("recipe_2", "Bread", "Bread", "Cooking", 1, "Cooking", 10, 300, null, null, null);
        refData.AddRecipe("recipe_3", "Cake", "Cake", "Cooking", 1, "Cooking", 10, 100, null, null, null);

        // Level 1, 0/1000 XP. Three first-time bonuses available.
        var character = MakeCharacter(
            skills: new Dictionary<string, CharacterSkill>
            {
                ["Cooking"] = new(1, 0, 0, 1000),
            },
            recipes: new Dictionary<string, int>
            {
                ["Butter"] = 0,
                ["Bread"] = 0,
                ["Cake"] = 0,
            });

        var engine = new SkillAdvisorEngine(refData);
        var simulator = new LevelingSimulator(refData, engine);
        var result = simulator.Simulate("Cooking", character, 2)!;

        // All three first-time bonuses should be consumed (300 + 200 + 100 = 600 XP)
        var bonusSteps = result.Steps.Where(s => s.UsesFirstTimeBonus).ToList();
        bonusSteps.Should().HaveCount(3);
        bonusSteps.Sum(s => s.FirstTimeBonusXp).Should().Be(600);
    }

    [Fact]
    public void Simulate_UnlearnedRecipe_FirstTimeBonusUsedOnceAvailable()
    {
        var refData = new FakeRefData();
        refData.AddSkill("Cooking", 1, false, "TestTable", 25);
        refData.AddXpTable("TestTable", [100, 100, 100, 100, 100]);
        // Butter: already learned. Steak: NOT learned yet (not in RecipeCompletions), level 2 req.
        refData.AddRecipe("recipe_1", "Butter", "Butter", "Cooking", 1, "Cooking", 10, 0, null, null, null);
        refData.AddRecipe("recipe_2", "Steak", "Steak", "Cooking", 2, "Cooking", 10, 500, null, null, null);

        // Character only knows Butter, level 1. Steak not in completions at all.
        var character = MakeCharacter(
            skills: new Dictionary<string, CharacterSkill>
            {
                ["Cooking"] = new(1, 0, 0, 100),
            },
            recipes: new Dictionary<string, int> { ["Butter"] = 5 });

        var engine = new SkillAdvisorEngine(refData);
        var simulator = new LevelingSimulator(refData, engine);
        var result = simulator.Simulate("Cooking", character, 5)!;

        // Steak's first-time bonus (500 XP) should be used once available at level 2
        var steakBonusStep = result.Steps.FirstOrDefault(s => s.RecipeName == "Steak" && s.UsesFirstTimeBonus);
        steakBonusStep.Should().NotBeNull();
        steakBonusStep!.LevelAtStart.Should().BeGreaterThanOrEqualTo(2);
    }

    [Fact]
    public void Simulate_PrereqRecipe_RespectedBeforeUnlocking()
    {
        var refData = new FakeRefData();
        refData.AddSkill("Cooking", 1, false, "TestTable", 25);
        refData.AddXpTable("TestTable", [100, 100, 100, 100, 100]);
        // Chain: Butter (no prereq) -> Bread (prereq: Butter) -> Cake (prereq: Bread)
        refData.AddRecipe("recipe_1", "Butter", "Butter", "Cooking", 1, "Cooking", 10, 50, null, null, null);
        refData.AddRecipe("recipe_2", "Bread", "Bread", "Cooking", 1, "Cooking", 10, 200, null, null, null, prereqRecipe: "Butter");
        refData.AddRecipe("recipe_3", "Cake", "Cake", "Cooking", 1, "Cooking", 10, 300, null, null, null, prereqRecipe: "Bread");

        // None learned yet
        var character = MakeCharacter(
            skills: new Dictionary<string, CharacterSkill>
            {
                ["Cooking"] = new(1, 0, 0, 100),
            },
            recipes: new Dictionary<string, int>());

        var engine = new SkillAdvisorEngine(refData);
        var simulator = new LevelingSimulator(refData, engine);
        var result = simulator.Simulate("Cooking", character, 3)!;

        // Butter must be used first (unlocks Bread), then Bread (unlocks Cake), then Cake
        var bonusSteps = result.Steps.Where(s => s.UsesFirstTimeBonus).ToList();
        bonusSteps.Should().HaveCountGreaterThanOrEqualTo(2);

        // Butter's bonus should come before Bread's bonus
        var butterIdx = result.Steps.ToList().FindIndex(s => s.RecipeName == "Butter" && s.UsesFirstTimeBonus);
        var breadIdx = result.Steps.ToList().FindIndex(s => s.RecipeName == "Bread" && s.UsesFirstTimeBonus);
        butterIdx.Should().BeLessThan(breadIdx);
    }

    [Fact]
    public void Simulate_AlreadyAtGoal_ReturnsZeroSteps()
    {
        var refData = new FakeRefData();
        refData.AddSkill("Cooking", 1, false, "TestTable", 25);
        refData.AddXpTable("TestTable", [100, 200]);
        refData.AddRecipe("recipe_1", "Butter", "Butter", "Cooking", 1, "Cooking", 10, 0, null, null, null);

        var character = MakeCharacter(
            skills: new Dictionary<string, CharacterSkill>
            {
                ["Cooking"] = new(5, 0, 0, 500),
            },
            recipes: new Dictionary<string, int> { ["Butter"] = 10 });

        var engine = new SkillAdvisorEngine(refData);
        var simulator = new LevelingSimulator(refData, engine);
        var result = simulator.Simulate("Cooking", character, 5)!;

        result.TotalCompletions.Should().Be(0);
        result.Steps.Should().BeEmpty();
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
        public IReadOnlyDictionary<string, IReadOnlyList<ItemSource>> ItemSources { get; } = new Dictionary<string, IReadOnlyList<ItemSource>>();
        public IReadOnlyDictionary<string, AttributeEntry> Attributes { get; } = new Dictionary<string, AttributeEntry>();
        public IReadOnlyDictionary<string, PowerEntry> Powers { get; } = new Dictionary<string, PowerEntry>();
        public IReadOnlyDictionary<string, IReadOnlyList<string>> Profiles { get; } = new Dictionary<string, IReadOnlyList<string>>();
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
