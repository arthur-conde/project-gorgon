using FluentAssertions;
using Mithril.Reference.Models.Items;
using Mithril.Reference.Models.Npcs;
using Mithril.Reference.Models.Quests;
using Mithril.Reference.Models.Recipes;
using Mithril.Shared.Reference;
using Silmarillion.Navigation;
using Silmarillion.ViewModels;
using Xunit;

namespace Silmarillion.Tests.ViewModels;

/// <summary>
/// Covers the intent-bucketing + display-name resolution that <see cref="QuestDetailProjector"/>
/// does for the 42 <c>QuestRequirement</c> subclasses. Synthetic fixtures here verify projection
/// shape; the cookbook rung-4 real-data sanity walk verifies the result reads legibly in-app.
/// </summary>
public sealed class QuestDetailProjectorTests
{
    [Fact]
    public void MinSkillLevel_BucketedAsSkillGate_ResolvesSkillDisplayName()
    {
        var refData = new StubRefData();
        refData.Skills["Cooking"] = new SkillEntry("Cooking", "Cooking", 1, false, "TypicalNoncombatSkill", 0, [],
            new Dictionary<string, SkillRewardEntry>(StringComparer.Ordinal));
        var nav = new SilmarillionReferenceNavigator(Array.Empty<IReferenceKindTarget>());
        var resolver = new ReferenceDataEntityNameResolver(refData);

        var groups = QuestDetailProjector.BuildRequirementGroups(
            new QuestRequirement[] { new MinSkillLevelRequirement { T = "MinSkillLevel", Skill = "Cooking", Level = "30" } },
            refData, resolver, nav);

        groups.Should().ContainSingle(g => g.Label == "Skill / ability gates");
        groups.Single().Requirements.Single().Text.Should().Be("Cooking 30");
    }

    [Fact]
    public void MinFavorLevel_BucketedAsFavor_ResolvesNpcDisplayName_AndExposesNavigableChip()
    {
        var refData = new StubRefData();
        refData.NpcsByInternalNameMap["NPC_Joeh"] = new Npc { Name = "Joeh" };
        var nav = new SilmarillionReferenceNavigator(new[] { (IReferenceKindTarget)new RecordingTarget(EntityKind.Npc) });
        var resolver = new ReferenceDataEntityNameResolver(refData);

        var groups = QuestDetailProjector.BuildRequirementGroups(
            new QuestRequirement[] { new MinFavorLevelRequirement { T = "MinFavorLevel", Npc = "NPC_Joeh", Level = "Friends" } },
            refData, resolver, nav);

        var req = groups.Should().ContainSingle(g => g.Label == "Favor").Which.Requirements.Single();
        req.Text.Should().Be("Friends with Joeh");
        req.Reference.Should().NotBeNull();
        req.Reference!.Kind.Should().Be(EntityKind.Npc);
        req.IsNavigable.Should().BeTrue("the Npc kind target is registered");
    }

    [Fact]
    public void QuestCompleted_BucketedAsStoryPrerequisite_WithNavigableQuestChip()
    {
        var refData = new StubRefData();
        refData.QuestsByInternalNameMap["KillSkeletons"] = new Quest { InternalName = "KillSkeletons", Name = "Kill Skeletons" };
        var nav = new SilmarillionReferenceNavigator(new[] { (IReferenceKindTarget)new RecordingTarget(EntityKind.Quest) });
        var resolver = new ReferenceDataEntityNameResolver(refData);

        var groups = QuestDetailProjector.BuildRequirementGroups(
            new QuestRequirement[] { new QuestCompletedRequirement { T = "QuestCompleted", Quest = "KillSkeletons" } },
            refData, resolver, nav);

        var req = groups.Should().ContainSingle(g => g.Label == "Story prerequisites").Which.Requirements.Single();
        req.Text.Should().Be("Completed: Kill Skeletons");
        req.Reference.Should().NotBeNull();
        req.Reference!.Kind.Should().Be(EntityKind.Quest);
        req.IsNavigable.Should().BeTrue();
    }

    [Fact]
    public void InventoryItem_BucketedAsInventory_ResolvesItemDisplayName()
    {
        var refData = new StubRefData();
        refData.ItemsByInternalNameMap["SkinningKnife"] = new Item { Id = 1, InternalName = "SkinningKnife", Name = "Skinning Knife" };
        var nav = new SilmarillionReferenceNavigator(new[] { (IReferenceKindTarget)new RecordingTarget(EntityKind.Item) });
        var resolver = new ReferenceDataEntityNameResolver(refData);

        var groups = QuestDetailProjector.BuildRequirementGroups(
            new QuestRequirement[] { new InventoryItemRequirement { T = "InventoryItem", Item = "SkinningKnife" } },
            refData, resolver, nav);

        var req = groups.Should().ContainSingle(g => g.Label == "Inventory & equipment").Which.Requirements.Single();
        req.Text.Should().Be("Has Skinning Knife");
        req.IsNavigable.Should().BeTrue();
    }

