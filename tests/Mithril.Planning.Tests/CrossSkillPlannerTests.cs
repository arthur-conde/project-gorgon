using FluentAssertions;
using Mithril.Leveling;
using Mithril.Reference.Models.Items;
using Mithril.Reference.Models.Quests;
using Mithril.Reference.Models.Recipes;
using Mithril.Shared.Crafting;
using Mithril.Shared.Reference;
using Xunit;

namespace Mithril.Planning.Tests;

/// <summary>
/// Coverage for the cross-skill leveling planner (#227, engine-only). Pins the
/// v1 contract: goal/umbrella guards, single-recipe grind, recipe-switch +
/// skill-source unlock events, first-time-bonus ordering, sourcing-policy
/// pruning, asserted unlocks, and depth-1 intermediate-craft reuse credit.
/// </summary>
public class CrossSkillPlannerTests
{
    private const string Skill = "Smithing";

    private static CrossSkillPlanner Planner(FakeRef data)
        => new(data, new LevelingMath(data), new RecipeExpander(data));

    private static SkillState State(int level, long xpToward = 0, long xpNeeded = 100)
        => new(new Dictionary<string, SkillProgress>
        {
            [Skill] = new(level, 0, xpToward, xpNeeded),
        });

    // ── Guards ───────────────────────────────────────────────────────────

    [Fact]
    public void GoalAlreadyMet_ReturnsCompleteEmptyPlan()
    {
        var data = new FakeRef().AddSkill(Skill).AddXpTable(100);
        var plan = Planner(data).Plan(new SkillTarget(Skill, 5), State(5), RecipeHistory.Empty);

        plan.Should().NotBeNull();
        plan!.IsComplete.Should().BeTrue();
        plan.Phases.Should().BeEmpty();
        plan.TotalCrafts.Should().Be(0);
    }

    [Fact]
    public void UmbrellaSkill_NoXpTable_ReturnsNull()
    {
        var data = new FakeRef().AddSkill(Skill, xpTable: "");
        Planner(data).Plan(new SkillTarget(Skill, 5), State(1), RecipeHistory.Empty)
            .Should().BeNull();
    }

    [Fact]
    public void SkillAbsentFromState_ReturnsNull()
    {
        var data = new FakeRef().AddSkill(Skill).AddXpTable(100);
        Planner(data).Plan(new SkillTarget(Skill, 5), SkillState.Empty, RecipeHistory.Empty)
            .Should().BeNull();
    }

    // ── Single-recipe grind ──────────────────────────────────────────────

    [Fact]
    public void SingleKnownRecipe_GrindsToGoal_MergedIntoOnePhase()
    {
        var data = new FakeRef().AddSkill(Skill).AddXpTable(100)
            .AddItem(1, "Nail")
            .AddRecipe("ForgeNail", Skill, xp: 50, produces: (1, 1));

        var history = new RecipeHistory(new Dictionary<string, int> { ["ForgeNail"] = 1 });
        var plan = Planner(data).Plan(new SkillTarget(Skill, 3), State(1), history);

        plan.Should().NotBeNull();
        plan!.Phases.Should().ContainSingle();
        var phase = plan.Phases[0];
        phase.RecipeInternalName.Should().Be("ForgeNail");
        phase.LevelAtStart.Should().Be(1);
        phase.LevelAtEnd.Should().Be(3);
        phase.PredictedCrafts.Should().Be(4, because: "200 XP @ 50/craft");
        plan.TotalCrafts.Should().Be(4);
        plan.FinalState.LevelOf(Skill).Should().Be(3);
    }

    // ── Recipe-switch point + skill-source unlock ────────────────────────

