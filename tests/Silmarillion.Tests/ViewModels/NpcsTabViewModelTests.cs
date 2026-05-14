using FluentAssertions;
using Mithril.Reference.Models.Items;
using Mithril.Reference.Models.Npcs;
using Mithril.Reference.Models.Recipes;
using Mithril.Shared.Reference;
using Mithril.Shared.Wpf.Query;
using Silmarillion.Navigation;
using Silmarillion.ViewModels;
using Xunit;
// The POCO NpcService / NpcPreference clash with the slim Mithril.Shared.Reference projections
// of the same names — alias to keep both reachable.
using NpcServicePoco = Mithril.Reference.Models.Npcs.NpcService;
using NpcPreferencePoco = Mithril.Reference.Models.Npcs.NpcPreference;
using NpcStoreServicePoco = Mithril.Reference.Models.Npcs.StoreService;
using NpcTrainingServicePoco = Mithril.Reference.Models.Npcs.TrainingService;

namespace Silmarillion.Tests.ViewModels;

// Helper copied from RecipesTabViewModelTests — duplicate-free because file-scoped.
file static class NavFactory
{
    public static SilmarillionReferenceNavigator WithKinds(params EntityKind[] kinds) =>
        new(kinds.Select(k => (IReferenceKindTarget)new StubKindTarget(k)));

    private sealed class StubKindTarget : IReferenceKindTarget
    {
        public StubKindTarget(EntityKind kind) => Kind = kind;
        public EntityKind Kind { get; }
        public int TabIndex => 0;
        public bool TrySelectByInternalName(string internalName) => true;
        public bool TryOpenInWindow() => false;
    }
}

public sealed class NpcsTabViewModelTests
{
    [Fact]
    public void AllNpcs_PopulatedFromReferenceData_OrderedByName()
    {
        var refData = new StubReferenceData
        {
            NpcsByKey =
            {
                ["NPC_Joeh"] = new Npc { Name = "Joeh", AreaFriendlyName = "Serbule" },
                ["NPC_Albert"] = new Npc { Name = "Albert", AreaFriendlyName = "Eltibule" },
            },
        };

        var vm = new NpcsTabViewModel(refData, new SilmarillionReferenceNavigator(Array.Empty<IReferenceKindTarget>()));

        vm.AllNpcs.Should().HaveCount(2);
        vm.AllNpcs.Select(n => n.Name).Should().Equal("Albert", "Joeh");
        vm.AllNpcs[0].InternalName.Should().Be("NPC_Albert");
    }

    [Fact]
    public void NpcListRow_AreaDisplayName_FallsBackThroughFriendlyThenRaw_ThenUnknown()
    {
        var refData = new StubReferenceData
        {
            NpcsByKey =
            {
                ["NPC_Friendly"] = new Npc { Name = "Friendly", AreaFriendlyName = "Friendly Area" },
                ["NPC_Raw"] = new Npc { Name = "Raw", AreaName = "AreaRaw" },
                ["NPC_None"] = new Npc { Name = "None" },
            },
        };
        var vm = new NpcsTabViewModel(refData, new SilmarillionReferenceNavigator(Array.Empty<IReferenceKindTarget>()));

        vm.AllNpcs.Single(r => r.InternalName == "NPC_Friendly").AreaDisplayName.Should().Be("Friendly Area");
        vm.AllNpcs.Single(r => r.InternalName == "NPC_Raw").AreaDisplayName.Should().Be("AreaRaw");
        vm.AllNpcs.Single(r => r.InternalName == "NPC_None").AreaDisplayName.Should().Be("(unknown)");
    }

