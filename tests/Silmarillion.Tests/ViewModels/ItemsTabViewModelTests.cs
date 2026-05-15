using FluentAssertions;
using Mithril.Reference.Models.Items;
using Mithril.Reference.Models.Recipes;
using Mithril.Shared.Reference;
using Mithril.Shared.Wpf;
using Mithril.TestSupport;
using Silmarillion.Navigation;
using Silmarillion.ViewModels;
using Xunit;

namespace Silmarillion.Tests.ViewModels;

public sealed class ItemsTabViewModelTests
{
    [Fact]
    public void AllItems_PopulatedFromReferenceData_OrderedByName()
    {
        var refData = new StubReferenceData
        {
            ItemsByName =
            {
                ["Tomato"] = new Item { Id = 1, InternalName = "Tomato", Name = "Tomato", IconId = 1 },
                ["Apple"] = new Item { Id = 2, InternalName = "Apple", Name = "Apple", IconId = 2 },
            },
        };

        var vm = new ItemsTabViewModel(refData, new SilmarillionReferenceNavigator(Array.Empty<IReferenceKindTarget>()), new FakeEntityNameResolver());

        vm.AllItems.Should().HaveCount(2);
        vm.AllItems.Select(i => i.Name).Should().Equal("Apple", "Tomato");
    }

    [Fact]
    public void SelectingItem_BuildsDetailViewModel()
    {
        var item = new Item { Id = 1, InternalName = "Tomato", Name = "Tomato", IconId = 1 };
        var refData = new StubReferenceData
        {
            ItemsByName = { ["Tomato"] = item },
        };
        var vm = new ItemsTabViewModel(refData, new SilmarillionReferenceNavigator(Array.Empty<IReferenceKindTarget>()), new FakeEntityNameResolver());

        vm.SelectedItem = item;

        vm.DetailViewModel.Should().NotBeNull();
        vm.DetailViewModel!.Item.Should().Be(item);
    }

    [Fact]
    public void DeselectingItem_ClearsDetailViewModel()
    {
        var item = new Item { Id = 1, InternalName = "Tomato", Name = "Tomato" };
        var refData = new StubReferenceData
        {
            ItemsByName = { ["Tomato"] = item },
        };
        var vm = new ItemsTabViewModel(refData, new SilmarillionReferenceNavigator(Array.Empty<IReferenceKindTarget>()), new FakeEntityNameResolver());

        vm.SelectedItem = item;
        vm.SelectedItem = null;

        vm.DetailViewModel.Should().BeNull();
    }

    [Fact]
    public void BuildCrossLinkContext_emits_keyword_chips_for_intersection_of_item_Keywords_and_KeywordsUsedInRecipeSlots()
    {
        var tourmaline = new Item
        {
            Id = 99,
            InternalName = "MassiveTourmaline",
            Name = "Massive Tourmaline",
            IconId = 0,
            Keywords = [new ItemKeyword("Crystal", 0), new ItemKeyword("Bogus", 0)],
        };
        var refData = new StubReferenceData
        {
            ItemsByName = { ["MassiveTourmaline"] = tourmaline },
            KeywordsInRecipeSlots = new HashSet<string>(StringComparer.Ordinal) { "Crystal" },
        };
        var vm = new ItemsTabViewModel(refData, new SilmarillionReferenceNavigator(Array.Empty<IReferenceKindTarget>()), new FakeEntityNameResolver());

        vm.SelectedItem = tourmaline;

        vm.DetailViewModel.Should().NotBeNull();
        var chips = vm.DetailViewModel!.ConsumedAsKeywordIn;
        chips.Should().ContainSingle();
        chips.Single().DisplayName.Should().Be("Crystal");
        chips.Single().IconId.Should().Be(0);
        chips.Single().Reference.Should().Be(EntityRef.RecipeIngredientKeyword("Crystal"));
    }

    [Fact]
    public void Keyword_chip_uses_KeywordDisplayNames_when_present()
    {
        var item = new Item
        {
            Id = 99,
            InternalName = "ShinyArmor",
            Name = "Shiny Armor",
            Keywords = [new ItemKeyword("MetalArmor", 0)],
        };
        var refData = new StubReferenceData
        {
            ItemsByName = { ["ShinyArmor"] = item },
            KeywordsInRecipeSlots = new HashSet<string>(StringComparer.Ordinal) { "MetalArmor" },
            KeywordDisplays = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["MetalArmor"] = "Metal Armor",  // friendly form sourced from a recipe slot
            },
        };
        var vm = new ItemsTabViewModel(refData, new SilmarillionReferenceNavigator(Array.Empty<IReferenceKindTarget>()), new FakeEntityNameResolver());

