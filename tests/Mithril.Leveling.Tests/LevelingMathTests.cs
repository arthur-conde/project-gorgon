using FluentAssertions;
using Mithril.Reference.Models.Items;
using Mithril.Reference.Models.Quests;
using Mithril.Reference.Models.Recipes;
using Mithril.Shared.Reference;
using Xunit;

namespace Mithril.Leveling.Tests;

/// <summary>
/// Coverage for the skill-XP math lifted out of Elrond (#225). The drop-off cases
/// pin the #159 fix (first reduction applies AT the drop-off level, not after it);
/// the <c>XpForCraft</c> cases pin the first-time-per-character bonus semantics.
/// </summary>
public class LevelingMathTests
{
    // ── EffectiveXpPerCraft / drop-off ───────────────────────────────────

    [Fact]
    public void NoDropOffMetadata_ReturnsBaseXp()
    {
        var math = new LevelingMath(new FakeRef());
        var recipe = new Recipe { RewardSkill = "Cooking", RewardSkillXp = 100 };

        math.EffectiveXpPerCraft(recipe, rewardSkillLevel: 999).Should().Be(100);
    }

    [Fact]
    public void BelowDropOffLevel_ReturnsBaseXp()
    {
        var math = new LevelingMath(new FakeRef());
        var recipe = new Recipe
        {
            RewardSkill = "Cooking",
            RewardSkillXp = 100,
            RewardSkillXpDropOffLevel = 20,
            RewardSkillXpDropOffPct = 0.5,
            RewardSkillXpDropOffRate = 5,
        };

        math.EffectiveXpPerCraft(recipe, rewardSkillLevel: 19).Should().Be(100);
    }

    [Fact]
    public void AtDropOffLevel_AppliesFirstReduction_159Regression()
    {
        var math = new LevelingMath(new FakeRef());
        var recipe = new Recipe
        {
            RewardSkill = "Cooking",
            RewardSkillXp = 100,
            RewardSkillXpDropOffLevel = 20,
            RewardSkillXpDropOffPct = 0.5,
            RewardSkillXpDropOffRate = 5,
        };

        // At exactly the drop-off level one reduction already applies (×0.5),
        // NOT zero reductions. This is the #159 behaviour.
        math.EffectiveXpPerCraft(recipe, rewardSkillLevel: 20).Should().Be(50);
    }

    [Fact]
    public void CompoundsOnePerRatePastDropOff()
    {
        var math = new LevelingMath(new FakeRef());
        var recipe = new Recipe
        {
            RewardSkill = "Cooking",
            RewardSkillXp = 100,
            RewardSkillXpDropOffLevel = 20,
            RewardSkillXpDropOffPct = 0.5,
            RewardSkillXpDropOffRate = 5,
        };

        // level 20 → 1 reduction (×0.5 = 50); level 25 → 2 reductions (×0.25 = 25);
        // level 30 → 3 reductions (×0.125 → (int)12.5 = 12).
        math.EffectiveXpPerCraft(recipe, 25).Should().Be(25);
        math.EffectiveXpPerCraft(recipe, 30).Should().Be(12);
    }

    // ── XpForCraft / first-time bonus ────────────────────────────────────

    [Fact]
    public void XpForCraft_KnownWithZeroCompletions_BonusAvailable()
    {
        var math = new LevelingMath(new FakeRef());
        var recipe = new Recipe
        {
            InternalName = "MakeBread",
            RewardSkill = "Cooking",
            RewardSkillXp = 40,
            RewardSkillXpFirstTime = 250,
        };
        var skills = new SkillState(new Dictionary<string, SkillProgress>
        {
            ["Cooking"] = new(Level: 5, BonusLevels: 0, XpTowardNextLevel: 0, XpNeededForNextLevel: 100),
        });
        var history = new RecipeHistory(new Dictionary<string, int> { ["MakeBread"] = 0 });

        var delta = math.XpForCraft(recipe, skills, history);

        delta.RewardSkill.Should().Be("Cooking");
        delta.BaseXp.Should().Be(40);
        delta.EffectiveXp.Should().Be(40);
        delta.FirstTimeBonusAvailable.Should().BeTrue();
        delta.NextCraftXp.Should().Be(250, because: "the unconsumed first-time bonus applies to the next craft");
    }