    [Fact]
    public void IsVampire_BucketedAsIdentity_RendersStaticText()
    {
        var refData = new StubRefData();
        var nav = new SilmarillionReferenceNavigator(Array.Empty<IReferenceKindTarget>());
        var resolver = new ReferenceDataEntityNameResolver(refData);

        var groups = QuestDetailProjector.BuildRequirementGroups(
            new QuestRequirement[] { new IsVampireRequirement { T = "IsVampire" } },
            refData, resolver, nav);

        groups.Should().ContainSingle(g => g.Label == "Identity / state")
            .Which.Requirements.Single().Text.Should().Be("Must be a vampire");
    }

    [Fact]
    public void TimeOfDay_BucketedAsTimeAndMoon_FormatsRange()
    {
        var refData = new StubRefData();
        var nav = new SilmarillionReferenceNavigator(Array.Empty<IReferenceKindTarget>());
        var resolver = new ReferenceDataEntityNameResolver(refData);

        var groups = QuestDetailProjector.BuildRequirementGroups(
            new QuestRequirement[] { new TimeOfDayRequirement { T = "TimeOfDay", MinHour = 18, MaxHour = 23 } },
            refData, resolver, nav);

        groups.Should().ContainSingle(g => g.Label == "Time & moon")
            .Which.Requirements.Single().Text.Should().Be("Between 18:00 and 23:00 in-game time");
    }

    [Fact]
    public void AreaEvent_BucketedAsLocationAndArea()
    {
        var refData = new StubRefData();
        var nav = new SilmarillionReferenceNavigator(Array.Empty<IReferenceKindTarget>());
        var resolver = new ReferenceDataEntityNameResolver(refData);

        var groups = QuestDetailProjector.BuildRequirementGroups(
            new QuestRequirement[]
            {
                new AreaEventOnRequirement { T = "AreaEventOn", AreaEvent = "GoblinRaid" },
                new InHotspotRequirement { T = "InHotspot", Name = "Crypt" },
            },
            refData, resolver, nav);

        groups.Should().ContainSingle(g => g.Label == "Location & area")
            .Which.Requirements.Should().HaveCount(2);
    }

    [Fact]
    public void InCombatWithElite_BucketedAsCombatState()
    {
        var refData = new StubRefData();
        var nav = new SilmarillionReferenceNavigator(Array.Empty<IReferenceKindTarget>());
        var resolver = new ReferenceDataEntityNameResolver(refData);

        var groups = QuestDetailProjector.BuildRequirementGroups(
            new QuestRequirement[] { new InCombatWithEliteRequirement { T = "InCombatWithElite", MinLevel = 50 } },
            refData, resolver, nav);

        groups.Should().ContainSingle(g => g.Label == "Combat state")
            .Which.Requirements.Single().Text.Should().Be("In combat with elite (level ≥ 50)");
    }

    [Fact]
    public void OrRequirement_BucketedAsComposite_ChildrenJoinedInline()
    {
        var refData = new StubRefData();
        refData.Skills["Cooking"] = new SkillEntry("Cooking", "Cooking", 1, false, "TypicalNoncombatSkill", 0, [],
            new Dictionary<string, SkillRewardEntry>(StringComparer.Ordinal));
        var nav = new SilmarillionReferenceNavigator(Array.Empty<IReferenceKindTarget>());
        var resolver = new ReferenceDataEntityNameResolver(refData);

        var groups = QuestDetailProjector.BuildRequirementGroups(
            new QuestRequirement[]
            {
                new OrRequirement
                {
                    T = "Or",
                    List = new QuestRequirement[]
                    {
                        new MinSkillLevelRequirement { T = "MinSkillLevel", Skill = "Cooking", Level = "30" },
                        new IsVampireRequirement { T = "IsVampire" },
                    },
                },
            },
            refData, resolver, nav);

        groups.Should().ContainSingle(g => g.Label == "Any of")
            .Which.Requirements.Single().Text.Should().Be("Cooking 30 • Must be a vampire");
    }

    [Fact]
    public void UnknownDiscriminator_BucketedAsUnrecognised_RendersDriftWarning()
    {
        var refData = new StubRefData();
        var nav = new SilmarillionReferenceNavigator(Array.Empty<IReferenceKindTarget>());
        var resolver = new ReferenceDataEntityNameResolver(refData);

        var groups = QuestDetailProjector.BuildRequirementGroups(
            new QuestRequirement[] { new UnknownQuestRequirement { T = "FutureGate", DiscriminatorValue = "FutureGate" } },
            refData, resolver, nav);

        groups.Should().ContainSingle(g => g.Label == "Unrecognised (data drift)")
            .Which.Requirements.Single().Text.Should().Be("Unrecognised: FutureGate");
    }

