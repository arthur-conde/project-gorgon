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

    // ── #318 slice 4, surface 2 — Items "Used as" provenance popup ──────────────
    // The item-detail "Used as" surface (recipes that consume this item via a
    // keyword-ingredient slot) is now a recipe-chip cluster + ProvenancePopupViewModel
    // fed IReferenceDataService.RecipesByIngredientKeywordWithReason directly; the retired
    // per-keyword RecipeIngredientKeyword synthetic-kind ActionChips are gone. These tests
    // assert the #318 invariant: popup membership == index collection (unioned across the
    // item's keyword tags, deduped by recipe), "View all N" == distinct members,
    // single-reason ⇒ flat, and opening pushes no navigator history.

    [Fact]
    public void UsedAs_unionsRecipesAcrossItemKeywords_dedupedByRecipe_andBuildsFlatPopup()
    {
        // Massive Tourmaline carries Crystal + Gem; both tags map to recipes. A recipe
        // matching via *both* of the item's tags must appear once (deduped by recipe).
        var tourmaline = new Item
        {
            Id = 99,
            InternalName = "MassiveTourmaline",
            Name = "Massive Tourmaline",
            IconId = 0,
            Keywords = [new ItemKeyword("Crystal", 0), new ItemKeyword("Gem", 0), new ItemKeyword("Bogus", 0)],
        };
        var rEnchant = new Recipe { Key = "recipe_1", InternalName = "EnchantRing", Name = "Enchant Ring" };
        var rCut = new Recipe { Key = "recipe_2", InternalName = "CutGem", Name = "Cut Gem" };
        var rBoth = new Recipe { Key = "recipe_3", InternalName = "FuseGemstone", Name = "Fuse Gemstone" };
        var refData = new StubReferenceData
        {
            ItemsByName = { ["MassiveTourmaline"] = tourmaline },
            // rBoth qualifies via both Crystal and Gem — must be carried once.
            RecipesByIngredientKeyword =
            {
                ["Crystal"] = new[] { rEnchant, rBoth },
                ["Gem"] = new[] { rCut, rBoth },
                // "Bogus" is one of the item's keywords but maps to no recipe slot.
            },
        };
        var vm = new ItemsTabViewModel(refData, NavWithRecipe(), new FakeEntityNameResolver(),
            new SilmarillionSettings { UsedInChipCap = 12 });

        vm.SelectedItem = tourmaline;
        var detail = vm.DetailViewModel!;

        detail.ConsumedAsKeywordIn.Should().HaveCount(3);
        detail.ConsumedAsKeywordInPopup.Should().NotBeNull();
        var popup = detail.ConsumedAsKeywordInPopup!;
        popup.TotalCount.Should().Be(3, because: "FuseGemstone matched via two of the item's tags but is one member");
        detail.ConsumedAsKeywordInTotal.Should().Be(3);
        detail.ShowConsumedAsKeywordInPopupCommand.CanExecute(null).Should().BeTrue();
        popup.IsFlat.Should().BeTrue(because: "one trivial reason (KeywordIngredientSlot) is noise — collapse to flat");
        popup.FlatChips.Select(c => c.Reference.InternalName).Should().BeEquivalentTo(
            new[] { "EnchantRing", "CutGem", "FuseGemstone" },
            because: "popup membership == the unioned, deduped index collection (no second derivation)");
    }

    [Fact]
    public void UsedAs_itemWithNoMatchingKeyword_popupIsNull_andSectionHidden()
    {
        var item = new Item
        {
            Id = 99,
            InternalName = "Tourmaline",
            Name = "Tourmaline",
            Keywords = [new ItemKeyword("Bogus", 0)],
        };
        var refData = new StubReferenceData
        {
            ItemsByName = { ["Tourmaline"] = item },
            RecipesByIngredientKeyword = { ["Crystal"] = new[] { new Recipe { Key = "r", InternalName = "X", Name = "X" } } },
        };
        var vm = new ItemsTabViewModel(refData, NavWithRecipe(), new FakeEntityNameResolver(),
            new SilmarillionSettings { UsedInChipCap = 12 });

        vm.SelectedItem = item;
        var detail = vm.DetailViewModel!;

        detail.ConsumedAsKeywordIn.Should().BeEmpty();
        detail.ConsumedAsKeywordInPopup.Should().BeNull(
            because: "no recipe consumes the item via any of its keyword tags — the 'Used as' section hides");
        detail.ConsumedAsKeywordInTotal.Should().Be(0);
        detail.ShowConsumedAsKeywordInPopupCommand.CanExecute(null).Should().BeFalse();
    }

    [Fact]
    public void UsedAs_aboveCap_capsChips_butPopupCarriesFullDistinctSet()
    {
        var item = new Item
        {
            Id = 1,
            InternalName = "CommonReagent",
            Name = "Common Reagent",
            Keywords = [new ItemKeyword("Reagent", 0)],
        };
        var recipes = MakeRecipeList(count: 40);
        var refData = new StubReferenceData
        {
            ItemsByName = { ["CommonReagent"] = item },
            RecipesByIngredientKeyword = { ["Reagent"] = recipes },
        };
        var vm = new ItemsTabViewModel(refData, NavWithRecipe(), new FakeEntityNameResolver(),
            new SilmarillionSettings { UsedInChipCap = 12 });

        vm.SelectedItem = item;
        var detail = vm.DetailViewModel!;

        detail.ConsumedAsKeywordIn.Should().HaveCount(12,
            because: "cap 12 means the first dozen show as chips; the rest are reachable via the popup");
        var popup = detail.ConsumedAsKeywordInPopup!;
        // The defining #318 assertion: "View all N" == distinct index members, NOT the cap.
        popup.TotalCount.Should().Be(40);
        detail.ConsumedAsKeywordInTotal.Should().Be(40);
        popup.FlatChips.Should().HaveCount(40,
            because: "the popup carries the full set fed from the index, not the capped cluster");
    }

    [Fact]
    public void UsedAs_recipeQualifyingOnlyViaNonPrimaryIndexEntry_appearsInPopup_withCorrectProvenance()
    {
        // #318 Gate C — the load-bearing regression that would have caught the original
        // dual-derivation bug for THIS surface. The pre-#318 "Used as" surface rendered
        // per-keyword chips that deep-linked via RecipeIngredientKeyword, whose kind target
        // re-derived the set as the query `IngredientKeywords CONTAINS "<tag>"`. If the
        // index ever carried a member the query string did NOT, the chip opened a list
        // missing it. The popup-from-index has no query between the set and the screen, so
        // a member present ONLY in the index must still appear — with its
        // KeywordIngredientSlot provenance — and the count must equal the distinct index
        // membership exactly. Here `direct` is the obvious member; `indexOnly` is seeded
        // into the provenance index for a tag with NO matching recipe re-derivable from a
        // naive keyword query (the non-primary analogue) and must still surface.
        var item = new Item
        {
            Id = 1,
            InternalName = "Widget",
            Name = "Widget",
            Keywords = [new ItemKeyword("Alpha", 0), new ItemKeyword("Beta", 0)],
        };
        var direct = new Recipe { Key = "recipe_001", InternalName = "DirectUser", Name = "Direct User" };
        var indexOnly = new Recipe { Key = "recipe_002", InternalName = "IndexOnlyUser", Name = "Index Only User" };
        var refData = new StubReferenceData
        {
            ItemsByName = { ["Widget"] = item },
            // Seed the provenance index directly. `direct` is reachable for tag Alpha;
            // `indexOnly` only via tag Beta — both must appear, deduped, in the popup,
            // independent of any query.
            RecipesByIngredientKeywordWithReasonOverride =
            {
                ["Alpha"] = new[]
                {
                    new RecipeIngredientKeywordMatch(direct, RecipeIngredientKeywordMatchReason.KeywordIngredientSlot),
                },
                ["Beta"] = new[]
                {
                    new RecipeIngredientKeywordMatch(indexOnly, RecipeIngredientKeywordMatchReason.KeywordIngredientSlot),
                },
            },
        };
        var vm = new ItemsTabViewModel(refData, NavWithRecipe(), new FakeEntityNameResolver(),
            new SilmarillionSettings { UsedInChipCap = 12 });

        vm.SelectedItem = item;
        var popup = vm.DetailViewModel!.ConsumedAsKeywordInPopup;

        popup.Should().NotBeNull();
        popup!.TotalCount.Should().Be(2, because: "View all N == distinct index members across the item's tags");
        popup.IsFlat.Should().BeTrue(because: "single reason (KeywordIngredientSlot) ⇒ flat list");
        popup.FlatChips.Select(c => c.Reference.InternalName).Should().BeEquivalentTo(
            new[] { "DirectUser", "IndexOnlyUser" },
            because: "every index member appears — there is no query that could drop one");
        popup.Sections.Should().ContainSingle().Which.Label.Should().Be("Used as");
        popup.Sections.Single().Chips.Select(c => c.Reference.InternalName).Should()
            .BeEquivalentTo(new[] { "DirectUser", "IndexOnlyUser" });
    }

    [Fact]
    public void UsedAs_openingPopup_doesNotTouchNavigator_noHistoryPushed()
    {
        // #318 — opening the popup must NOT push navigator back/forward history (the #229
        // non-navigating contract). Swap the opener for a capturing no-op so no window
        // spawns; assert navigator state is pristine before and after.
        var item = new Item
        {
            Id = 1,
            InternalName = "MassiveTourmaline",
            Name = "Massive Tourmaline",
            Keywords = [new ItemKeyword("Crystal", 0)],
        };
        var refData = new StubReferenceData
        {
            ItemsByName = { ["MassiveTourmaline"] = item },
            RecipesByIngredientKeyword = { ["Crystal"] = MakeRecipeList(count: 4) },
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

            detail.ShowConsumedAsKeywordInPopupCommand.Execute(null);

            captured.Should().NotBeNull(because: "the command invoked the opener with the built popup VM");
            nav.Current.Should().BeNull();
            nav.CanGoBack.Should().BeFalse();
            nav.CanGoForward.Should().BeFalse();
        }
        finally
        {
            ItemDetailViewModel.ProvenancePopupOpener = prior;
        }
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
        // Non-entity-resolving source kinds (Monster drop, Skill, plain-text kinds) leave the
        // chip's EntityReference null so the UI renders them as plain text in a transparent
        // frame. (Recipe/Quest DO resolve to an EntityRef now and are either suppressed as a
        // reverse-twin dup or kept as declared-only residue — see the #407 tests below.)
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

    // ── #407 declared-vs-reverse source-duplication policy ─────────────────────
    // Ratified policy (docs/silmarillion-field-coverage.md §#407): suppress a
    // declared Recipe/Quest "Sources" row per (item,entity) edge when the same
    // entity is already shown under its reverse header; declared-only residue
    // survives prefix-less with an in-pane asymmetry warning (the ProvenanceSuffix
    // slot, i.e. ItemSourceChipVm.Detail). Projection-layer only.

    [Fact]
    public void Source407_RecipeDeclared_AlsoProducedBy_IsSuppressedFromSources()
    {
        var apple = new Item { Id = 1, InternalName = "Apple", Name = "Apple" };
        var refData = new ItemSourceStub
        {
            ItemsByName = { ["Apple"] = apple },
            ItemSourcesByName = { ["Apple"] = new[] { new ItemSource("Recipe", null, "MakeApple") } },
            ProducedByByName =
            {
                ["Apple"] = new[] { new Recipe { Key = "recipe_1", InternalName = "MakeApple", Name = "Make Apple" } },
            },
        };
        var vm = new ItemsTabViewModel(refData, NavWithRecipe(), new FakeEntityNameResolver());

        vm.SelectedItem = apple;

        // The only declared source is the reverse-twin dup → "Sources" collapses to null;
        // the entity still appears, once, under its dedicated "Produced by" header.
        vm.DetailViewModel!.Sources.Should().BeNullOrEmpty();
        vm.DetailViewModel!.ProducedByRecipes.Select(c => c.DisplayName).Should().ContainSingle()
            .Which.Should().Be("Make Apple");
    }

    [Fact]
    public void Source407_QuestDeclared_AlsoAwardedBy_IsSuppressedFromSources()
    {
        var slab = new Item { Id = 1, InternalName = "MetalSlab1", Name = "Simple Metal Slab" };
        var refData = new ItemSourceStub
        {
            ItemsByName = { ["MetalSlab1"] = slab },
            ItemSourcesByName =
            {
                ["MetalSlab1"] = new[]
                {
                    new ItemSource("Quest", null, "AllTheFishYouCouldWant"),
                    new ItemSource("Vendor", "NPC_Way", null),
                },
            },
            AwardedByByName =
            {
                ["MetalSlab1"] = new[]
                {
                    new Mithril.Reference.Models.Quests.Quest
                    { InternalName = "AllTheFishYouCouldWant", Name = "All The Fish You Could Want" },
                },
            },
        };
        var vm = new ItemsTabViewModel(refData, new SilmarillionReferenceNavigator(
            new IReferenceKindTarget[] { new StubKindTarget(EntityKind.Npc) }),
            new FakeEntityNameResolver());

        vm.SelectedItem = slab;

        // The Quest dup is gone from Sources; the NPC-mechanic row survives with its
        // (deliberately kept) acquisition-method prefix. The quest stays under "Awarded by".
        vm.DetailViewModel!.Sources!.Select(c => c.DisplayName).Should().ContainSingle()
            .Which.Should().Be("Vendor: NPC_Way");
        // FakeEntityNameResolver (no map) echoes the InternalName — the assertion is
        // that the quest is *present* under "Awarded by", not its friendly form.
        vm.DetailViewModel!.AwardedByQuests.Select(c => c.Reference)
            .Should().Equal(EntityRef.Quest("AllTheFishYouCouldWant"));
    }

    [Fact]
    public void Source407_RecipeDeclaredOnly_NoReverseTwin_SurvivesPrefixlessWithAsymmetryWarning()
    {
        var boots = new Item { Id = 1, InternalName = "CraftedSnailBoots12", Name = "Snail Boots" };
        var refData = new ItemSourceStub
        {
            ItemsByName = { ["CraftedSnailBoots12"] = boots },
            ItemSourcesByName =
            {
                ["CraftedSnailBoots12"] = new[] { new ItemSource("Recipe", null, "CraftedSnailBoots12") },
            },
            // ProducedByByName intentionally empty — the declared-only residue case.
        };
        var vm = new ItemsTabViewModel(refData, NavWithRecipe(), new FakeEntityNameResolver());

        vm.SelectedItem = boots;

        var chip = vm.DetailViewModel!.Sources!.Single();
        // No "Recipe:" prefix — kind is carried by the Link lead-glyph standard.
        chip.DisplayName.Should().Be("CraftedSnailBoots12");
        // G-d (#431): the caveat is the Unconfirmed reference state, NOT an
        // overloaded provenance suffix. Detail (→ ProvenanceSuffix) stays free.
        chip.Detail.Should().BeNull();
        chip.IsUnconfirmed.Should().BeTrue();
        chip.UnconfirmedTooltip.Should().Contain("does not list this item among its products");
        chip.EntityReference.Should().Be(EntityRef.Recipe("CraftedSnailBoots12"));
    }

    [Fact]
    public void Source407_QuestDeclaredOnly_NoReverseTwin_SurvivesPrefixlessWithAsymmetryWarning()
    {
        var seed = new Item { Id = 1, InternalName = "CarrotSeedling", Name = "Carrot Seedling" };
        var refData = new ItemSourceStub
        {
            ItemsByName = { ["CarrotSeedling"] = seed },
            ItemSourcesByName =
            {
                ["CarrotSeedling"] = new[] { new ItemSource("Quest", null, "LiveEvent_BunFu_Flopsy") },
            },
        };
        var vm = new ItemsTabViewModel(refData, new SilmarillionReferenceNavigator(
            Array.Empty<IReferenceKindTarget>()), new FakeEntityNameResolver());

        vm.SelectedItem = seed;

        var chip = vm.DetailViewModel!.Sources!.Single();
        chip.DisplayName.Should().Be("LiveEvent_BunFu_Flopsy");
        chip.Detail.Should().BeNull();
        chip.IsUnconfirmed.Should().BeTrue();
        chip.UnconfirmedTooltip.Should().Contain("does not list this item among its rewards");
        chip.EntityReference.Should().Be(EntityRef.Quest("LiveEvent_BunFu_Flopsy"));
    }

    [Fact]
    public void Source407_PartialOverlap_DupSuppressed_ResidueKept_PerEdge()
    {
        // Two declared Recipe sources for the same item: one has a reverse twin
        // (suppressed), one does not (kept as residue). Proves the test is per-edge.
        var apple = new Item { Id = 1, InternalName = "Apple", Name = "Apple" };
        var refData = new ItemSourceStub
        {
            ItemsByName = { ["Apple"] = apple },
            ItemSourcesByName =
            {
                ["Apple"] = new[]
                {
                    new ItemSource("Recipe", null, "MakeApple"),     // reverse twin → suppressed
                    new ItemSource("Recipe", null, "OrphanApple"),   // no reverse twin → residue
                },
            },
            ProducedByByName =
            {
                ["Apple"] = new[] { new Recipe { Key = "recipe_1", InternalName = "MakeApple", Name = "Make Apple" } },
            },
        };
        var vm = new ItemsTabViewModel(refData, NavWithRecipe(), new FakeEntityNameResolver());

        vm.SelectedItem = apple;

        var sources = vm.DetailViewModel!.Sources!;
        sources.Should().ContainSingle();
        sources[0].EntityReference.Should().Be(EntityRef.Recipe("OrphanApple"));
        sources[0].Detail.Should().BeNull();
        sources[0].IsUnconfirmed.Should().BeTrue();
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
        // #407: reverse-header membership. When seeded, BuildSourceChips suppresses a
        // declared Recipe/Quest "Sources" row whose Context is present here (the
        // per-edge dedupe); unseeded entries default to the interface's empty index.
        public Dictionary<string, IReadOnlyList<Recipe>> ProducedByByName { get; } = new(StringComparer.Ordinal);
        public Dictionary<string, IReadOnlyList<Mithril.Reference.Models.Quests.Quest>> AwardedByByName { get; } = new(StringComparer.Ordinal);
        public IReadOnlyDictionary<string, IReadOnlyList<Recipe>> RecipesByProducedItem => ProducedByByName;
        public IReadOnlyDictionary<string, IReadOnlyList<Mithril.Reference.Models.Quests.Quest>> QuestsRewardingItem => AwardedByByName;

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

    // Keyword_chip_falls_back_to_CamelCaseSplit_when_no_friendly_display_name was removed
    // in #318 slice 4 (surface 2): the "Used as" section no longer renders per-keyword
    // chips (whose label was the keyword's friendly/CamelCaseSplit display name) — it
    // renders the recipe-chip cluster + provenance popup fed
    // RecipesByIngredientKeywordWithReason. Recipe display-name resolution is covered by
    // the surface-1 "Used in" tests; keyword-display-name resolution
    // (KeywordDisplayNames / CamelCaseSplit) is still exercised where it remains live
    // (recipe-detail keyword chips).

    private sealed class StubReferenceData : IReferenceDataService
    {
        public Dictionary<string, Item> ItemsByName { get; } = new(StringComparer.Ordinal);
        public Dictionary<string, IReadOnlyList<Recipe>> RecipesByIngredient { get; } = new(StringComparer.Ordinal);
        // #318 slice 4, surface 2: keyword tag → recipes matching that tag via a keyword
        // slot. Convenience: when set, the provenance index (RecipesByIngredientKeyword-
        // WithReason) is derived from it with the single KeywordIngredientSlot reason,
        // mirroring production. For Gate C use RecipesByIngredientKeywordWithReasonOverride
        // to seed the provenance index independently (prove popup == index, no query).
        public Dictionary<string, IReadOnlyList<Recipe>> RecipesByIngredientKeyword { get; } = new(StringComparer.Ordinal);
        public Dictionary<string, IReadOnlyList<RecipeIngredientKeywordMatch>> RecipesByIngredientKeywordWithReasonOverride { get; }
            = new(StringComparer.Ordinal);
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

        public IReadOnlyDictionary<string, IReadOnlyList<RecipeIngredientKeywordMatch>> RecipesByIngredientKeywordWithReason =>
            RecipesByIngredientKeywordWithReasonOverride.Count > 0
                ? RecipesByIngredientKeywordWithReasonOverride
                : RecipesByIngredientKeyword.ToDictionary(
                    kv => kv.Key,
                    kv => (IReadOnlyList<RecipeIngredientKeywordMatch>)kv.Value
                        .Select(r => new RecipeIngredientKeywordMatch(r, RecipeIngredientKeywordMatchReason.KeywordIngredientSlot))
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