    [Fact]
    public void NpcListRow_ServiceTypes_FlattenedDistinct_ForCollectionContainsQuery()
    {
        var refData = new StubReferenceData
        {
            NpcsByKey =
            {
                ["NPC_Multi"] = new Npc
                {
                    Name = "Multi-Service",
                    Services = new NpcServicePoco[]
                    {
                        new StoreService { Type = "Store" },
                        new TrainingService { Type = "Training" },
                        new StoreService { Type = "Store" }, // duplicate, collapses
                    },
                },
            },
        };
        var vm = new NpcsTabViewModel(refData, new SilmarillionReferenceNavigator(Array.Empty<IReferenceKindTarget>()));

        var row = vm.AllNpcs.Single();
        row.ServiceTypes.Select(s => s.Type).Should().BeEquivalentTo(["Store", "Training"]);
    }

    [Fact]
    public void QueryText_ServiceTypesContains_FiltersToMatchingNpcOnly()
    {
        var refData = new StubReferenceData
        {
            NpcsByKey =
            {
                ["NPC_Vendor"] = new Npc
                {
                    Name = "Vendor",
                    Services = new NpcServicePoco[] { new StoreService { Type = "Store" } },
                },
                ["NPC_Trainer"] = new Npc
                {
                    Name = "Trainer",
                    Services = new NpcServicePoco[] { new TrainingService { Type = "Training" } },
                },
            },
        };
        var vm = new NpcsTabViewModel(refData, new SilmarillionReferenceNavigator(Array.Empty<IReferenceKindTarget>()));

        const string queryString = "ServiceTypes CONTAINS \"Store\"";
        var columns = ColumnBindingHelper.BuildFromProperties(typeof(NpcListRow));
        var predicate = QueryCompiler.Compile(queryString, columns);
        predicate.Should().NotBeNull();

        var matches = vm.AllNpcs.Where(row => predicate!(row)).ToList();

        matches.Should().ContainSingle(r => r.InternalName == "NPC_Vendor");
        matches.Should().NotContain(r => r.InternalName == "NPC_Trainer");
    }

    [Fact]
    public void SelectingRow_BuildsDetailViewModel()
    {
        var npc = new Npc { Name = "Joeh", AreaFriendlyName = "Serbule" };
        var refData = new StubReferenceData
        {
            NpcsByKey = { ["NPC_Joeh"] = npc },
        };
        var vm = new NpcsTabViewModel(refData, new SilmarillionReferenceNavigator(Array.Empty<IReferenceKindTarget>()));

        vm.SelectedRow = vm.AllNpcs.Single();

        vm.DetailViewModel.Should().NotBeNull();
        vm.DetailViewModel!.Npc.Should().Be(npc);
        vm.DetailViewModel.InternalName.Should().Be("NPC_Joeh");
    }

    [Fact]
    public void DeselectingRow_ClearsDetailViewModel()
    {
        var refData = new StubReferenceData
        {
            NpcsByKey = { ["NPC_Joeh"] = new Npc { Name = "Joeh" } },
        };
        var vm = new NpcsTabViewModel(refData, new SilmarillionReferenceNavigator(Array.Empty<IReferenceKindTarget>()));

        vm.SelectedRow = vm.AllNpcs.Single();
        vm.SelectedRow = null;

        vm.DetailViewModel.Should().BeNull();
    }

    [Fact]
    public void DetailViewModel_TaughtRecipes_ProjectsRecipesTaughtByNpc_ToChips_Navigable_WhenRecipeKindRegistered()
    {
        var recipe = new Recipe
        {
            Key = "recipe_1",
            InternalName = "BakeBread",
            Name = "Bake Bread",
            IconId = 7,
        };
        var refData = new StubReferenceData
        {
            NpcsByKey = { ["NPC_Joeh"] = new Npc { Name = "Joeh" } },
            RecipesTaughtByNpcMap = { ["NPC_Joeh"] = new[] { recipe } },
        };
        var vm = new NpcsTabViewModel(refData, NavFactory.WithKinds(EntityKind.Recipe));

        vm.SelectedRow = vm.AllNpcs.Single();

        vm.DetailViewModel!.TaughtRecipes.Should().HaveCount(1);
        var chip = vm.DetailViewModel.TaughtRecipes[0];
        chip.DisplayName.Should().Be("Bake Bread");
        chip.IconId.Should().Be(7);
        chip.Reference.Should().Be(EntityRef.Recipe("BakeBread"));
        chip.IsNavigable.Should().BeTrue();
    }