    [Fact]
    public void XpForCraft_AlreadyCompleted_BonusConsumed()
    {
        var math = new LevelingMath(new FakeRef());
        var recipe = new Recipe
        {
            InternalName = "MakeBread",
            RewardSkill = "Cooking",
            RewardSkillXp = 40,
            RewardSkillXpFirstTime = 250,
        };
        var skills = new SkillState(new Dictionary<string, SkillProgress>
        {
            ["Cooking"] = new(5, 0, 0, 100),
        });
        var history = new RecipeHistory(new Dictionary<string, int> { ["MakeBread"] = 3 });

        var delta = math.XpForCraft(recipe, skills, history);

        delta.FirstTimeBonusAvailable.Should().BeFalse();
        delta.NextCraftXp.Should().Be(40, because: "the bonus is spent once the recipe has any completions");
    }

    [Fact]
    public void XpForCraft_NotKnown_NoBonus()
    {
        var math = new LevelingMath(new FakeRef());
        var recipe = new Recipe
        {
            InternalName = "MakeBread",
            RewardSkill = "Cooking",
            RewardSkillXp = 40,
            RewardSkillXpFirstTime = 250,
        };

        var delta = math.XpForCraft(recipe, SkillState.Empty, RecipeHistory.Empty);

        delta.FirstTimeBonusAvailable.Should().BeFalse(
            because: "an unlearned recipe has no available bonus per the row semantics — "
                     + "the simulator's looser policy stays in Elrond");
        delta.NextCraftXp.Should().Be(40);
    }

    [Fact]
    public void XpForCraft_AppliesDropOffAtRewardSkillLevel()
    {
        var math = new LevelingMath(new FakeRef());
        var recipe = new Recipe
        {
            InternalName = "MakeBread",
            RewardSkill = "Cooking",
            RewardSkillXp = 100,
            RewardSkillXpDropOffLevel = 10,
            RewardSkillXpDropOffPct = 0.5,
            RewardSkillXpDropOffRate = 5,
        };
        var skills = new SkillState(new Dictionary<string, SkillProgress>
        {
            ["Cooking"] = new(Level: 10, BonusLevels: 0, XpTowardNextLevel: 0, XpNeededForNextLevel: 1),
        });

        math.XpForCraft(recipe, skills, RecipeHistory.Empty).EffectiveXp.Should().Be(50);
    }

    // ── ResolveXpTable / XpToGoal ────────────────────────────────────────

    [Fact]
    public void ResolveXpTable_MissingSkill_ReturnsNull()
    {
        var math = new LevelingMath(new FakeRef());
        math.ResolveXpTable("Nope").Should().BeNull();
    }

    [Fact]
    public void ResolveXpTable_ResolvesSkillToTable()
    {
        var fake = new FakeRef();
        fake.SkillsRaw["Cooking"] = Skill("Cooking", "TypicalNoncombatSkill");
        fake.XpTablesRaw["TypicalNoncombatSkill"] = new XpTableEntry("TypicalNoncombatSkill", [100, 200, 400]);
        var math = new LevelingMath(fake);

        math.ResolveXpTable("Cooking").Should().Equal([100, 200, 400]);
    }

    [Fact]
    public void XpToGoal_NoTable_ReturnsRemainingInCurrentLevelOnly()
    {
        var math = new LevelingMath(new FakeRef());

        // 100 needed, 30 in → 70 remaining; no table → can't project further levels.
        math.XpToGoal("Cooking", currentLevel: 5, currentXp: 30, currentLevelXpNeeded: 100, goalLevel: 9)
            .Should().Be(70);
    }