    [Fact]
    public void HigherXpRecipe_UnlocksMidGrind_EmitsSwitchPhaseAndUnlockEvent()
    {
        var data = new FakeRef().AddSkill(Skill).AddXpTable(100)
            .AddItem(1, "Tack").AddItem(2, "Spike")
            .AddRecipe("ForgeTack", Skill, xp: 20, produces: (1, 1))             // always available (known)
            .AddRecipe("ForgeSpike", Skill, xp: 100, skillLevelReq: 3, produces: (2, 1)); // unlocks @ L3

        var history = new RecipeHistory(new Dictionary<string, int> { ["ForgeTack"] = 1 });
        var plan = Planner(data).Plan(new SkillTarget(Skill, 5), State(1), history)!;

        // Grind the low recipe, then switch to the higher-XP one when it unlocks.
        plan.Phases.Select(p => p.RecipeInternalName)
            .Should().Equal(new[] { "ForgeTack", "ForgeSpike" });
        plan.Phases[0].LevelAtEnd.Should().Be(3);
        plan.Phases[1].LevelAtStart.Should().Be(3);

        plan.Unlocks.Should().ContainSingle();
        plan.Unlocks[0].RecipeInternalName.Should().Be("ForgeSpike");
        plan.Unlocks[0].AtLevel.Should().Be(3);
    }

    // ── First-time bonus ─────────────────────────────────────────────────

    [Fact]
    public void FirstTimeBonus_SpentBeforeGrinding_HighestFirst()
    {
        var data = new FakeRef().AddSkill(Skill).AddXpTable(1000)
            .AddItem(1, "A").AddItem(2, "B")
            .AddRecipe("RecipeLowBonus", Skill, xp: 10, firstTime: 100, produces: (1, 1))
            .AddRecipe("RecipeHighBonus", Skill, xp: 10, firstTime: 400, produces: (2, 1));

        var history = new RecipeHistory(new Dictionary<string, int>
        {
            ["RecipeLowBonus"] = 0,   // known, bonus unspent
            ["RecipeHighBonus"] = 0,
        });
        var plan = Planner(data).Plan(new SkillTarget(Skill, 2), State(1, xpNeeded: 1000), history)!;

        var bonusPhases = plan.Phases.Where(p => p.UsesFirstTimeBonus).ToList();
        bonusPhases.Should().NotBeEmpty();
        bonusPhases[0].RecipeInternalName.Should().Be("RecipeHighBonus",
            because: "the largest first-time bonus is spent first");
    }

    // ── Sourcing policy ──────────────────────────────────────────────────

    [Fact]
    public void SourcingIgnore_ExcludesRecipeWhoseOutputIsIgnored()
    {
        var data = new FakeRef().AddSkill(Skill).AddXpTable(100)
            .AddItem(1, "Junk")
            .AddRecipe("ForgeJunk", Skill, xp: 50, produces: (1, 1));

        var history = new RecipeHistory(new Dictionary<string, int> { ["ForgeJunk"] = 1 });
        var sourcing = new SourcingPolicy(new Dictionary<string, SourcingMode>
        {
            ["Junk"] = SourcingMode.Ignore,
        });

        // Only candidate is ignored → no usable recipes → null.
        Planner(data).Plan(new SkillTarget(Skill, 3), State(1), history, sourcing: sourcing)
            .Should().BeNull();
    }

    // ── Asserted unlocks ─────────────────────────────────────────────────

    [Fact]
    public void NonSkillGatedUnknownRecipe_OnlyConsideredWhenAsserted()
    {
        var data = new FakeRef().AddSkill(Skill).AddXpTable(100)
            .AddItem(1, "Charm")
            .AddRecipe("WeaveCharm", Skill, xp: 50, skillLevelReq: 0, produces: (1, 1));

        // Unknown, not skill-gated, not asserted → unusable → null.
        Planner(data).Plan(new SkillTarget(Skill, 3), State(1), RecipeHistory.Empty)
            .Should().BeNull();

        // Asserted → considered.
        var plan = Planner(data).Plan(new SkillTarget(Skill, 3), State(1), RecipeHistory.Empty,
            asserted: new AssertedUnlocks(["WeaveCharm"]));
        plan.Should().NotBeNull();
        plan!.Phases.Should().ContainSingle().Which.RecipeInternalName.Should().Be("WeaveCharm");
    }

    // ── Intermediate-craft reuse credit (depth 1) ────────────────────────