    [Fact]
    public void DetailViewModel_SoldItems_ProjectsItemsSoldByNpc_ToChips_Navigable_WhenItemKindRegistered()
    {
        var apple = new Item { Id = 1, InternalName = "Apple", Name = "Apple", IconId = 3 };
        var refData = new StubReferenceData
        {
            NpcsByKey = { ["NPC_Joeh"] = new Npc { Name = "Joeh" } },
            ItemsSoldByNpcMap = { ["NPC_Joeh"] = new[] { apple } },
        };
        var vm = new NpcsTabViewModel(refData, NavFactory.WithKinds(EntityKind.Item));

        vm.SelectedRow = vm.AllNpcs.Single();

        vm.DetailViewModel!.SoldItems.Should().HaveCount(1);
        var chip = vm.DetailViewModel.SoldItems[0];
        chip.DisplayName.Should().Be("Apple");
        chip.IconId.Should().Be(3);
        chip.Reference.Should().Be(EntityRef.Item("Apple"));
        chip.IsNavigable.Should().BeTrue();
    }

    [Fact]
    public void DetailViewModel_EmptyNpc_ProducesEmptyCrossLinkSections_HeaderAndFooterOnly()
    {
        // Altars / pedestals carry no services, preferences, or gifts. The detail VM should
        // render every section as empty/null so the section-hide convention collapses them
        // and only the header (name/area/pos) + footer (internal name) remain.
        var refData = new StubReferenceData
        {
            NpcsByKey =
            {
                ["Altar_Druid"] = new Npc { Name = "Druid Altar", AreaName = "AreaSerbule" },
            },
        };
        var vm = new NpcsTabViewModel(refData, new SilmarillionReferenceNavigator(Array.Empty<IReferenceKindTarget>()));

        vm.SelectedRow = vm.AllNpcs.Single();

        var detail = vm.DetailViewModel!;
        detail.Services.Should().BeEmpty();
        detail.TaughtRecipes.Should().BeEmpty();
        detail.SoldItems.Should().BeEmpty();
        detail.Quests.Should().BeEmpty();
        detail.Preferences.Should().BeEmpty();
        detail.GiftSentimentTiers.Should().BeEmpty();
        detail.Description.Should().BeNull();
    }

    [Fact]
    public void DetailViewModel_Preferences_SortedByDesire_LoveLikeDislikeHate_ThenByPrefDescending()
    {
        var npc = new Npc
        {
            Name = "Joeh",
            Preferences = new[]
            {
                new NpcPreferencePoco { Desire = "Hate", Name = "Mushrooms", Pref = -2.0, Keywords = ["Mushroom"] },
                new NpcPreferencePoco { Desire = "Love", Name = "Magic Clubs", Pref = 3.5, Keywords = ["Club"] },
                new NpcPreferencePoco { Desire = "Like", Name = "Fairy Wings", Pref = 1.5, Keywords = ["FairyWing"] },
                new NpcPreferencePoco { Desire = "Love", Name = "Crystals", Pref = 4.0, Keywords = ["Crystal"] },
            },
        };
        var refData = new StubReferenceData
        {
            NpcsByKey = { ["NPC_Joeh"] = npc },
        };
        var vm = new NpcsTabViewModel(refData, new SilmarillionReferenceNavigator(Array.Empty<IReferenceKindTarget>()));

        vm.SelectedRow = vm.AllNpcs.Single();

        var prefs = vm.DetailViewModel!.Preferences;
        prefs.Select(p => p.DisplayName).Should().Equal(
            "Crystals",     // Love + 4.0 (highest Pref among Loves)
            "Magic Clubs",  // Love + 3.5
            "Fairy Wings",  // Like + 1.5
            "Mushrooms");   // Hate
    }