    [Fact]
    public void XpToGoal_SumsCurrentRemainderPlusInterveningLevels()
    {
        var fake = new FakeRef();
        fake.SkillsRaw["Cooking"] = Skill("Cooking", "T");
        // index 0 = XP for level 1, ... index 4 = XP for level 5, ...
        fake.XpTablesRaw["T"] = new XpTableEntry("T", [10, 20, 30, 40, 50, 60, 70, 80, 90]);
        var math = new LevelingMath(fake);

        // current level 5, 30 toward next, 100 needed → remainder 70.
        // goal 8 → add levels 6 and 7 = xpAmounts[5] + xpAmounts[6] = 60 + 70.
        math.XpToGoal("Cooking", currentLevel: 5, currentXp: 30, currentLevelXpNeeded: 100, goalLevel: 8)
            .Should().Be(70 + 60 + 70);
    }

    private static SkillEntry Skill(string key, string xpTable)
        => new(Key: key, DisplayName: key, Id: 0, Combat: false,
               XpTable: xpTable, MaxBonusLevels: 0, Parents: [],
               Rewards: new Dictionary<string, SkillRewardEntry>());

    /// <summary>
    /// Minimal <see cref="IReferenceDataService"/> fake — only Skills + XpTables
    /// are mutable (all <see cref="LevelingMath"/> touches); the rest are empty.
    /// </summary>
    private sealed class FakeRef : IReferenceDataService
    {
        public Dictionary<string, SkillEntry> SkillsRaw { get; } = new(StringComparer.Ordinal);
        public Dictionary<string, XpTableEntry> XpTablesRaw { get; } = new(StringComparer.Ordinal);
        public IReadOnlyDictionary<string, SkillEntry> Skills => SkillsRaw;
        public IReadOnlyDictionary<string, XpTableEntry> XpTables => XpTablesRaw;

        public IReadOnlyList<string> Keys { get; } = ["skills", "xptables"];
        public IReadOnlyDictionary<long, Item> Items { get; } = new Dictionary<long, Item>();
        public IReadOnlyDictionary<string, Item> ItemsByInternalName { get; } = new Dictionary<string, Item>();
        public ItemKeywordIndex KeywordIndex => new(new Dictionary<long, Item>());
        public IReadOnlyDictionary<string, Recipe> Recipes { get; } = new Dictionary<string, Recipe>();
        public IReadOnlyDictionary<string, Recipe> RecipesByInternalName { get; } = new Dictionary<string, Recipe>();
        public IReadOnlyDictionary<string, NpcEntry> Npcs { get; } = new Dictionary<string, NpcEntry>();
        public IReadOnlyDictionary<string, AreaEntry> Areas { get; } = new Dictionary<string, AreaEntry>(StringComparer.Ordinal);
        public IReadOnlyDictionary<string, IReadOnlyList<ItemSource>> ItemSources { get; } = new Dictionary<string, IReadOnlyList<ItemSource>>();
        public IReadOnlyDictionary<string, AttributeEntry> Attributes { get; } = new Dictionary<string, AttributeEntry>();
        public IReadOnlyDictionary<string, PowerEntry> Powers { get; } = new Dictionary<string, PowerEntry>();
        public IReadOnlyDictionary<string, IReadOnlyList<string>> Profiles { get; } = new Dictionary<string, IReadOnlyList<string>>();
        public IReadOnlyDictionary<string, Quest> Quests { get; } = new Dictionary<string, Quest>();
        public IReadOnlyDictionary<string, Quest> QuestsByInternalName { get; } = new Dictionary<string, Quest>();
        public IReadOnlyDictionary<string, string> Strings { get; } = new Dictionary<string, string>(StringComparer.Ordinal);
        public ReferenceFileSnapshot GetSnapshot(string key) => new(key, ReferenceFileSource.Bundled, "test", null, 0);
        public Task RefreshAsync(string key, CancellationToken ct = default) => Task.CompletedTask;
        public Task RefreshAllAsync(CancellationToken ct = default) => Task.CompletedTask;
        public void BeginBackgroundRefresh() { }
        public event EventHandler<string>? FileUpdated { add { } remove { } }
    }
}