    [Fact]
    public void IntermediateCraftThatAlsoLevels_CreditsItsXpAgainstThePhase()
    {
        // Crafting 1 Widget requires 1 Gear; making Gear also rewards Smithing.
        var data = new FakeRef().AddSkill(Skill).AddXpTable(100)
            .AddItem(1, "Widget").AddItem(2, "Gear").AddItem(3, "Ore")
            .AddRecipe("MakeWidget", Skill, xp: 10, produces: (1, 1), ingredients: (2, 1))
            .AddRecipe("MakeGear", Skill, xp: 5, produces: (2, 1), ingredients: (3, 1));

        var history = new RecipeHistory(new Dictionary<string, int>
        {
            ["MakeWidget"] = 1,
            ["MakeGear"] = 1,
        });
        var plan = Planner(data).Plan(new SkillTarget(Skill, 2), State(1), history)!;

        var widgetPhase = plan.Phases.Single(p => p.RecipeInternalName == "MakeWidget");
        widgetPhase.XpPerCraft.Should().Be(10, because: "base XP excludes reuse");
        widgetPhase.IntermediateReuseXpPerCraft.Should().Be(5,
            because: "each Widget needs a Gear craft, which also grants 5 Smithing XP");
        // Effective 15 XP/craft → ceil(100/15) = 7 crafts, fewer than the naive 10.
        widgetPhase.PredictedCrafts.Should().Be(7);
    }

    // ── Recipe.MaxUses (per-character lifetime cap) ──────────────────────

    [Fact]
    public void MaxUses_CapsGrind_ThenSwitchesToNextBestRecipe()
    {
        // Capped recipe is the highest XP but usable only twice ever; the
        // planner must grind it ≤2× then fall through to the uncapped one.
        var data = new FakeRef().AddSkill(Skill).AddXpTable(100)
            .AddItem(1, "Rune").AddItem(2, "Brick")
            .AddRecipe("ScribeRune", Skill, xp: 100, produces: (1, 1), skillLevelReq: 1, maxUses: 2)
            .AddRecipe("LayBrick", Skill, xp: 40, produces: (2, 1), skillLevelReq: 1);

        var plan = Planner(data).Plan(new SkillTarget(Skill, 5), State(1), RecipeHistory.Empty)!;

        plan.Phases.Where(p => p.RecipeInternalName == "ScribeRune").Sum(p => p.PredictedCrafts)
            .Should().Be(2, because: "MaxUses=2 is a hard per-character lifetime cap");
        plan.Phases.Should().Contain(p => p.RecipeInternalName == "LayBrick",
            because: "the planner switches to the uncapped recipe once the cap is hit");
        plan.FinalState.LevelOf(Skill).Should().Be(5, because: "the goal is still reached via the fallback");
    }

    [Fact]
    public void MaxUses_PriorHistoryCountsAgainstTheCap()
    {
        // ScribeRune already crafted twice on this character ⇒ cap exhausted;
        // it must not appear in the plan at all.
        var data = new FakeRef().AddSkill(Skill).AddXpTable(100)
            .AddItem(1, "Rune").AddItem(2, "Brick")
            .AddRecipe("ScribeRune", Skill, xp: 100, produces: (1, 1), skillLevelReq: 1, maxUses: 2)
            .AddRecipe("LayBrick", Skill, xp: 40, produces: (2, 1), skillLevelReq: 1);

        var history = new RecipeHistory(new Dictionary<string, int> { ["ScribeRune"] = 2 });
        var plan = Planner(data).Plan(new SkillTarget(Skill, 4), State(1), history)!;

        plan.Phases.Should().NotContain(p => p.RecipeInternalName == "ScribeRune",
            because: "history completions count toward MaxUses — the cap is already spent");
        plan.Phases.Should().OnlyContain(p => p.RecipeInternalName == "LayBrick");
        plan.FinalState.LevelOf(Skill).Should().Be(4);
    }

    // ── OtherRequirements gates ──────────────────────────────────────────