    [Fact]
    public void DetailViewModel_ServiceRow_MinFavorTier_DespisedIsHidden_AsTheDefaultAccessTier()
    {
        // "Despised" is the lowest favor tier — i.e. anyone can access the service. Showing
        // a "Favor: Despised" badge on every row is pure visual noise, so the VM nulls it out
        // and the XAML hides the chip. Non-default tiers (Neutral, Friends, …) still surface.
        var npc = new Npc
        {
            Name = "Joeh",
            Services = new NpcServicePoco[]
            {
                new NpcStoreServicePoco { Type = "Store", Favor = "Despised" },
                new NpcTrainingServicePoco { Type = "Training", Favor = "Neutral", Skills = ["Unarmed"] },
            },
        };
        var refData = new StubReferenceData
        {
            NpcsByKey = { ["NPC_Joeh"] = npc },
        };
        var vm = new NpcsTabViewModel(refData, new SilmarillionReferenceNavigator(Array.Empty<IReferenceKindTarget>()));

        vm.SelectedRow = vm.AllNpcs.Single();

        var rows = vm.DetailViewModel!.Services;
        rows[0].Type.Should().Be("Store");
        rows[0].MinFavorTier.Should().BeNull(because: "Despised is the default access tier; show no chip");
        rows[1].Type.Should().Be("Training");
        rows[1].MinFavorTier.Should().Be("Neutral");
    }

    [Fact]
    public void DetailViewModel_TrainingService_LabelsSkillsAndUnlocks_AsSeparateRows()
    {
        // Pre-#241 review feedback: an unlabeled Concat(Skills, Unlocks) rendered as one mixed
        // list ("Unarmed, Lore, Neutral, Comfortable, …") with no visual cue separating skill
        // names from favor-tier unlocks. The labeled-line projection makes the grouping legible.
        var npc = new Npc
        {
            Name = "Joeh",
            Services = new NpcServicePoco[]
            {
                new NpcTrainingServicePoco
                {
                    Type = "Training",
                    Skills = ["Unarmed", "Lore"],
                    Unlocks = ["Neutral", "Comfortable", "Friends"],
                },
            },
        };
        var refData = new StubReferenceData
        {
            NpcsByKey = { ["NPC_Joeh"] = npc },
        };
        var vm = new NpcsTabViewModel(refData, new SilmarillionReferenceNavigator(Array.Empty<IReferenceKindTarget>()));

        vm.SelectedRow = vm.AllNpcs.Single();

        var training = vm.DetailViewModel!.Services.Single();
        training.Details.Should().Equal(
            "Skills: Unarmed, Lore",
            "Unlocks at higher favor: Neutral, Comfortable, Friends");
    }

    [Fact]
    public void DetailViewModel_TrainingService_EmptyUnlocks_OnlyEmitsSkillsRow()
    {
        // Empty sub-lists drop out entirely — the section doesn't render a dangling label.
        var npc = new Npc
        {
            Name = "Joeh",
            Services = new NpcServicePoco[]
            {
                new NpcTrainingServicePoco { Type = "Training", Skills = ["Unarmed"], Unlocks = [] },
            },
        };
        var refData = new StubReferenceData
        {
            NpcsByKey = { ["NPC_Joeh"] = npc },
        };
        var vm = new NpcsTabViewModel(refData, new SilmarillionReferenceNavigator(Array.Empty<IReferenceKindTarget>()));

        vm.SelectedRow = vm.AllNpcs.Single();

        vm.DetailViewModel!.Services.Single().Details
            .Should().Equal("Skills: Unarmed");
    }

