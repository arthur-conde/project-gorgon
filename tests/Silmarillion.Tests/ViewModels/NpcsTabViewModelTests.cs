using FluentAssertions;
using Mithril.Reference.Models.Abilities;
using Mithril.Reference.Models.Items;
using Mithril.Reference.Models.Npcs;
using Mithril.Reference.Models.Recipes;
using Mithril.Shared.Reference;
using Mithril.Shared.Wpf.Query;
using Mithril.TestSupport;
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

        var vm = new NpcsTabViewModel(refData, new SilmarillionReferenceNavigator(Array.Empty<IReferenceKindTarget>()), new ReferenceDataEntityNameResolver(refData));

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
        var vm = new NpcsTabViewModel(refData, new SilmarillionReferenceNavigator(Array.Empty<IReferenceKindTarget>()), new ReferenceDataEntityNameResolver(refData));

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
        var vm = new NpcsTabViewModel(refData, new SilmarillionReferenceNavigator(Array.Empty<IReferenceKindTarget>()), new ReferenceDataEntityNameResolver(refData));

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
        var vm = new NpcsTabViewModel(refData, new SilmarillionReferenceNavigator(Array.Empty<IReferenceKindTarget>()), new ReferenceDataEntityNameResolver(refData));

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
        var vm = new NpcsTabViewModel(refData, new SilmarillionReferenceNavigator(Array.Empty<IReferenceKindTarget>()), new ReferenceDataEntityNameResolver(refData));

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
        var vm = new NpcsTabViewModel(refData, new SilmarillionReferenceNavigator(Array.Empty<IReferenceKindTarget>()), new ReferenceDataEntityNameResolver(refData));

        vm.SelectedRow = vm.AllNpcs.Single();
        vm.SelectedRow = null;

        vm.DetailViewModel.Should().BeNull();
    }

    [Fact]
    public void DetailViewModel_AreaChip_PopulatedFromAreaName_NavigableWhenAreaKindRegistered()
    {
        // Cookbook *Cross-link chips → audit existing surfaces*: when the Areas tab ships
        // (#245), this previously-stub plain-text field flips to a navigable chip.
        var refData = new StubReferenceData
        {
            NpcsByKey =
            {
                ["NPC_Joeh"] = new Npc { Name = "Joeh", AreaName = "AreaSerbule", AreaFriendlyName = "Serbule" },
            },
        };
        var vm = new NpcsTabViewModel(refData, NavFactory.WithKinds(EntityKind.Area), new ReferenceDataEntityNameResolver(refData));

        vm.SelectedRow = vm.AllNpcs.Single();

        var chip = vm.DetailViewModel!.AreaChip;
        chip.Should().NotBeNull();
        chip!.DisplayName.Should().Be("Serbule", because: "AreaFriendlyName is the chip's display label");
        chip.Reference.Should().Be(EntityRef.Area("AreaSerbule"), because: "AreaName is the navigation key");
        chip.IsNavigable.Should().BeTrue(because: "the Areas kind is registered in this fixture");
    }

    [Fact]
    public void DetailViewModel_AreaChip_DegradesToNonNavigable_WhenAreaKindNotRegistered()
    {
        var refData = new StubReferenceData
        {
            NpcsByKey =
            {
                ["NPC_Joeh"] = new Npc { Name = "Joeh", AreaName = "AreaSerbule", AreaFriendlyName = "Serbule" },
            },
        };
        // Empty navigator — no kind targets registered, so CanOpen returns false.
        var vm = new NpcsTabViewModel(refData, NavFactory.WithKinds(), new ReferenceDataEntityNameResolver(refData));

        vm.SelectedRow = vm.AllNpcs.Single();

        vm.DetailViewModel!.AreaChip.Should().NotBeNull();
        vm.DetailViewModel.AreaChip!.IsNavigable.Should().BeFalse(
            because: "the chip degrades to plain text in EntityChip's fallback rendering when the kind isn't registered");
    }

    [Fact]
    public void DetailViewModel_AreaChip_FallsBackToAreaName_WhenFriendlyNameEmpty()
    {
        var refData = new StubReferenceData
        {
            NpcsByKey = { ["NPC_X"] = new Npc { Name = "X", AreaName = "AreaSerbule" } },
        };
        var vm = new NpcsTabViewModel(refData, NavFactory.WithKinds(EntityKind.Area), new ReferenceDataEntityNameResolver(refData));

        vm.SelectedRow = vm.AllNpcs.Single();

        vm.DetailViewModel!.AreaChip!.DisplayName.Should().Be("AreaSerbule",
            because: "the chip's label falls back to the raw area key when AreaFriendlyName is missing");
    }

    [Fact]
    public void DetailViewModel_AreaChip_NullWhenNpcHasNoArea()
    {
        var refData = new StubReferenceData
        {
            NpcsByKey = { ["NPC_Wandering"] = new Npc { Name = "Wandering" } },
        };
        var vm = new NpcsTabViewModel(refData, NavFactory.WithKinds(EntityKind.Area), new ReferenceDataEntityNameResolver(refData));

        vm.SelectedRow = vm.AllNpcs.Single();

        vm.DetailViewModel!.AreaChip.Should().BeNull(
            because: "no AreaName on the POCO means no area chip — XAML hides the row via NullToVis");
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
        var vm = new NpcsTabViewModel(refData, NavFactory.WithKinds(EntityKind.Recipe), new ReferenceDataEntityNameResolver(refData));

        vm.SelectedRow = vm.AllNpcs.Single();

        vm.DetailViewModel!.TaughtRecipes.Should().HaveCount(1);
        var chip = vm.DetailViewModel.TaughtRecipes[0];
        chip.DisplayName.Should().Be("Bake Bread");
        chip.IconId.Should().Be(7);
        chip.Reference.Should().Be(EntityRef.Recipe("BakeBread"));
        chip.IsNavigable.Should().BeTrue();
    }

    [Fact]
    public void DetailViewModel_TaughtAbilities_ProjectsAbilitiesTaughtByNpc_ToChips_Navigable_WhenAbilityKindRegistered()
    {
        var ability = new Ability
        {
            InternalName = "WoundingShot",
            Name = "Wounding Shot",
            Skill = "Archery",
            Level = 5,
            IconID = 42,
        };
        var refData = new StubReferenceData
        {
            NpcsByKey = { ["NPC_Flia"] = new Npc { Name = "Flia" } },
            AbilitiesTaughtByNpcMap = { ["NPC_Flia"] = new[] { ability } },
        };
        var vm = new NpcsTabViewModel(refData, NavFactory.WithKinds(EntityKind.Ability), new ReferenceDataEntityNameResolver(refData));

        vm.SelectedRow = vm.AllNpcs.Single();

        vm.DetailViewModel!.TaughtAbilities.Should().HaveCount(1);
        var chip = vm.DetailViewModel.TaughtAbilities[0];
        chip.DisplayName.Should().Be("Wounding Shot");
        chip.IconId.Should().Be(42);
        chip.Reference.Should().Be(EntityRef.Ability("WoundingShot"));
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
        var vm = new NpcsTabViewModel(refData, NavFactory.WithKinds(EntityKind.Item), new ReferenceDataEntityNameResolver(refData));

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
        var vm = new NpcsTabViewModel(refData, new SilmarillionReferenceNavigator(Array.Empty<IReferenceKindTarget>()), new ReferenceDataEntityNameResolver(refData));

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
        var vm = new NpcsTabViewModel(refData, new SilmarillionReferenceNavigator(Array.Empty<IReferenceKindTarget>()), new ReferenceDataEntityNameResolver(refData));

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
        var vm = new NpcsTabViewModel(refData, new SilmarillionReferenceNavigator(Array.Empty<IReferenceKindTarget>()), new ReferenceDataEntityNameResolver(refData));

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
        var vm = new NpcsTabViewModel(refData, new SilmarillionReferenceNavigator(Array.Empty<IReferenceKindTarget>()), new ReferenceDataEntityNameResolver(refData));

        vm.SelectedRow = vm.AllNpcs.Single();

        var training = vm.DetailViewModel!.Services.Single();
        training.Details.Select(d => d.Text).Should().Equal(
            "Skills: Unarmed, Lore",
            "Unlocks at higher favor: Neutral, Comfortable, Friends");
        training.Details.Should().AllSatisfy(d => d.Chips.Should().BeEmpty(
            because: "Training rows are text-only; only Store cap-increase rows surface chips."));
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
        var vm = new NpcsTabViewModel(refData, new SilmarillionReferenceNavigator(Array.Empty<IReferenceKindTarget>()), new ReferenceDataEntityNameResolver(refData));

        vm.SelectedRow = vm.AllNpcs.Single();

        vm.DetailViewModel!.Services.Single().Details
            .Select(d => d.Text).Should().Equal("Skills: Unarmed");
    }

    [Fact]
    public void DetailViewModel_TrainingService_SkillsResolveToFriendlyDisplayName_FromSkillsJson()
    {
        // Bug from #292: training.Skills carries raw skills.json keys ("NonfictionWriting"),
        // which were being passed straight to string.Join. The resolver should map PascalCase
        // keys to their SkillEntry.DisplayName ("Non-Fiction Writing"). Skills whose key already
        // matches their display name (Toolcrafting, Carpentry) round-trip unchanged.
        var npc = new Npc
        {
            Name = "Hulon",
            Services = new NpcServicePoco[]
            {
                new NpcTrainingServicePoco
                {
                    Type = "Training",
                    Skills = ["Toolcrafting", "NonfictionWriting", "Carpentry"],
                },
            },
        };
        var refData = new StubReferenceData
        {
            NpcsByKey = { ["NPC_Hulon"] = npc },
            SkillsByKey =
            {
                ["Toolcrafting"] = MakeSkill("Toolcrafting", "Toolcrafting"),
                ["NonfictionWriting"] = MakeSkill("NonfictionWriting", "Non-Fiction Writing"),
                ["Carpentry"] = MakeSkill("Carpentry", "Carpentry"),
            },
        };
        var vm = new NpcsTabViewModel(refData, new SilmarillionReferenceNavigator(Array.Empty<IReferenceKindTarget>()), new ReferenceDataEntityNameResolver(refData));

        vm.SelectedRow = vm.AllNpcs.Single();

        vm.DetailViewModel!.Services.Single().Details
            .Select(d => d.Text).Should().Equal("Skills: Toolcrafting, Non-Fiction Writing, Carpentry");
    }

    [Fact]
    public void DetailViewModel_TrainingService_UnknownSkillKey_FallsThroughToRawKey()
    {
        // Defensive — if a Training service references a skill that isn't in skills.json
        // (stale or out-of-sync data), surface the raw key rather than crash or render blank.
        var npc = new Npc
        {
            Name = "Trainer",
            Services = new NpcServicePoco[]
            {
                new NpcTrainingServicePoco { Type = "Training", Skills = ["UnknownSkillFromTheFuture"] },
            },
        };
        var refData = new StubReferenceData
        {
            NpcsByKey = { ["NPC_Trainer"] = npc },
        };
        var vm = new NpcsTabViewModel(refData, new SilmarillionReferenceNavigator(Array.Empty<IReferenceKindTarget>()), new ReferenceDataEntityNameResolver(refData));

        vm.SelectedRow = vm.AllNpcs.Single();

        vm.DetailViewModel!.Services.Single().Details
            .Select(d => d.Text).Should().Equal("Skills: UnknownSkillFromTheFuture");
    }

    private static SkillEntry MakeSkill(string key, string displayName) =>
        new(key, displayName, Id: 0, Combat: false, XpTable: "", MaxBonusLevels: 0,
            Parents: Array.Empty<string>(), Rewards: new Dictionary<string, SkillRewardEntry>());

    [Fact]
    public void DetailViewModel_ServiceDetails_StoreCapIncreases_FormattedWithTierGoldKeywords()
    {
        // Prose stays the tier+gold form; keywords move to a per-line chip strip (covered by
        // the dedicated chip test below). The parenthesised "(Armor, Weapon)" tail is gone now
        // that chips replace it.
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
        var vm = new NpcsTabViewModel(refData, new SilmarillionReferenceNavigator(Array.Empty<IReferenceKindTarget>()), new ReferenceDataEntityNameResolver(refData));

        vm.SelectedRow = vm.AllNpcs.Single();

        var store = vm.DetailViewModel!.Services.Single();
        store.Type.Should().Be("Store");
        store.MinFavorTier.Should().Be("Neutral");
        store.Details.Should().HaveCount(3);
        store.Details[0].Text.Should().Be("Despised → 5,000g");
        store.Details[1].Text.Should().Be("Comfortable → 50,000g");
        store.Details[2].Text.Should().Be("Friends → 200,000g");
    }

    [Fact]
    public void DetailViewModel_ServiceDetails_StoreCapKeywords_EmitNavigableItemByKeywordChips()
    {
        // #292 lifted the per-tier keyword tuple into chips. #326 degraded these to
        // non-navigable plain text when the double-duty EntityKind.ItemKeyword was retired
        // for its fan-out use. #327 restores navigability via the symmetric single-keyword
        // EntityKind.ItemByKeyword filter pivot (1:1 — one tag → Items tab filtered by
        // Keywords CONTAINS; NOT the retired #270 recipe-slot fan-out kind).
        var npc = new Npc
        {
            Name = "Joeh",
            Services = new NpcServicePoco[]
            {
                new NpcStoreServicePoco
                {
                    Type = "Store",
                    CapIncreases =
                    [
                        "Despised:10000:Food,CookingIngredient,AlchemyIngredient,Potion",
                        "Comfortable:50000:",
                    ],
                },
            },
        };
        var refData = new StubReferenceData
        {
            NpcsByKey = { ["NPC_Joeh"] = npc },
        };
        var vm = new NpcsTabViewModel(refData, NavFactory.WithKinds(EntityKind.ItemByKeyword), new ReferenceDataEntityNameResolver(refData));

        vm.SelectedRow = vm.AllNpcs.Single();

        var store = vm.DetailViewModel!.Services.Single();
        store.Details.Should().HaveCount(2);

        var firstLine = store.Details[0];
        firstLine.Chips.Should().HaveCount(4);
        firstLine.Chips.Should().AllSatisfy(c =>
        {
            c.IsNavigable.Should().BeTrue(
                because: "#327 restored the single-keyword Items filter pivot via EntityKind.ItemByKeyword");
            c.Reference.Kind.Should().Be(EntityKind.ItemByKeyword);
        });
        // The chip's deep-link payload carries the raw keyword tag (not the friendly
        // display label) so the Items-tab filter is Keywords CONTAINS "<tag>".
        firstLine.Chips.Select(c => c.Reference.InternalName).Should().Equal(
            "Food", "CookingIngredient", "AlchemyIngredient", "Potion");
        // Friendly chip labels fall back to a CamelCase split when KeywordDisplayNames has no
        // entry — mirrors the existing item-detail "Used as" chip behaviour from PR #267.
        firstLine.Chips.Select(c => c.DisplayName).Should().Equal(
            "Food", "Cooking Ingredient", "Alchemy Ingredient", "Potion");

        // The empty-keyword row has no chips.
        store.Details[1].Chips.Should().BeEmpty();
    }

    [Fact]
    public void DetailViewModel_ServiceDetails_StoreCapKeywords_NonNavigable_WhenItemByKeywordKindNotRegistered()
    {
        // Mirror image: in a context where ItemByKeyword routing isn't wired (e.g. tests, or a
        // future host that swaps out the navigator), chips should still render but degrade to
        // plain-text via IsNavigable=false. Validates EntityChipVm's graceful-degradation contract
        // — the chip-builder gates IsNavigable on navigator.CanOpen, not an unconditional true.
        var npc = new Npc
        {
            Name = "Joeh",
            Services = new NpcServicePoco[]
            {
                new NpcStoreServicePoco
                {
                    Type = "Store",
                    CapIncreases = ["Despised:10000:Food,Potion"],
                },
            },
        };
        var refData = new StubReferenceData
        {
            NpcsByKey = { ["NPC_Joeh"] = npc },
        };
        var vm = new NpcsTabViewModel(refData, new SilmarillionReferenceNavigator(Array.Empty<IReferenceKindTarget>()), new ReferenceDataEntityNameResolver(refData));

        vm.SelectedRow = vm.AllNpcs.Single();

        var chips = vm.DetailViewModel!.Services.Single().Details[0].Chips;
        chips.Should().HaveCount(2);
        chips.Should().AllSatisfy(c => c.IsNavigable.Should().BeFalse());
    }

    [Fact]
    public void DetailViewModel_ServiceDetails_ConsignmentItemTypes_EmitNavigableItemByKeywordChips()
    {
        // Follow-up to #292 / PR #294: ConsignmentService.ItemTypes is an array of raw
        // item-keyword tags (e.g. Equipment, CorpseTrophy, Tool) — same shape as Store cap
        // keyword tuples, lifted into a per-line chip strip. #326 degraded these to
        // non-navigable plain text on ItemKeyword retirement; #327 restores navigability
        // via the symmetric single-keyword EntityKind.ItemByKeyword filter pivot.
        var npc = new Npc
        {
            Name = "Joeh",
            Services = new NpcServicePoco[]
            {
                new ConsignmentService
                {
                    Type = "Consignment",
                    ItemTypes = ["Equipment", "CorpseTrophy", "AugmentationRelated"],
                    Unlocks = ["CloseFriends", "BestFriends"],
                },
            },
        };
        var refData = new StubReferenceData
        {
            NpcsByKey = { ["NPC_Joeh"] = npc },
        };
        var vm = new NpcsTabViewModel(refData, NavFactory.WithKinds(EntityKind.ItemByKeyword), new ReferenceDataEntityNameResolver(refData));

        vm.SelectedRow = vm.AllNpcs.Single();

        var consignment = vm.DetailViewModel!.Services.Single();
        consignment.Details.Should().HaveCount(2);

        var itemsLine = consignment.Details[0];
        itemsLine.Text.Should().Be("Items:", because: "the label is preserved as prose so the chip strip reads as a labeled list, matching the Store cap-increase visual rhythm");
        itemsLine.Chips.Should().HaveCount(3);
        itemsLine.Chips.Should().AllSatisfy(c =>
        {
            c.IsNavigable.Should().BeTrue(
                because: "#327 restored the single-keyword Items filter pivot via EntityKind.ItemByKeyword");
            c.Reference.Kind.Should().Be(EntityKind.ItemByKeyword);
        });
        itemsLine.Chips.Select(c => c.Reference.InternalName).Should().Equal(
            "Equipment", "CorpseTrophy", "AugmentationRelated");
        // CamelCase split fallback when KeywordDisplayNames has no entry (default test stub).
        itemsLine.Chips.Select(c => c.DisplayName).Should().Equal(
            "Equipment", "Corpse Trophy", "Augmentation Related");
    }

    [Fact]
    public void DetailViewModel_ServiceDetails_ConsignmentUnlocks_StaysTextOnly()
    {
        // Unlocks are favor tiers, not item keywords — they must stay as comma-joined prose.
        // Only ItemTypes lifts into chips; mirrors the Store cap split where the keyword tuple
        // becomes chips but the tier→gold prose stays text.
        var npc = new Npc
        {
            Name = "Joeh",
            Services = new NpcServicePoco[]
            {
                new ConsignmentService
                {
                    Type = "Consignment",
                    ItemTypes = ["Equipment"],
                    Unlocks = ["CloseFriends", "BestFriends", "LikeFamily"],
                },
            },
        };
        var refData = new StubReferenceData
        {
            NpcsByKey = { ["NPC_Joeh"] = npc },
        };
        var vm = new NpcsTabViewModel(refData, NavFactory.WithKinds(), new ReferenceDataEntityNameResolver(refData));

        vm.SelectedRow = vm.AllNpcs.Single();

        var unlocksLine = vm.DetailViewModel!.Services.Single().Details[1];
        unlocksLine.Text.Should().Be("Unlocks at higher favor: CloseFriends, BestFriends, LikeFamily");
        unlocksLine.Chips.Should().BeEmpty();
    }

    [Fact]
    public void DetailViewModel_ServiceDetails_ConsignmentItemTypes_NonNavigable_WhenItemByKeywordKindNotRegistered()
    {
        // Mirror image of the Store cap non-navigable test: when the navigator has no
        // ItemByKeyword kind wired, chips still render but degrade to IsNavigable=false.
        var npc = new Npc
        {
            Name = "Joeh",
            Services = new NpcServicePoco[]
            {
                new ConsignmentService
                {
                    Type = "Consignment",
                    ItemTypes = ["Equipment", "Tool"],
                },
            },
        };
        var refData = new StubReferenceData
        {
            NpcsByKey = { ["NPC_Joeh"] = npc },
        };
        var vm = new NpcsTabViewModel(refData, new SilmarillionReferenceNavigator(Array.Empty<IReferenceKindTarget>()), new ReferenceDataEntityNameResolver(refData));

        vm.SelectedRow = vm.AllNpcs.Single();

        var chips = vm.DetailViewModel!.Services.Single().Details[0].Chips;
        chips.Should().HaveCount(2);
        chips.Should().AllSatisfy(c => c.IsNavigable.Should().BeFalse());
    }

    [Fact]
    public void DetailViewModel_Quests_PopulatedFromQuestsByGiverNpc()
    {
        var npc = new Npc { Name = "Joeh" };
        var quest = new Mithril.Reference.Models.Quests.Quest
        {
            InternalName = "GetCatEyeballs",
            Name = "Get Cat Eyeballs",
            FavorNpc = "NPC_Joeh",
        };
        var refData = new StubReferenceData
        {
            NpcsByKey = { ["NPC_Joeh"] = npc },
            QuestsByGiverNpcMap = { ["NPC_Joeh"] = new[] { quest } },
        };
        var vm = new NpcsTabViewModel(refData, new SilmarillionReferenceNavigator(Array.Empty<IReferenceKindTarget>()), new ReferenceDataEntityNameResolver(refData));

        vm.SelectedRow = vm.AllNpcs.Single();

        vm.DetailViewModel!.Quests.Should().ContainSingle();
        vm.DetailViewModel.Quests[0].Reference.InternalName.Should().Be("GetCatEyeballs");
        vm.DetailViewModel.Quests[0].DisplayName.Should().Be("Get Cat Eyeballs");
    }

    [Fact]
    public void FileUpdated_NpcsRefresh_RebuildsAllNpcs_PreservingSelectionByInternalName()
    {
        var refData = new StubReferenceData
        {
            NpcsByKey = { ["NPC_Joeh"] = new Npc { Name = "Joeh" } },
        };
        var vm = new NpcsTabViewModel(refData, new SilmarillionReferenceNavigator(Array.Empty<IReferenceKindTarget>()), new ReferenceDataEntityNameResolver(refData));
        vm.SelectedRow = vm.AllNpcs.Single();

        // Swap in a fresh Npc instance for the same key — refData hands out the new instance.
        refData.NpcsByKey["NPC_Joeh"] = new Npc { Name = "Joeh", Desc = "Fresh after refresh." };
        refData.RaiseFileUpdated("npcs");

        vm.SelectedRow.Should().NotBeNull();
        vm.SelectedRow!.InternalName.Should().Be("NPC_Joeh");
        vm.DetailViewModel!.Description.Should().Be("Fresh after refresh.");
    }

    [Fact]
    public void NpcListRow_CapIncreases_FlattenedAndParsed_AcrossStoreServices()
    {
        // #352: surface the parsed store cap-increase rows on the list-row so the
        // query box can ask the originating #349 question. Two StoreServices flatten
        // into one homogeneous list; parsing (tier/gold/keywords) is #350's parser.
        var npc = new Npc
        {
            Name = "Big Vendor",
            Services = new NpcServicePoco[]
            {
                new NpcStoreServicePoco
                {
                    Type = "Store",
                    CapIncreases = ["Friends:5000:Armor,Weapon", "Comfortable:50000:"],
                },
                new NpcTrainingServicePoco { Type = "Training", Skills = ["Unarmed"] },
                new NpcStoreServicePoco
                {
                    Type = "Store",
                    CapIncreases = ["Despised:200000"],
                },
            },
        };
        var refData = new StubReferenceData { NpcsByKey = { ["NPC_Vendor"] = npc } };
        var vm = new NpcsTabViewModel(refData, new SilmarillionReferenceNavigator(Array.Empty<IReferenceKindTarget>()), new ReferenceDataEntityNameResolver(refData));

        var caps = vm.AllNpcs.Single().CapIncreases;
        caps.Select(c => c.Tier).Should().Equal(FavorTier.Friends, FavorTier.Comfortable, FavorTier.Despised);
        caps.Select(c => c.GoldCap).Should().Equal(5000, 50000, 200000);
        caps[0].Keywords.Should().Equal("Armor", "Weapon");
        caps[1].Keywords.Should().BeEmpty();
    }

    [Fact]
    public void QueryText_CapIncreasesWithAny_CorrelatesTierAndGoldCapPerElement()
    {
        // The originating #349 question, end-to-end through the engine over the
        // NPC list-row schema: "a Friends-tier cap above 1000g". The correlation
        // trap NPC has Friends and >1000 on DIFFERENT cap rows — it must NOT match.
        var refData = new StubReferenceData
        {
            NpcsByKey =
            {
                ["NPC_Match"] = new Npc
                {
                    Name = "Match",
                    Services = new NpcServicePoco[]
                    {
                        new NpcStoreServicePoco
                        {
                            Type = "Store",
                            CapIncreases = ["Friends:5000:Armor", "Despised:100:Food"],
                        },
                    },
                },
                ["NPC_SplitTrap"] = new Npc
                {
                    Name = "Split Trap",
                    Services = new NpcServicePoco[]
                    {
                        new NpcStoreServicePoco
                        {
                            Type = "Store",
                            CapIncreases = ["Friends:100:Armor", "Despised:9000:Food"],
                        },
                    },
                },
                ["NPC_NoStore"] = new Npc
                {
                    Name = "No Store",
                    Services = new NpcServicePoco[] { new NpcTrainingServicePoco { Type = "Training" } },
                },
            },
        };
        var vm = new NpcsTabViewModel(refData, new SilmarillionReferenceNavigator(Array.Empty<IReferenceKindTarget>()), new ReferenceDataEntityNameResolver(refData));

        const string query = "CapIncreases WITH ANY (Tier = 'Friends' AND GoldCap > 1000)";
        var columns = ColumnBindingHelper.BuildFromProperties(typeof(NpcListRow));
        var predicate = QueryCompiler.Compile(query, columns);
        predicate.Should().NotBeNull();

        var matches = vm.AllNpcs.Where(r => predicate!(r)).Select(r => r.InternalName).ToList();
        matches.Should().BeEquivalentTo(["NPC_Match"]);
    }

    [Fact]
    public void QueryText_CapIncreasesWithAny_TierIsOrdinal_FriendsOrBetter()
    {
        // #368: Tier is now FavorTier (ordinal). "Friends or better" must filter by
        // favor RANK, not string equality/alphabetical — Comfortable (below Friends)
        // is excluded; CloseFriends (above) is included.
        var refData = new StubReferenceData
        {
            NpcsByKey =
            {
                ["NPC_Friends"] = StoreNpc("Friends", "Friends:5000:Armor"),
                ["NPC_Closer"] = StoreNpc("Closer", "CloseFriends:5000:Armor"),
                ["NPC_TooLow"] = StoreNpc("TooLow", "Comfortable:5000:Armor"),
                ["NPC_WayLow"] = StoreNpc("WayLow", "Despised:5000:Armor"),
            },
        };
        var vm = new NpcsTabViewModel(refData, new SilmarillionReferenceNavigator(Array.Empty<IReferenceKindTarget>()), new ReferenceDataEntityNameResolver(refData));

        var columns = ColumnBindingHelper.BuildFromProperties(typeof(NpcListRow));
        var predicate = QueryCompiler.Compile(
            "CapIncreases WITH ANY (Tier >= 'Friends')", columns);
        predicate.Should().NotBeNull();

        vm.AllNpcs.Where(r => predicate!(r)).Select(r => r.InternalName)
            .Should().BeEquivalentTo(["NPC_Friends", "NPC_Closer"]);
    }

    private static Npc StoreNpc(string name, params string[] capIncreases) => new()
    {
        Name = name,
        Services = new NpcServicePoco[]
        {
            new NpcStoreServicePoco { Type = "Store", CapIncreases = capIncreases },
        },
    };

    private sealed class StubReferenceData : IReferenceDataService
    {
        public Dictionary<string, Npc> NpcsByKey { get; } = new(StringComparer.Ordinal);
        public Dictionary<string, IReadOnlyList<Recipe>> RecipesTaughtByNpcMap { get; } = new(StringComparer.Ordinal);
        public Dictionary<string, IReadOnlyList<Item>> ItemsSoldByNpcMap { get; } = new(StringComparer.Ordinal);
        public Dictionary<string, IReadOnlyList<Ability>> AbilitiesTaughtByNpcMap { get; } = new(StringComparer.Ordinal);
        public Dictionary<string, Mithril.Reference.Models.Quests.Quest> QuestsByKey { get; } = new(StringComparer.Ordinal);
        public Dictionary<string, IReadOnlyList<Mithril.Reference.Models.Quests.Quest>> QuestsByGiverNpcMap { get; } = new(StringComparer.Ordinal);
        public Dictionary<string, SkillEntry> SkillsByKey { get; } = new(StringComparer.Ordinal);

        public IReadOnlyList<string> Keys { get; } = [];
        public IReadOnlyDictionary<long, Item> Items { get; } = new Dictionary<long, Item>();
        public IReadOnlyDictionary<string, Item> ItemsByInternalName { get; } = new Dictionary<string, Item>(StringComparer.Ordinal);
        public ItemKeywordIndex KeywordIndex => new(new Dictionary<long, Item>());
        public IReadOnlyDictionary<string, Recipe> Recipes { get; } = new Dictionary<string, Recipe>();
        public IReadOnlyDictionary<string, Recipe> RecipesByInternalName { get; } = new Dictionary<string, Recipe>();
        public IReadOnlyDictionary<string, SkillEntry> Skills => SkillsByKey;
        public IReadOnlyDictionary<string, XpTableEntry> XpTables { get; } = new Dictionary<string, XpTableEntry>();
        public IReadOnlyDictionary<string, NpcEntry> Npcs { get; } = new Dictionary<string, NpcEntry>();
        public IReadOnlyDictionary<string, Npc> NpcsByInternalName => NpcsByKey;
        public IReadOnlyDictionary<string, IReadOnlyList<Recipe>> RecipesTaughtByNpc => RecipesTaughtByNpcMap;
        public IReadOnlyDictionary<string, IReadOnlyList<Item>> ItemsSoldByNpc => ItemsSoldByNpcMap;
        public IReadOnlyDictionary<string, IReadOnlyList<Ability>> AbilitiesTaughtByNpc => AbilitiesTaughtByNpcMap;
        // Surface every ability referenced in AbilitiesTaughtByNpcMap so the entity-name resolver
        // can project InternalName → Name for the chip's DisplayName.
        public IReadOnlyDictionary<string, Ability> AbilitiesByInternalName =>
            AbilitiesTaughtByNpcMap.Values.SelectMany(list => list)
                .Where(a => !string.IsNullOrEmpty(a.InternalName))
                .GroupBy(a => a.InternalName!, StringComparer.Ordinal)
                .ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);
        public IReadOnlyDictionary<string, AreaEntry> Areas { get; } = new Dictionary<string, AreaEntry>();
        public IReadOnlyDictionary<string, IReadOnlyList<ItemSource>> ItemSources { get; } = new Dictionary<string, IReadOnlyList<ItemSource>>();
        public IReadOnlyDictionary<string, IReadOnlyList<RecipeSource>> RecipeSources { get; } = new Dictionary<string, IReadOnlyList<RecipeSource>>();
        public IReadOnlyDictionary<string, AttributeEntry> Attributes { get; } = new Dictionary<string, AttributeEntry>();
        public IReadOnlyDictionary<string, PowerEntry> Powers { get; } = new Dictionary<string, PowerEntry>();
        public IReadOnlyDictionary<string, IReadOnlyList<string>> Profiles { get; } = new Dictionary<string, IReadOnlyList<string>>();
        public IReadOnlyDictionary<string, Mithril.Reference.Models.Quests.Quest> Quests => QuestsByKey;
        public IReadOnlyDictionary<string, Mithril.Reference.Models.Quests.Quest> QuestsByInternalName { get; } = new Dictionary<string, Mithril.Reference.Models.Quests.Quest>();
        public IReadOnlyDictionary<string, IReadOnlyList<Mithril.Reference.Models.Quests.Quest>> QuestsByGiverNpc => QuestsByGiverNpcMap;
        public IReadOnlyDictionary<string, string> Strings { get; } = new Dictionary<string, string>();

        public ReferenceFileSnapshot GetSnapshot(string key) => new(key, ReferenceFileSource.Bundled, "test", null, 0);
        public Task RefreshAsync(string key, CancellationToken ct = default) => Task.CompletedTask;
        public Task RefreshAllAsync(CancellationToken ct = default) => Task.CompletedTask;
        public void BeginBackgroundRefresh() { }
        public event EventHandler<string>? FileUpdated;

        public void RaiseFileUpdated(string fileKey) => FileUpdated?.Invoke(this, fileKey);
    }
}