        vm.SelectedItem = item;

        vm.DetailViewModel!.ConsumedAsKeywordIn.Single().DisplayName
            .Should().Be("Metal Armor", because: "KeywordDisplayNames lookup wins over the raw tag");
    }

    // ── #318 slice 4, surface 1 — Items "Used in" provenance popup ──────────────
    // The reverse-lookup "recipes that consume this item" is now a ProvenancePopupViewModel
    // fed IReferenceDataService.RecipesByIngredientItemWithReason directly; the retired
    // RecipeIngredientItem synthetic-kind ActionChip is gone. These tests assert the #318
    // invariant: popup membership == index collection, "View all N" == distinct members,
    // single-reason ⇒ flat, and opening pushes no navigator history.

    [Fact]
    public void UsedIn_belowCap_emitsAllChips_andBuildsPopup()
    {
        var item = new Item { Id = 1, InternalName = "MetalSlab1", Name = "Metal Slab" };
        var recipes = MakeRecipeList(count: 5);
        var refData = new StubReferenceData
        {
            ItemsByName = { ["MetalSlab1"] = item },
            RecipesByIngredient = { ["MetalSlab1"] = recipes },
        };
        var vm = new ItemsTabViewModel(refData, NavWithRecipe(), new FakeEntityNameResolver(),
            new SilmarillionSettings { UsedInChipCap = 12 });

        vm.SelectedItem = item;
        var detail = vm.DetailViewModel!;

        detail.ConsumedByRecipes.Should().HaveCount(5);
        detail.ConsumedByRecipesPopup.Should().NotBeNull(
            because: "the View-all affordance is always built, even at counts within the cap");
        var popup = detail.ConsumedByRecipesPopup!;
        popup.TotalCount.Should().Be(5);
        detail.ConsumedByRecipesTotal.Should().Be(5);
        detail.ShowConsumedByRecipesPopupCommand.CanExecute(null).Should().BeTrue();
        // Single reason (DirectIngredient) ⇒ flat list, no section chrome (#318 Discipline).
        popup.IsFlat.Should().BeTrue(because: "one trivial reason is noise — collapse to flat");
        popup.FlatChips.Select(c => c.Reference.InternalName).Should().BeEquivalentTo(
            recipes.Select(r => r.InternalName),
            because: "popup membership == the index collection (no second derivation)");
    }

    [Fact]
    public void UsedIn_aboveCap_capsChips_butPopupCarriesFullDistinctSet()
    {
        var item = new Item { Id = 1, InternalName = "MetalSlab1", Name = "Metal Slab", IconId = 4242 };
        // The MetalSlab1 case from the issue (~69 recipes).
        var recipes = MakeRecipeList(count: 69);
        var refData = new StubReferenceData
        {
            ItemsByName = { ["MetalSlab1"] = item },
            RecipesByIngredient = { ["MetalSlab1"] = recipes },
        };
        var vm = new ItemsTabViewModel(refData, NavWithRecipe(), new FakeEntityNameResolver(),
            new SilmarillionSettings { UsedInChipCap = 12 });

        vm.SelectedItem = item;
        var detail = vm.DetailViewModel!;

        detail.ConsumedByRecipes.Should().HaveCount(12,
            because: "cap 12 means the first dozen show as chips and the rest are reachable via the popup");
        var popup = detail.ConsumedByRecipesPopup!;
        // The defining #318 assertion: "View all N" == distinct index members, NOT the cap.
        popup.TotalCount.Should().Be(69);
        detail.ConsumedByRecipesTotal.Should().Be(69);
        popup.FlatChips.Should().HaveCount(69,
            because: "the popup carries the full set fed from the index, not the capped cluster");
        popup.FlatChips.Select(c => c.Reference.InternalName).Should().BeEquivalentTo(
            recipes.Select(r => r.InternalName));
    }

    [Fact]
    public void UsedIn_capZero_collapsesAllChips_butPopupStillFull()
    {
        var item = new Item { Id = 1, InternalName = "MetalSlab1", Name = "Metal Slab" };
        var refData = new StubReferenceData
        {
            ItemsByName = { ["MetalSlab1"] = item },
            RecipesByIngredient = { ["MetalSlab1"] = MakeRecipeList(count: 3) },
        };
        var vm = new ItemsTabViewModel(refData, NavWithRecipe(), new FakeEntityNameResolver(),
            new SilmarillionSettings { UsedInChipCap = 0 });

        vm.SelectedItem = item;
        var detail = vm.DetailViewModel!;

        detail.ConsumedByRecipes.Should().BeEmpty();
        detail.ConsumedByRecipesPopup.Should().NotBeNull();
        detail.ConsumedByRecipesPopup!.TotalCount.Should().Be(3);
        detail.ConsumedByRecipesTotal.Should().Be(3);
    }

    [Fact]
    public void UsedIn_noConsumingRecipe_popupIsNull_andSectionHidden()
    {
        var item = new Item { Id = 1, InternalName = "Lonely", Name = "Lonely" };
        var refData = new StubReferenceData { ItemsByName = { ["Lonely"] = item } };
        var vm = new ItemsTabViewModel(refData, NavWithRecipe(), new FakeEntityNameResolver(),
            new SilmarillionSettings { UsedInChipCap = 12 });

        vm.SelectedItem = item;
        var detail = vm.DetailViewModel!;

        detail.ConsumedByRecipes.Should().BeEmpty();
        detail.ConsumedByRecipesPopup.Should().BeNull(
            because: "no recipe consumes the item — the whole 'Used in' section hides");
        detail.ConsumedByRecipesTotal.Should().Be(0);
        detail.ShowConsumedByRecipesPopupCommand.CanExecute(null).Should().BeFalse();
    }

    [Fact]
    public void UsedInChipCap_change_liveRebuilds_DetailViewModel_PopupStaysFull()
    {
        // The user drags the slider; the open detail view re-caps on the spot. The popup
        // (fed from the index) carries the full set regardless of the cap.
        var item = new Item { Id = 1, InternalName = "MetalSlab1", Name = "Metal Slab" };
        var refData = new StubReferenceData
        {
            ItemsByName = { ["MetalSlab1"] = item },
            RecipesByIngredient = { ["MetalSlab1"] = MakeRecipeList(count: 20) },
        };
        var settings = new SilmarillionSettings { UsedInChipCap = 12 };
        var vm = new ItemsTabViewModel(refData, NavWithRecipe(), new FakeEntityNameResolver(), settings);
        vm.SelectedItem = item;

        var before = vm.DetailViewModel!;
        before.ConsumedByRecipes.Should().HaveCount(12);
        before.ConsumedByRecipesPopup!.TotalCount.Should().Be(20);

        settings.UsedInChipCap = 25;

        var after = vm.DetailViewModel!;
        after.Should().NotBeSameAs(before, because: "live-update rebuilds the detail VM on slider drag");
        after.ConsumedByRecipes.Should().HaveCount(20);
        after.ConsumedByRecipesPopup!.TotalCount.Should().Be(20,
            because: "the popup is fed the index directly — the cap never affects its count");
    }

    [Fact]
    public void UsedIn_recipeQualifyingOnlyViaNonPrimaryIndexEntry_appearsInPopup_withCorrectProvenance()
    {
        // #318 Gate C — the load-bearing regression that would have caught the original
        // dual-derivation bug for THIS surface. The pre-#318 "View all N" deep-linked via
        // RecipeIngredientItem, whose kind target re-derived the set as the query string
        // `Ingredients CONTAINS "<item>"`. If the index ever carried a member the query
        // string did NOT (e.g. a recipe added to RecipesByIngredientItemWithReason for a
        // reason the query couldn't express), the chip opened a list missing that member.
        // The popup-from-index has no query between the set and the screen, so a member
        // present ONLY in the index must still appear — with its DirectIngredient
        // provenance — and the count must equal the distinct index membership exactly.
        var item = new Item { Id = 1, InternalName = "Widget", Name = "Widget" };
        var direct = new Recipe { Key = "recipe_001", InternalName = "DirectUser", Name = "Direct User" };
        // A recipe whose membership comes from a match record only (the "non-primary"
        // analogue here — it lives in the provenance index, never re-derivable from a
        // naive query). It must still surface in the popup.
        var indexOnly = new Recipe { Key = "recipe_002", InternalName = "IndexOnlyUser", Name = "Index Only User" };
        var refData = new StubReferenceData
        {
            ItemsByName = { ["Widget"] = item },
            // Seed the provenance index directly with BOTH members so the test asserts
            // popup membership == index membership, independent of any query.
            RecipesByIngredientWithReasonOverride =
            {
                ["Widget"] = new[]
                {
                    new RecipeIngredientItemMatch(direct, RecipeIngredientItemMatchReason.DirectIngredient),
                    new RecipeIngredientItemMatch(indexOnly, RecipeIngredientItemMatchReason.DirectIngredient),
                },
            },
        };
        var vm = new ItemsTabViewModel(refData, NavWithRecipe(), new FakeEntityNameResolver(),
            new SilmarillionSettings { UsedInChipCap = 12 });

        vm.SelectedItem = item;
        var popup = vm.DetailViewModel!.ConsumedByRecipesPopup;

        popup.Should().NotBeNull();
        popup!.TotalCount.Should().Be(2, because: "View all N == distinct index members");
        popup.IsFlat.Should().BeTrue(because: "single reason (DirectIngredient) ⇒ flat list");
        popup.FlatChips.Select(c => c.Reference.InternalName).Should().BeEquivalentTo(
            new[] { "DirectUser", "IndexOnlyUser" },
            because: "every index member appears — there is no query that could drop one");
        // Provenance is intact: the lone section is the DirectIngredient one.
        popup.Sections.Should().ContainSingle().Which.Label.Should().Be("Used in");
        popup.Sections.Single().Chips.Select(c => c.Reference.InternalName).Should()
            .BeEquivalentTo(new[] { "DirectUser", "IndexOnlyUser" });
    }

    [Fact]
    public void UsedIn_openingPopup_doesNotTouchNavigator_noHistoryPushed()
    {
        // #318 — opening the popup must NOT push navigator back/forward history (the #229
        // non-navigating contract, mirroring TryOpenInWindow and the slice-2
        // EffectDetailViewModel test). The opener is swapped for a capturing no-op so no
        // window spawns; assert navigator state is pristine before and after.
        var item = new Item { Id = 1, InternalName = "MetalSlab1", Name = "Metal Slab" };
        var refData = new StubReferenceData
        {
            ItemsByName = { ["MetalSlab1"] = item },
            RecipesByIngredient = { ["MetalSlab1"] = MakeRecipeList(count: 4) },
        };
        var nav = new SilmarillionReferenceNavigator(Array.Empty<IReferenceKindTarget>());
        var vm = new ItemsTabViewModel(refData, nav, new FakeEntityNameResolver(),
            new SilmarillionSettings { UsedInChipCap = 12 });

        var prior = ItemDetailViewModel.ProvenancePopupOpener;
        ProvenancePopupViewModel? captured = null;
        ItemDetailViewModel.ProvenancePopupOpener = (popupVm, _) => captured = popupVm;
        try
        {
            vm.SelectedItem = item;
            var detail = vm.DetailViewModel!;

            nav.Current.Should().BeNull();
            nav.CanGoBack.Should().BeFalse();
            nav.CanGoForward.Should().BeFalse();

            detail.ShowConsumedByRecipesPopupCommand.Execute(null);

            captured.Should().NotBeNull(because: "the command invoked the opener with the built popup VM");
            // The defining assertion: opening the popup pushed no navigator state.
            nav.Current.Should().BeNull();
            nav.CanGoBack.Should().BeFalse();
            nav.CanGoForward.Should().BeFalse();
        }
        finally
        {
            ItemDetailViewModel.ProvenancePopupOpener = prior;
        }
    }

    private static SilmarillionReferenceNavigator NavWithRecipe() =>
        new(new IReferenceKindTarget[] { new StubKindTarget(EntityKind.Recipe) });

    private static IReadOnlyList<Recipe> MakeRecipeList(int count) =>
        Enumerable.Range(1, count)
            .Select(i => new Recipe { Key = $"recipe_{i:D3}", InternalName = $"Recipe{i:D3}", Name = $"Recipe {i:D3}" })
            .ToList();

    [Fact]
    public void SourceChips_NpcVendorSource_AttachesNpcEntityRef_Navigable_WhenNpcKindRegistered()
    {
        // Symmetric to RecipesTabViewModel.SourceChips: an Item's NPC vendor source must carry an
        // EntityRef.Npc(...) and be navigable once the NPCs kind target is registered (#241). Pre-fix,
        // ItemsTabViewModel hardcoded EntityReference: null / IsNavigable: false on every source, so
        // the chip stayed plain-text even after the NPCs tab shipped.
        var apple = new Item { Id = 1, InternalName = "Apple", Name = "Apple" };
        var refData = new ItemSourceStub
        {
            ItemsByName = { ["Apple"] = apple },
            ItemSourcesByName =
            {
                ["Apple"] = new[]
                {
                    new ItemSource("Vendor", "NPC_Joeh", null),
                },
            },
        };
        // Navigator with the Npc kind registered — matches the post-#241 ship state.
        var vm = new ItemsTabViewModel(refData, new SilmarillionReferenceNavigator(
            new IReferenceKindTarget[] { new StubKindTarget(EntityKind.Npc) }),
            new ReferenceDataEntityNameResolver(refData));

        vm.SelectedItem = apple;

        vm.DetailViewModel!.Sources.Should().ContainSingle();
        var chip = vm.DetailViewModel.Sources![0];
        // No NPC POCO seeded → ReferenceDataEntityNameResolver falls back to stripping the
        // "NPC_" prefix (envelope-key heuristic), so "NPC_Joeh" reads as "Joeh" on the chip.
        chip.DisplayName.Should().Be("Vendor: Joeh");
        chip.EntityReference.Should().Be(EntityRef.Npc("NPC_Joeh"));
        chip.IsNavigable.Should().BeTrue();
    }

    [Fact]
    public void SourceChips_NpcVendorSource_ResolvesNpcFriendlyName_WhenNpcPocoIsRegistered()
    {
        // When the NPC POCO ships a Name (the common case for the named cast — Joeh, Marna,
        // Velkort), the chip uses Name verbatim rather than the envelope key. Mirrors what
        // the NPCs-tab master list does.
        var apple = new Item { Id = 1, InternalName = "Apple", Name = "Apple" };
        var refData = new ItemSourceStub
        {
            ItemsByName = { ["Apple"] = apple },
            ItemSourcesByName =
            {
                ["Apple"] = new[] { new ItemSource("Vendor", "NPC_Joeh", null) },
            },
            NpcsByKey =
            {
                ["NPC_Joeh"] = new Mithril.Reference.Models.Npcs.Npc { Name = "Joeh of Serbule" },
            },
        };
        var vm = new ItemsTabViewModel(refData, new SilmarillionReferenceNavigator(
            new IReferenceKindTarget[] { new StubKindTarget(EntityKind.Npc) }),
            new ReferenceDataEntityNameResolver(refData));

        vm.SelectedItem = apple;

        vm.DetailViewModel!.Sources!.Single().DisplayName.Should().Be("Vendor: Joeh of Serbule");
    }

    [Fact]
    public void SourceChips_NonNpcSource_LeavesEntityReferenceNull_AndChipNotNavigable()
    {
        // Source kinds without an Npc field (Monster drop, Recipe, Quest, Skill, …) leave the
        // chip's EntityReference null so the UI renders them as plain text in a transparent frame.
        var apple = new Item { Id = 1, InternalName = "Apple", Name = "Apple" };
        var refData = new ItemSourceStub
        {
            ItemsByName = { ["Apple"] = apple },
            ItemSourcesByName =
            {
                ["Apple"] = new[]
                {
                    new ItemSource("Monster", null, "Hippo"),
                },
            },
        };
        var vm = new ItemsTabViewModel(refData, new SilmarillionReferenceNavigator(
            new IReferenceKindTarget[] { new StubKindTarget(EntityKind.Npc) }),
            new FakeEntityNameResolver());

        vm.SelectedItem = apple;

        var chip = vm.DetailViewModel!.Sources!.Single();
        chip.EntityReference.Should().BeNull();
        chip.IsNavigable.Should().BeFalse();
    }

    private sealed class StubKindTarget : IReferenceKindTarget
    {
        public StubKindTarget(EntityKind kind) => Kind = kind;
        public EntityKind Kind { get; }
        public int TabIndex => 0;
        public bool TrySelectByInternalName(string internalName) => true;
        public bool TryOpenInWindow() => false;
    }

    // Slim stub with ItemSources + NpcsByInternalName support — the main StubReferenceData omits both.
    private sealed class ItemSourceStub : IReferenceDataService
    {
        public Dictionary<string, Item> ItemsByName { get; } = new(StringComparer.Ordinal);
        public Dictionary<string, IReadOnlyList<ItemSource>> ItemSourcesByName { get; } = new(StringComparer.Ordinal);
        public Dictionary<string, Mithril.Reference.Models.Npcs.Npc> NpcsByKey { get; } = new(StringComparer.Ordinal);

        public IReadOnlyList<string> Keys { get; } = [];
        public IReadOnlyDictionary<long, Item> Items => ItemsByName.Values.ToDictionary(i => i.Id);
        public IReadOnlyDictionary<string, Item> ItemsByInternalName => ItemsByName;
        public ItemKeywordIndex KeywordIndex => new(new Dictionary<long, Item>());
        public IReadOnlyDictionary<string, Recipe> Recipes { get; } = new Dictionary<string, Recipe>();
        public IReadOnlyDictionary<string, Recipe> RecipesByInternalName { get; } = new Dictionary<string, Recipe>();
        public IReadOnlyDictionary<string, SkillEntry> Skills { get; } = new Dictionary<string, SkillEntry>();
        public IReadOnlyDictionary<string, XpTableEntry> XpTables { get; } = new Dictionary<string, XpTableEntry>();
        public IReadOnlyDictionary<string, NpcEntry> Npcs { get; } = new Dictionary<string, NpcEntry>();
        public IReadOnlyDictionary<string, Mithril.Reference.Models.Npcs.Npc> NpcsByInternalName => NpcsByKey;
        public IReadOnlyDictionary<string, AreaEntry> Areas { get; } = new Dictionary<string, AreaEntry>();
        public IReadOnlyDictionary<string, IReadOnlyList<ItemSource>> ItemSources => ItemSourcesByName;
        public IReadOnlyDictionary<string, AttributeEntry> Attributes { get; } = new Dictionary<string, AttributeEntry>();
        public IReadOnlyDictionary<string, PowerEntry> Powers { get; } = new Dictionary<string, PowerEntry>();
        public IReadOnlyDictionary<string, IReadOnlyList<string>> Profiles { get; } = new Dictionary<string, IReadOnlyList<string>>();
        public IReadOnlyDictionary<string, Mithril.Reference.Models.Quests.Quest> Quests { get; } = new Dictionary<string, Mithril.Reference.Models.Quests.Quest>();
        public IReadOnlyDictionary<string, Mithril.Reference.Models.Quests.Quest> QuestsByInternalName { get; } = new Dictionary<string, Mithril.Reference.Models.Quests.Quest>();
        public IReadOnlyDictionary<string, string> Strings { get; } = new Dictionary<string, string>();

        public ReferenceFileSnapshot GetSnapshot(string key) => new(key, ReferenceFileSource.Bundled, "test", null, 0);
        public Task RefreshAsync(string key, CancellationToken ct = default) => Task.CompletedTask;
        public Task RefreshAllAsync(CancellationToken ct = default) => Task.CompletedTask;
        public void BeginBackgroundRefresh() { }
        public event EventHandler<string>? FileUpdated { add { } remove { } }
    }

    [Fact]
    public void Keyword_chip_falls_back_to_CamelCaseSplit_when_no_friendly_display_name()
    {
        // Mirrors the GreenCrystal case: no recipe carries a friendly singleton Desc for the keyword,
        // so the map has no entry. CamelCaseSplit on the raw tag is the visible result.
        var item = new Item
        {
            Id = 99,
            InternalName = "Tourmaline",
            Name = "Tourmaline",
            Keywords = [new ItemKeyword("GreenCrystal", 0)],
        };
        var refData = new StubReferenceData
        {
            ItemsByName = { ["Tourmaline"] = item },
            KeywordsInRecipeSlots = new HashSet<string>(StringComparer.Ordinal) { "GreenCrystal" },
            // KeywordDisplays intentionally empty — no friendly display name available.
        };
        var vm = new ItemsTabViewModel(refData, new SilmarillionReferenceNavigator(Array.Empty<IReferenceKindTarget>()), new FakeEntityNameResolver());

        vm.SelectedItem = item;

        vm.DetailViewModel!.ConsumedAsKeywordIn.Single().DisplayName
            .Should().Be("Green Crystal",
                because: "CamelCaseSplit splits PascalCase tokens at the camel-hump");
    }

    private sealed class StubReferenceData : IReferenceDataService
    {
        public Dictionary<string, Item> ItemsByName { get; } = new(StringComparer.Ordinal);
        public Dictionary<string, IReadOnlyList<Recipe>> RecipesByIngredient { get; } = new(StringComparer.Ordinal);
        public IReadOnlyCollection<string> KeywordsInRecipeSlots { get; init; } = [];
        public IReadOnlyDictionary<string, string> KeywordDisplays { get; init; } = new Dictionary<string, string>(StringComparer.Ordinal);

        public IReadOnlyList<string> Keys { get; } = [];
        public IReadOnlyDictionary<long, Item> Items => ItemsByName.Values.ToDictionary(i => i.Id);
        public IReadOnlyDictionary<string, Item> ItemsByInternalName => ItemsByName;
        public IReadOnlyDictionary<string, IReadOnlyList<Recipe>> RecipesByIngredientItem => RecipesByIngredient;

        // #318 slice 4: an explicit override lets a test seed the provenance index
        // independently of RecipesByIngredient (Gate C — prove popup membership == index
        // membership with NO query in between). When unset, derive from the same
        // RecipesByIngredient fixture so existing setups feed the new popup-from-index
        // code path unchanged. Single reason (DirectIngredient), mirroring production.
        public Dictionary<string, IReadOnlyList<RecipeIngredientItemMatch>> RecipesByIngredientWithReasonOverride { get; }
            = new(StringComparer.Ordinal);

        public IReadOnlyDictionary<string, IReadOnlyList<RecipeIngredientItemMatch>> RecipesByIngredientItemWithReason =>
            RecipesByIngredientWithReasonOverride.Count > 0
                ? RecipesByIngredientWithReasonOverride
                : RecipesByIngredient.ToDictionary(
                    kv => kv.Key,
                    kv => (IReadOnlyList<RecipeIngredientItemMatch>)kv.Value
                        .Select(r => new RecipeIngredientItemMatch(r, RecipeIngredientItemMatchReason.DirectIngredient))
                        .ToList(),
                    StringComparer.Ordinal);
        public ItemKeywordIndex KeywordIndex => new(new Dictionary<long, Item>());
        public IReadOnlyDictionary<string, Recipe> Recipes { get; } = new Dictionary<string, Recipe>();
        public IReadOnlyDictionary<string, Recipe> RecipesByInternalName { get; } = new Dictionary<string, Recipe>();
        public IReadOnlyDictionary<string, SkillEntry> Skills { get; } = new Dictionary<string, SkillEntry>();
        public IReadOnlyDictionary<string, XpTableEntry> XpTables { get; } = new Dictionary<string, XpTableEntry>();
        public IReadOnlyDictionary<string, NpcEntry> Npcs { get; } = new Dictionary<string, NpcEntry>();
        public IReadOnlyDictionary<string, AreaEntry> Areas { get; } = new Dictionary<string, AreaEntry>();
        public IReadOnlyDictionary<string, IReadOnlyList<ItemSource>> ItemSources { get; } = new Dictionary<string, IReadOnlyList<ItemSource>>();
        public IReadOnlyDictionary<string, AttributeEntry> Attributes { get; } = new Dictionary<string, AttributeEntry>();
        public IReadOnlyDictionary<string, PowerEntry> Powers { get; } = new Dictionary<string, PowerEntry>();
        public IReadOnlyDictionary<string, IReadOnlyList<string>> Profiles { get; } = new Dictionary<string, IReadOnlyList<string>>();
        public IReadOnlyDictionary<string, Mithril.Reference.Models.Quests.Quest> Quests { get; } = new Dictionary<string, Mithril.Reference.Models.Quests.Quest>();
        public IReadOnlyDictionary<string, Mithril.Reference.Models.Quests.Quest> QuestsByInternalName { get; } = new Dictionary<string, Mithril.Reference.Models.Quests.Quest>();
        public IReadOnlyDictionary<string, string> Strings { get; } = new Dictionary<string, string>();

        public IReadOnlyCollection<string> KeywordsUsedInRecipeSlots => KeywordsInRecipeSlots;
        public IReadOnlyDictionary<string, string> KeywordDisplayNames => KeywordDisplays;

        public ReferenceFileSnapshot GetSnapshot(string key) => new(key, ReferenceFileSource.Bundled, "test", null, 0);
        public Task RefreshAsync(string key, CancellationToken ct = default) => Task.CompletedTask;
        public Task RefreshAllAsync(CancellationToken ct = default) => Task.CompletedTask;
        public void BeginBackgroundRefresh() { }
        public event EventHandler<string>? FileUpdated { add { } remove { } }
    }
}