    [Fact]
    public void DetailViewModel_ServiceDetails_StoreCapIncreases_FormattedWithTierGoldKeywords()
    {
        var npc = new Npc
        {
            Name = "Joeh",
            Services = new NpcServicePoco[]
            {
                new NpcStoreServicePoco
                {
                    Type = "Store",
                    Favor = "Neutral",
                    CapIncreases =
                    [
                        "Despised:5000:Armor,Weapon",
                        "Comfortable:50000:",
                        "Friends:200000",
                    ],
                },
            },
        };
        var refData = new StubReferenceData
        {
            NpcsByKey = { ["NPC_Joeh"] = npc },
        };
        var vm = new NpcsTabViewModel(refData, new SilmarillionReferenceNavigator(Array.Empty<IReferenceKindTarget>()));

        vm.SelectedRow = vm.AllNpcs.Single();

        var store = vm.DetailViewModel!.Services.Single();
        store.Type.Should().Be("Store");
        store.MinFavorTier.Should().Be("Neutral");
        store.Details.Should().HaveCount(3);
        store.Details[0].Should().Be("Despised → 5,000g (Armor, Weapon)");
        store.Details[1].Should().Be("Comfortable → 50,000g");
        store.Details[2].Should().Be("Friends → 200,000g");
    }

    [Fact]
    public void DetailViewModel_Quests_PopulatedFromQuestsWithMatchingFavorNpc()
    {
        var npc = new Npc { Name = "Joeh" };
        var refData = new StubReferenceData
        {
            NpcsByKey = { ["NPC_Joeh"] = npc },
            QuestsByKey =
            {
                ["quest_1"] = new QuestEntry("quest_1", "Get Cat Eyeballs", "GetCatEyeballs", "...",
                    DisplayedLocation: null, FavorNpc: "NPC_Joeh", Keywords: [], Objectives: [],
                    Requirements: [], RequirementsToSustain: null, SkillRewards: [], ItemRewards: [],
                    FavorReward: 0, RewardEffects: [], RewardLootProfile: null,
                    ReuseMinutes: null, ReuseHours: null, ReuseDays: null,
                    PrefaceText: null, SuccessText: null),
                ["quest_2"] = new QuestEntry("quest_2", "Other Quest", "OtherQuest", "...",
                    DisplayedLocation: null, FavorNpc: "NPC_Other", Keywords: [], Objectives: [],
                    Requirements: [], RequirementsToSustain: null, SkillRewards: [], ItemRewards: [],
                    FavorReward: 0, RewardEffects: [], RewardLootProfile: null,
                    ReuseMinutes: null, ReuseHours: null, ReuseDays: null,
                    PrefaceText: null, SuccessText: null),
            },
        };
        var vm = new NpcsTabViewModel(refData, new SilmarillionReferenceNavigator(Array.Empty<IReferenceKindTarget>()));

        vm.SelectedRow = vm.AllNpcs.Single();

        vm.DetailViewModel!.Quests.Should().ContainSingle();
        vm.DetailViewModel.Quests[0].InternalName.Should().Be("GetCatEyeballs");
        vm.DetailViewModel.Quests[0].DisplayName.Should().Be("Get Cat Eyeballs");
    }

    [Fact]
    public void FileUpdated_NpcsRefresh_RebuildsAllNpcs_PreservingSelectionByInternalName()
    {
        var refData = new StubReferenceData
        {
            NpcsByKey = { ["NPC_Joeh"] = new Npc { Name = "Joeh" } },
        };
        var vm = new NpcsTabViewModel(refData, new SilmarillionReferenceNavigator(Array.Empty<IReferenceKindTarget>()));
        vm.SelectedRow = vm.AllNpcs.Single();

        // Swap in a fresh Npc instance for the same key — refData hands out the new instance.
        refData.NpcsByKey["NPC_Joeh"] = new Npc { Name = "Joeh", Desc = "Fresh after refresh." };
        refData.RaiseFileUpdated("npcs");

        vm.SelectedRow.Should().NotBeNull();
        vm.SelectedRow!.InternalName.Should().Be("NPC_Joeh");
        vm.DetailViewModel!.Description.Should().Be("Fresh after refresh.");
    }