    [Fact]
    public void OtherRequirements_AlwaysFail_RecipeNeverScheduled()
    {
        // AlwaysFail recipe advertises the highest XP but can never succeed
        // (the ImproveProphesied* family) — it must never be picked.
        var data = new FakeRef().AddSkill(Skill).AddXpTable(100)
            .AddItem(1, "Idol").AddItem(2, "Plank")
            .AddRecipe("ImproveProphesiedIdol", Skill, xp: 1000, produces: (1, 1), skillLevelReq: 1,
                otherRequirements: [new AlwaysFailRequirement()])
            .AddRecipe("CarvePlank", Skill, xp: 40, produces: (2, 1), skillLevelReq: 1);

        var plan = Planner(data).Plan(new SkillTarget(Skill, 4), State(1), RecipeHistory.Empty)!;

        plan.Phases.Should().NotContain(p => p.RecipeInternalName == "ImproveProphesiedIdol",
            because: "an AlwaysFail recipe can never succeed regardless of XP");
        plan.Phases.Should().OnlyContain(p => p.RecipeInternalName == "CarvePlank");
        plan.FinalState.LevelOf(Skill).Should().Be(4);
    }

    [Fact]
    public void OtherRequirements_RecipeUsed_SelfReferentialCap_IsHonoured()
    {
        // WeatherWitching shape: the recipe requires itself used ≤0 times ⇒
        // craftable exactly once per character, then the planner must switch.
        var data = new FakeRef().AddSkill(Skill).AddXpTable(100)
            .AddItem(1, "Sun").AddItem(2, "Twig")
            .AddRecipe("SongOfTheSun", Skill, xp: 100, produces: (1, 1), skillLevelReq: 1,
                otherRequirements: [new RecipeUsedRequirement { Recipe = "SongOfTheSun", MaxTimesUsed = 0 }])
            .AddRecipe("SnapTwig", Skill, xp: 40, produces: (2, 1), skillLevelReq: 1);

        var plan = Planner(data).Plan(new SkillTarget(Skill, 5), State(1), RecipeHistory.Empty)!;

        plan.Phases.Where(p => p.RecipeInternalName == "SongOfTheSun").Sum(p => p.PredictedCrafts)
            .Should().Be(1, because: "RecipeUsed{self, 0} ⇒ exactly one craft ever");
        plan.Phases.Should().Contain(p => p.RecipeInternalName == "SnapTwig");
        plan.FinalState.LevelOf(Skill).Should().Be(5);
    }

    [Fact]
    public void OtherRequirements_RecipeUsed_PriorHistoryCountsAgainstTheCap()
    {
        var data = new FakeRef().AddSkill(Skill).AddXpTable(100)
            .AddItem(1, "Sun").AddItem(2, "Twig")
            .AddRecipe("SongOfTheSun", Skill, xp: 100, produces: (1, 1), skillLevelReq: 1,
                otherRequirements: [new RecipeUsedRequirement { Recipe = "SongOfTheSun", MaxTimesUsed = 0 }])
            .AddRecipe("SnapTwig", Skill, xp: 40, produces: (2, 1), skillLevelReq: 1);

        var history = new RecipeHistory(new Dictionary<string, int> { ["SongOfTheSun"] = 1 });
        var plan = Planner(data).Plan(new SkillTarget(Skill, 4), State(1), history)!;

        plan.Phases.Should().NotContain(p => p.RecipeInternalName == "SongOfTheSun",
            because: "cap 0+1=1 already spent by history");
        plan.Phases.Should().OnlyContain(p => p.RecipeInternalName == "SnapTwig");
    }

    [Fact]
    public void OtherRequirements_RecipeKnown_GatesUntilTheReferencedRecipeIsKnown()
    {
        var data = new FakeRef().AddSkill(Skill).AddXpTable(100)
            .AddItem(1, "Augury").AddItem(2, "Note")
            .AddRecipe("Augury2", Skill, xp: 100, produces: (1, 1), skillLevelReq: 1,
                otherRequirements: [new RecipeKnownRequirement { Recipe = "Augury1" }])
            .AddRecipe("Scribble", Skill, xp: 40, produces: (2, 1), skillLevelReq: 1);

        // Augury1 unknown ⇒ Augury2 gated out; plan falls back to Scribble.
        var gated = Planner(data).Plan(new SkillTarget(Skill, 4), State(1), RecipeHistory.Empty)!;
        gated.Phases.Should().OnlyContain(p => p.RecipeInternalName == "Scribble");

        // Augury1 known ⇒ Augury2 available and (higher XP) preferred.
        var known = new RecipeHistory(new Dictionary<string, int> { ["Augury1"] = 1 });
        var ok = Planner(data).Plan(new SkillTarget(Skill, 4), State(1), known)!;
        ok.Phases.Should().Contain(p => p.RecipeInternalName == "Augury2",
            because: "the RecipeKnown gate is satisfied once Augury1 is known");
    }