    [Fact]
    public void InteractionFlagSet_BucketedAsInternalFlags_AndOrderedLast()
    {
        // Multiple buckets present — verify the player-relevance ordering puts internal flags after
        // story / inventory / etc.
        var refData = new StubRefData();
        refData.NpcsByInternalNameMap["NPC_Joeh"] = new Npc { Name = "Joeh" };
        var nav = new SilmarillionReferenceNavigator(Array.Empty<IReferenceKindTarget>());
        var resolver = new ReferenceDataEntityNameResolver(refData);

        var groups = QuestDetailProjector.BuildRequirementGroups(
            new QuestRequirement[]
            {
                new InteractionFlagSetRequirement { T = "InteractionFlagSet", InteractionFlag = "TalkedToJoeh" },
                new MinFavorLevelRequirement { T = "MinFavorLevel", Npc = "NPC_Joeh", Level = "Friends" },
            },
            refData, resolver, nav);

        groups.Should().HaveCount(2);
        groups[0].Label.Should().Be("Favor");
        groups[1].Label.Should().Be("Internal flags");
    }

    // ─── Rewards ───────────────────────────────────────────────────────────────────────

    [Fact]
    public void SkillXpReward_BucketedAsExperience_ResolvesSkillDisplayName()
    {
        var refData = new StubRefData();
        refData.Skills["Sword"] = new SkillEntry("Sword", "Sword", 5, true, "TypicalCombatSkill", 0, [],
            new Dictionary<string, SkillRewardEntry>(StringComparer.Ordinal));
        var quest = new Quest
        {
            Rewards = new QuestReward[] { new SkillXpReward { T = "SkillXp", Skill = "Sword", Xp = 1500 } },
        };

        var groups = QuestDetailProjector.BuildRewardGroups(quest, refData);

        groups.Should().ContainSingle(g => g.Label == "Experience")
            .Which.Rewards.Single().Text.Should().Be("Sword: 1,500 XP");
    }

    [Fact]
    public void CurrencyReward_BucketedAsCurrency_FormatsAmount()
    {
        var refData = new StubRefData();
        var quest = new Quest
        {
            Rewards = new QuestReward[] { new CurrencyReward { T = "Currency", Currency = "Councils", Amount = 1500 } },
        };

        var groups = QuestDetailProjector.BuildRewardGroups(quest, refData);

        groups.Should().ContainSingle(g => g.Label == "Currency")
            .Which.Rewards.Single().Text.Should().Be("1,500 Councils");
    }

    [Fact]
    public void GuildCreditsReward_BucketedAsCurrency()
    {
        var refData = new StubRefData();
        var quest = new Quest
        {
            Rewards = new QuestReward[] { new GuildCreditsReward { T = "GuildCredits", Credits = 100 } },
        };

        var groups = QuestDetailProjector.BuildRewardGroups(quest, refData);

        groups.Should().ContainSingle(g => g.Label == "Currency")
            .Which.Rewards.Single().Text.Should().Be("100 guild credits");
    }

    [Fact]
    public void RewardsEffects_BucketedAsEffects()
    {
        var refData = new StubRefData();
        var quest = new Quest { Rewards_Effects = new[] { "PsychologicalDamage" } };

        var groups = QuestDetailProjector.BuildRewardGroups(quest, refData);

        groups.Should().ContainSingle(g => g.Label == "Effects")
            .Which.Rewards.Single().Text.Should().Be("Psychological Damage",
                because: "effect internal names are camel-case-split for legibility");
    }

    [Fact]
    public void EmptyRequirements_ReturnsEmpty()
    {
        var refData = new StubRefData();
        var nav = new SilmarillionReferenceNavigator(Array.Empty<IReferenceKindTarget>());
        var resolver = new ReferenceDataEntityNameResolver(refData);

        QuestDetailProjector.BuildRequirementGroups(null, refData, resolver, nav).Should().BeEmpty();
        QuestDetailProjector.BuildRequirementGroups(Array.Empty<QuestRequirement>(), refData, resolver, nav).Should().BeEmpty();
    }

    // ─── Objectives ────────────────────────────────────────────────────────────────────

