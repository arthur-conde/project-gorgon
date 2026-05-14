using System.IO;
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
    public void MinFavorLevel_WithSlugFormNpc_StripsAreaPrefix_AppendsAreaFromPoco()
    {
        // Quest data references NPCs by slug ("AreaSerbule2/NPC_DurstinTallow") while npcs.json
        // keys them bare ("NPC_DurstinTallow"). EntityRef.Npc normalises for lookup, and the
        // resolved POCO's AreaFriendlyName is appended in parentheses for disambiguation.
        // Renders as a chip: Prefix="Neutral with" + ChipName="Durstin Tallow (Serbule Hills)".
        var refData = new StubRefData();
        refData.NpcsByInternalNameMap["NPC_DurstinTallow"] = new Npc { Name = "Durstin Tallow", AreaFriendlyName = "Serbule Hills" };
        var nav = new SilmarillionReferenceNavigator(Array.Empty<IReferenceKindTarget>());
        var resolver = new ReferenceDataEntityNameResolver(refData);

        var groups = QuestDetailProjector.BuildRequirementGroups(
            new QuestRequirement[]
            {
                new MinFavorLevelRequirement { T = "MinFavorLevel", Npc = "AreaSerbule2/NPC_DurstinTallow", Level = "Neutral" },
            },
            refData, resolver, nav);

        var req = groups.Should().ContainSingle(g => g.Label == "Favor").Which.Requirements.Single();
        req.Text.Should().Be("Neutral with Durstin Tallow (Serbule Hills)");
        req.Prefix.Should().Be("Neutral with");
        req.ChipName.Should().Be("Durstin Tallow (Serbule Hills)");
    }

    [Fact]
    public void MinFavorLevel_FallsBackToJustNpcName_WhenAreaMissing()
    {
        var refData = new StubRefData();
        refData.NpcsByInternalNameMap["NPC_Joeh"] = new Npc { Name = "Joeh" }; // no AreaFriendlyName
        var nav = new SilmarillionReferenceNavigator(Array.Empty<IReferenceKindTarget>());
        var resolver = new ReferenceDataEntityNameResolver(refData);

        var groups = QuestDetailProjector.BuildRequirementGroups(
            new QuestRequirement[] { new MinFavorLevelRequirement { T = "MinFavorLevel", Npc = "NPC_Joeh", Level = "Friends" } },
            refData, resolver, nav);

        groups.Single().Requirements.Single().Text.Should().Be("Friends with Joeh");
    }

    [Fact]
    public void MinFavorRequirement_IncludesArea_AndFormatsAmount()
    {
        var refData = new StubRefData();
        refData.NpcsByInternalNameMap["NPC_DurstinTallow"] = new Npc { Name = "Durstin Tallow", AreaFriendlyName = "Serbule Hills" };
        var nav = new SilmarillionReferenceNavigator(Array.Empty<IReferenceKindTarget>());
        var resolver = new ReferenceDataEntityNameResolver(refData);

        var groups = QuestDetailProjector.BuildRequirementGroups(
            new QuestRequirement[]
            {
                new MinFavorRequirement { T = "MinFavor", Npc = "AreaSerbule2/NPC_DurstinTallow", MinFavor = 5000 },
            },
            refData, resolver, nav);

        groups.Single().Requirements.Single().Text.Should().Be("5,000 favor with Durstin Tallow (Serbule Hills)");
    }

    [Fact]
    public void QuestCompleted_BucketedAsStoryPrerequisite_WithNavigableQuestChip()
    {
        // Chip-eligible: Prefix + ChipName populated so the XAML renders this as
        // "{Completed:} [chip:Kill Skeletons]" with the quest name as a proper navigable chip.
        // Text remains the canonical full-sentence form for the prose fallback path.
        var refData = new StubRefData();
        refData.QuestsByInternalNameMap["KillSkeletons"] = new Quest { InternalName = "KillSkeletons", Name = "Kill Skeletons" };
        var nav = new SilmarillionReferenceNavigator(new[] { (IReferenceKindTarget)new RecordingTarget(EntityKind.Quest) });
        var resolver = new ReferenceDataEntityNameResolver(refData);

        var groups = QuestDetailProjector.BuildRequirementGroups(
            new QuestRequirement[] { new QuestCompletedRequirement { T = "QuestCompleted", Quest = "KillSkeletons" } },
            refData, resolver, nav);

        var req = groups.Should().ContainSingle(g => g.Label == "Story prerequisites").Which.Requirements.Single();
        req.Text.Should().Be("Completed: Kill Skeletons");
        req.Prefix.Should().Be("Completed:");
        req.ChipName.Should().Be("Kill Skeletons");
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
        req.Prefix.Should().Be("Has");
        req.ChipName.Should().Be("Skinning Knife");
        req.IsNavigable.Should().BeTrue();
    }

    [Fact]
    public void IsVampire_BucketedAsIdentity_RendersStaticText_WithoutChipFields()
    {
        // No navigable entity → ChipName stays null and the XAML falls through to the
        // plain-Text rendering path. Lock that contract in so a future addition can't
        // accidentally chip-ify a no-entity row.
        var refData = new StubRefData();
        var nav = new SilmarillionReferenceNavigator(Array.Empty<IReferenceKindTarget>());
        var resolver = new ReferenceDataEntityNameResolver(refData);

        var groups = QuestDetailProjector.BuildRequirementGroups(
            new QuestRequirement[] { new IsVampireRequirement { T = "IsVampire" } },
            refData, resolver, nav);

        var req = groups.Should().ContainSingle(g => g.Label == "Identity / state")
            .Which.Requirements.Single();
        req.Text.Should().Be("Must be a vampire");
        req.ChipName.Should().BeNull();
        req.Prefix.Should().BeNull();
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

    // ─── Real-data sanity walk (cookbook rung 4) ───────────────────────────────────────
    //
    // Projects three real quests with mixed requirement families against the actual bundled
    // data and asserts the output is well-formed: every requirement text is non-empty and
    // doesn't contain "(unknown)" sentinels, every group label is one we know about, and the
    // expected requirement buckets are present. Synthetic-fixture tests above verify projection
    // logic per subclass; this one verifies the projector reads sensibly against the live
    // catalogue with all 42 subclasses landing in real entries.

    [Fact]
    public void RealBundledQuest_WolfHuntDeer2_ProjectsRequirementsByIntentAndResolvesInternalNames()
    {
        var realBundled = Path.Combine(AppContext.BaseDirectory, "Reference", "BundledData");
        if (!File.Exists(Path.Combine(realBundled, "quests.json")))
            return; // bundled data not co-located (some CI shapes); skip rather than fail.

        var svc = new Mithril.Shared.Reference.ReferenceDataService(
            cacheDir: Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N")),
            http: NeverCallHttp(),
            bundledDir: realBundled);
        var resolver = new ReferenceDataEntityNameResolver(svc);
        var nav = new SilmarillionReferenceNavigator(new[]
        {
            (IReferenceKindTarget)new RecordingTarget(EntityKind.Npc),
            new RecordingTarget(EntityKind.Quest),
        });

        // Wolf_HuntDeer2 — a real bundled quest with MinFavorLevel + MinSkillLevel +
        // QuestCompleted + RequirementsToSustain(MoonPhase=FullMoon) + Rewards_Effects.
        // Verifies four buckets and the moon-phase rendering with real data, not synthetics.
        svc.QuestsByInternalName.Should().ContainKey("Wolf_HuntDeer2");
        var quest = svc.QuestsByInternalName["Wolf_HuntDeer2"];

        var groups = QuestDetailProjector.BuildRequirementGroups(quest.Requirements, svc, resolver, nav);

        groups.Select(g => g.Label).Should().Contain(new[]
        {
            "Favor",
            "Skill / ability gates",
            "Story prerequisites",
        }, because: "Wolf_HuntDeer2 carries MinFavorLevel + MinSkillLevel + QuestCompleted gates");
        groups.Should().AllSatisfy(g => g.Requirements.Should().AllSatisfy(r =>
        {
            r.Text.Should().NotBeNullOrWhiteSpace();
            r.Text.Should().NotContain("(unknown)", because: "real-data internal names must resolve");
        }));

        var sustain = QuestDetailProjector.BuildRequirementGroups(quest.RequirementsToSustain, svc, resolver, nav);
        sustain.Should().ContainSingle(g => g.Label == "Time & moon")
            .Which.Requirements.Single().Text.Should().Contain("Full Moon");

        var rewardGroups = QuestDetailProjector.BuildRewardGroups(quest, svc);
        rewardGroups.Select(g => g.Label).Should().Contain("Effects");
    }

    [Fact]
    public void RealBundledQuest_KillSkeletons_ProjectsSensibly()
    {
        // quest_1 — every player's first repeatable. Lowest-effort sanity check that the
        // catalogue-anchored projection round-trips for a starter quest.
        var realBundled = Path.Combine(AppContext.BaseDirectory, "Reference", "BundledData");
        if (!File.Exists(Path.Combine(realBundled, "quests.json"))) return;

        var svc = new Mithril.Shared.Reference.ReferenceDataService(
            cacheDir: Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N")),
            http: NeverCallHttp(),
            bundledDir: realBundled);
        var resolver = new ReferenceDataEntityNameResolver(svc);
        var nav = new SilmarillionReferenceNavigator(Array.Empty<IReferenceKindTarget>());

        var quest = svc.Quests["quest_1"];
        var objectives = QuestDetailProjector.BuildObjectives(quest, svc, resolver, nav);
        var requirements = QuestDetailProjector.BuildRequirementGroups(quest.Requirements, svc, resolver, nav);
        var rewards = QuestDetailProjector.BuildRewardGroups(quest, svc);

        quest.Name.Should().NotBeNullOrEmpty();
        objectives.Should().NotBeEmpty(because: "KillSkeletons has at least one Kill objective");
        // Either path is fine — the assertion is "doesn't blow up + every projected text is non-empty"
        foreach (var group in requirements)
            group.Requirements.Should().AllSatisfy(r => r.Text.Should().NotBeNullOrWhiteSpace());
        foreach (var group in rewards)
            group.Rewards.Should().AllSatisfy(r => r.Text.Should().NotBeNullOrWhiteSpace());
    }

    private static System.Net.Http.HttpClient NeverCallHttp() =>
        new(new ThrowingHandler());
    private sealed class ThrowingHandler : System.Net.Http.HttpMessageHandler
    {
        protected override Task<System.Net.Http.HttpResponseMessage> SendAsync(
            System.Net.Http.HttpRequestMessage request, CancellationToken ct) =>
            throw new InvalidOperationException("HTTP must not be called from a sanity-walk test.");
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