    // ── Learnability policy (trainer/quest gating, #401) ─────────────────

    [Fact]
    public void TrainerGatedRecipe_DefaultPolicy_StillAutoLearned_NoRegression()
    {
        // A skill-gated recipe the game trains at an NPC. Under the default
        // (optimistic) policy the planner still assumes it's learned at the gate.
        var data = new FakeRef().AddSkill(Skill).AddXpTable(100)
            .AddItem(1, "Blade")
            .AddRecipe("ForgeBlade", Skill, xp: 50, skillLevelReq: 1, produces: (1, 1))
            .AddRecipeSource("ForgeBlade", "Training", npc: "NPC_Smith");

        var plan = Planner(data).Plan(new SkillTarget(Skill, 3), State(1), RecipeHistory.Empty)!;

        plan.Phases.Should().ContainSingle().Which.RecipeInternalName.Should().Be("ForgeBlade",
            because: "default LearnabilityPolicy keeps the v1 auto-learn behavior");
    }

    [Fact]
    public void TrainerOrQuestGatedRecipe_StrictPolicy_GatedUnlessKnownOrAsserted()
    {
        FakeRef Data() => new FakeRef().AddSkill(Skill).AddXpTable(100)
            .AddItem(1, "Blade").AddItem(2, "Charmstone")
            .AddRecipe("ForgeBlade", Skill, xp: 50, skillLevelReq: 1, produces: (1, 1))
            .AddRecipeSource("ForgeBlade", "Training", npc: "NPC_Smith")
            .AddRecipe("CutCharmstone", Skill, xp: 50, skillLevelReq: 1, produces: (2, 1))
            .AddRecipeSource("CutCharmstone", "Quest");

        var strict = LearnabilityPolicy.RequireKnownForTrainerAndQuest;

        // Unknown + unasserted ⇒ both gated out ⇒ no viable path.
        Planner(Data()).Plan(new SkillTarget(Skill, 3), State(1), RecipeHistory.Empty,
            learnability: strict).Should().BeNull(
            because: "strict policy refuses to assume trainer/quest recipes are auto-learned");

        // Asserted ⇒ the escape hatch reinstates it.
        var asserted = Planner(Data()).Plan(new SkillTarget(Skill, 3), State(1), RecipeHistory.Empty,
            asserted: new AssertedUnlocks(["ForgeBlade"]), learnability: strict)!;
        asserted.Phases.Should().OnlyContain(p => p.RecipeInternalName == "ForgeBlade");

        // Already known ⇒ also fine.
        var known = Planner(Data()).Plan(new SkillTarget(Skill, 3), State(1),
            new RecipeHistory(new Dictionary<string, int> { ["CutCharmstone"] = 1 }),
            learnability: strict)!;
        known.Phases.Should().Contain(p => p.RecipeInternalName == "CutCharmstone");
    }

    [Fact]
    public void SourcelessSkillGatedRecipe_StrictPolicy_StillAutoLearned()
    {
        // No Training/Quest source ⇒ a genuine skill-up unlock; even strict
        // policy keeps the auto-learn pass for it.
        var data = new FakeRef().AddSkill(Skill).AddXpTable(100)
            .AddItem(1, "Ingot")
            .AddRecipe("SmeltIngot", Skill, xp: 50, skillLevelReq: 1, produces: (1, 1));

        var plan = Planner(data).Plan(new SkillTarget(Skill, 3), State(1), RecipeHistory.Empty,
            learnability: LearnabilityPolicy.RequireKnownForTrainerAndQuest)!;

        plan.Phases.Should().ContainSingle().Which.RecipeInternalName.Should().Be("SmeltIngot",
            because: "source-less skill-gated recipes are genuine auto-learns");
    }

    // ── Minimal IReferenceDataService fake ───────────────────────────────