    [Fact]
    public void BuildObjectives_NumbersFromOne_PreservesDescription_BucketsNestedRequirements()
    {
        var refData = new StubRefData();
        refData.Skills["Sword"] = new SkillEntry("Sword", "Sword", 5, true, "TypicalCombatSkill", 0, [],
            new Dictionary<string, SkillRewardEntry>(StringComparer.Ordinal));
        var nav = new SilmarillionReferenceNavigator(Array.Empty<IReferenceKindTarget>());
        var resolver = new ReferenceDataEntityNameResolver(refData);

        var quest = new Quest
        {
            Objectives = new[]
            {
                new QuestObjective
                {
                    Type = "Kill",
                    Description = "Kill 5 wolves",
                    Number = 5,
                    Requirements = new QuestRequirement[]
                    {
                        new MinSkillLevelRequirement { T = "MinSkillLevel", Skill = "Sword", Level = "20" },
                    },
                },
                new QuestObjective { Type = "Scripted", Description = "Talk to Joeh", Number = 1 },
            },
        };

        var rows = QuestDetailProjector.BuildObjectives(quest, refData, resolver, nav);

        rows.Should().HaveCount(2);
        rows[0].Index.Should().Be(1);
        rows[0].Description.Should().Be("Kill 5 wolves");
        rows[0].Number.Should().Be(5);
        rows[0].NestedRequirements.Should().ContainSingle(g => g.Label == "Skill / ability gates");
        rows[1].Index.Should().Be(2);
        rows[1].Number.Should().BeNull(because: "Number==1 doesn't merit a 'repeat' chip");
    }

    private sealed class StubRefData : IReferenceDataService
    {
        public Dictionary<string, Item> ItemsByInternalNameMap { get; } = new(StringComparer.Ordinal);
        public Dictionary<string, Recipe> RecipesByInternalNameMap { get; } = new(StringComparer.Ordinal);
        public Dictionary<string, Npc> NpcsByInternalNameMap { get; } = new(StringComparer.Ordinal);
        public Dictionary<string, Quest> QuestsByInternalNameMap { get; } = new(StringComparer.Ordinal);
        public Dictionary<string, SkillEntry> Skills { get; } = new(StringComparer.Ordinal);

        public IReadOnlyList<string> Keys { get; } = [];
        public IReadOnlyDictionary<long, Item> Items { get; } = new Dictionary<long, Item>();
        public IReadOnlyDictionary<string, Item> ItemsByInternalName => ItemsByInternalNameMap;
        public ItemKeywordIndex KeywordIndex => new(new Dictionary<long, Item>());
        public IReadOnlyDictionary<string, Recipe> Recipes { get; } = new Dictionary<string, Recipe>();
        public IReadOnlyDictionary<string, Recipe> RecipesByInternalName => RecipesByInternalNameMap;
        IReadOnlyDictionary<string, SkillEntry> IReferenceDataService.Skills => Skills;
        public IReadOnlyDictionary<string, XpTableEntry> XpTables { get; } = new Dictionary<string, XpTableEntry>();
        public IReadOnlyDictionary<string, NpcEntry> Npcs { get; } = new Dictionary<string, NpcEntry>();
        public IReadOnlyDictionary<string, Npc> NpcsByInternalName => NpcsByInternalNameMap;
        public IReadOnlyDictionary<string, AreaEntry> Areas { get; } = new Dictionary<string, AreaEntry>();
        public IReadOnlyDictionary<string, IReadOnlyList<ItemSource>> ItemSources { get; } = new Dictionary<string, IReadOnlyList<ItemSource>>();
        public IReadOnlyDictionary<string, AttributeEntry> Attributes { get; } = new Dictionary<string, AttributeEntry>();
        public IReadOnlyDictionary<string, PowerEntry> Powers { get; } = new Dictionary<string, PowerEntry>();
        public IReadOnlyDictionary<string, IReadOnlyList<string>> Profiles { get; } = new Dictionary<string, IReadOnlyList<string>>();
        public IReadOnlyDictionary<string, Quest> Quests { get; } = new Dictionary<string, Quest>();
        public IReadOnlyDictionary<string, Quest> QuestsByInternalName => QuestsByInternalNameMap;
        public IReadOnlyDictionary<string, string> Strings { get; } = new Dictionary<string, string>();

        public ReferenceFileSnapshot GetSnapshot(string key) => new(key, ReferenceFileSource.Bundled, "test", null, 0);
        public Task RefreshAsync(string key, CancellationToken ct = default) => Task.CompletedTask;
        public Task RefreshAllAsync(CancellationToken ct = default) => Task.CompletedTask;
        public void BeginBackgroundRefresh() { }
        public event EventHandler<string>? FileUpdated { add { } remove { } }
    }

    private sealed class RecordingTarget : IReferenceKindTarget
    {
        public RecordingTarget(EntityKind kind) => Kind = kind;
        public EntityKind Kind { get; }
        public int TabIndex => 0;
        public bool TrySelectByInternalName(string internalName) => true;
        public bool TryOpenInWindow() => false;
    }
}