    private sealed class StubReferenceData : IReferenceDataService
    {
        public Dictionary<string, Npc> NpcsByKey { get; } = new(StringComparer.Ordinal);
        public Dictionary<string, IReadOnlyList<Recipe>> RecipesTaughtByNpcMap { get; } = new(StringComparer.Ordinal);
        public Dictionary<string, IReadOnlyList<Item>> ItemsSoldByNpcMap { get; } = new(StringComparer.Ordinal);
        public Dictionary<string, QuestEntry> QuestsByKey { get; } = new(StringComparer.Ordinal);

        public IReadOnlyList<string> Keys { get; } = [];
        public IReadOnlyDictionary<long, Item> Items { get; } = new Dictionary<long, Item>();
        public IReadOnlyDictionary<string, Item> ItemsByInternalName { get; } = new Dictionary<string, Item>(StringComparer.Ordinal);
        public ItemKeywordIndex KeywordIndex => new(new Dictionary<long, Item>());
        public IReadOnlyDictionary<string, Recipe> Recipes { get; } = new Dictionary<string, Recipe>();
        public IReadOnlyDictionary<string, Recipe> RecipesByInternalName { get; } = new Dictionary<string, Recipe>();
        public IReadOnlyDictionary<string, SkillEntry> Skills { get; } = new Dictionary<string, SkillEntry>();
        public IReadOnlyDictionary<string, XpTableEntry> XpTables { get; } = new Dictionary<string, XpTableEntry>();
        public IReadOnlyDictionary<string, NpcEntry> Npcs { get; } = new Dictionary<string, NpcEntry>();
        public IReadOnlyDictionary<string, Npc> NpcsByInternalName => NpcsByKey;
        public IReadOnlyDictionary<string, IReadOnlyList<Recipe>> RecipesTaughtByNpc => RecipesTaughtByNpcMap;
        public IReadOnlyDictionary<string, IReadOnlyList<Item>> ItemsSoldByNpc => ItemsSoldByNpcMap;
        public IReadOnlyDictionary<string, AreaEntry> Areas { get; } = new Dictionary<string, AreaEntry>();
        public IReadOnlyDictionary<string, IReadOnlyList<ItemSource>> ItemSources { get; } = new Dictionary<string, IReadOnlyList<ItemSource>>();
        public IReadOnlyDictionary<string, IReadOnlyList<RecipeSource>> RecipeSources { get; } = new Dictionary<string, IReadOnlyList<RecipeSource>>();
        public IReadOnlyDictionary<string, AttributeEntry> Attributes { get; } = new Dictionary<string, AttributeEntry>();
        public IReadOnlyDictionary<string, PowerEntry> Powers { get; } = new Dictionary<string, PowerEntry>();
        public IReadOnlyDictionary<string, IReadOnlyList<string>> Profiles { get; } = new Dictionary<string, IReadOnlyList<string>>();
        public IReadOnlyDictionary<string, QuestEntry> Quests => QuestsByKey;
        public IReadOnlyDictionary<string, QuestEntry> QuestsByInternalName { get; } = new Dictionary<string, QuestEntry>();
        public IReadOnlyDictionary<string, string> Strings { get; } = new Dictionary<string, string>();

        public ReferenceFileSnapshot GetSnapshot(string key) => new(key, ReferenceFileSource.Bundled, "test", null, 0);
        public Task RefreshAsync(string key, CancellationToken ct = default) => Task.CompletedTask;
        public Task RefreshAllAsync(CancellationToken ct = default) => Task.CompletedTask;
        public void BeginBackgroundRefresh() { }
        public event EventHandler<string>? FileUpdated;

        public void RaiseFileUpdated(string fileKey) => FileUpdated?.Invoke(this, fileKey);
    }
}