    private sealed class FakeRef : IReferenceDataService
    {
        private readonly Dictionary<long, Item> _items = new();
        private readonly Dictionary<string, Item> _itemsByName = new(StringComparer.Ordinal);
        private readonly Dictionary<string, Recipe> _recipes = new(StringComparer.Ordinal);
        private readonly Dictionary<string, Recipe> _recipesByName = new(StringComparer.Ordinal);
        private readonly Dictionary<string, SkillEntry> _skills = new(StringComparer.Ordinal);
        private readonly Dictionary<string, XpTableEntry> _xp = new(StringComparer.Ordinal);
        private readonly Dictionary<string, IReadOnlyList<RecipeSource>> _recipeSources = new(StringComparer.Ordinal);
        private int _serial;

        public FakeRef AddSkill(string key, string xpTable = "T")
        {
            _skills[key] = new SkillEntry(key, key, 0, false, xpTable, 0, [],
                new Dictionary<string, SkillRewardEntry>());
            return this;
        }

        public FakeRef AddXpTable(long perLevel, string name = "T", int levels = 50)
        {
            _xp[name] = new XpTableEntry(name, Enumerable.Repeat(perLevel, levels).ToList());
            return this;
        }

        public FakeRef AddItem(long id, string internalName)
        {
            var it = new Item { Id = id, Name = internalName, InternalName = internalName };
            _items[id] = it;
            _itemsByName[internalName] = it;
            return this;
        }

        public FakeRef AddRecipe(
            string internalName, string rewardSkill, int xp,
            (long id, int stack) produces,
            int firstTime = 0, int skillLevelReq = 0, (long id, int stack)? ingredients = null,
            int? maxUses = null, IReadOnlyList<RecipeRequirement>? otherRequirements = null)
        {
            var r = new Recipe
            {
                Key = $"recipe_{++_serial}",
                Name = internalName,
                InternalName = internalName,
                Skill = rewardSkill,
                SkillLevelReq = skillLevelReq,
                RewardSkill = rewardSkill,
                RewardSkillXp = xp,
                RewardSkillXpFirstTime = firstTime,
                MaxUses = maxUses,
                OtherRequirements = otherRequirements,
                Ingredients = ingredients is { } ing
                    ? [new RecipeItemIngredient { ItemCode = ing.id, StackSize = ing.stack }]
                    : [],
                ResultItems = [new RecipeResultItem { ItemCode = produces.id, StackSize = produces.stack }],
            };
            _recipes[r.Key] = r;
            _recipesByName[internalName] = r;
            return this;
        }

        public FakeRef AddRecipeSource(string recipeInternalName, string type, string? npc = null)
        {
            if (_recipeSources.TryGetValue(recipeInternalName, out var existing))
                ((List<RecipeSource>)existing).Add(new RecipeSource(type, npc, null));
            else
                _recipeSources[recipeInternalName] = new List<RecipeSource> { new(type, npc, null) };
            return this;
        }

        public IReadOnlyList<string> Keys { get; } = ["skills", "xptables", "recipes", "items"];
        public IReadOnlyDictionary<long, Item> Items => _items;
        public IReadOnlyDictionary<string, Item> ItemsByInternalName => _itemsByName;
        public ItemKeywordIndex KeywordIndex => new(new Dictionary<long, Item>());
        public IReadOnlyDictionary<string, Recipe> Recipes => _recipes;
        public IReadOnlyDictionary<string, Recipe> RecipesByInternalName => _recipesByName;
        public IReadOnlyDictionary<string, SkillEntry> Skills => _skills;
        public IReadOnlyDictionary<string, XpTableEntry> XpTables => _xp;
        public IReadOnlyDictionary<string, NpcEntry> Npcs { get; } = new Dictionary<string, NpcEntry>();
        public IReadOnlyDictionary<string, AreaEntry> Areas { get; } = new Dictionary<string, AreaEntry>(StringComparer.Ordinal);
        public IReadOnlyDictionary<string, IReadOnlyList<ItemSource>> ItemSources { get; } = new Dictionary<string, IReadOnlyList<ItemSource>>();
        public IReadOnlyDictionary<string, IReadOnlyList<RecipeSource>> RecipeSources => _recipeSources;
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
