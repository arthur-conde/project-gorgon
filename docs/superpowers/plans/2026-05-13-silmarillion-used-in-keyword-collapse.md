# Silmarillion "Used in" — Keyword-Collapse Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Collapse the 500+ keyword-matched recipe chips currently surfaced in the Silmarillion item-detail "Used in" section into ≤ N keyword chips (one per item-keyword that's actually used in some recipe slot), with each keyword chip deep-linking to a Recipes-tab view filtered via `IngredientKeywords CONTAINS "<keyword>"`. Amends in-flight PR #259.

**Architecture:** Reverts PR #259's widening of `RecipesByIngredientItem` (back to direct ItemCode refs only) and replaces it with two layers: (1) a new flat `IReadOnlyCollection<string> KeywordsUsedInRecipeSlots` on `IReferenceDataService` so the item-detail VM can filter `item.Keywords` to "keywords that point somewhere"; (2) a new `EntityKind.RecipeIngredientKeyword` deep-link target whose dispatch flips to the Recipes tab and pre-populates `QueryText` using #261's collection-`CONTAINS`-via-`IQueryStringValue` mechanism. `RecipeListRow` gains a flat queryable `IngredientKeywords` collection so the pre-filled query actually matches.

**Tech Stack:** .NET 10, WPF, CommunityToolkit.Mvvm, xunit + FluentAssertions, `Microsoft.Extensions.DependencyInjection`. Builds on PR #261 (`IQueryStringValue` + collection-CONTAINS in `QueryCompiler`).

**Spec reference:** [docs/superpowers/specs/2026-05-13-silmarillion-used-in-keyword-collapse-design.md](../specs/2026-05-13-silmarillion-used-in-keyword-collapse-design.md)

---

## File Structure

**New files:**
- `src/Silmarillion.Module/ViewModels/IngredientKeywordValue.cs` — wrapper record `(string Tag) : IQueryStringValue`; makes a flat list of ingredient-keyword strings queryable via `IngredientKeywords CONTAINS "<tag>"`.
- `src/Silmarillion.Module/Navigation/RecipeIngredientKeywordKindTarget.cs` — `IReferenceKindTarget` for the new EntityKind variant; flips to Recipes tab + sets `QueryText` to the pre-filled query.

**Modified files:**
- `src/Mithril.Shared/Reference/EntityRef.cs` — add `EntityKind.RecipeIngredientKeyword` + `EntityRef.RecipeIngredientKeyword(string)` factory.
- `src/Mithril.Shared/Reference/IReferenceDataService.cs` — add `KeywordsUsedInRecipeSlots` property; revert `RecipesByIngredientItem` docstring to direct-refs-only wording.
- `src/Mithril.Shared/Reference/ReferenceDataService.cs` — `BuildRecipeCrossLinkIndices`: drop PR #259's keyword fan-out; build new keyword set in same walk. Add backing `_keywordsUsedInRecipeSlots` field + property override.
- `src/Mithril.Shared.Wpf/ItemDetailContext.cs` — add `ConsumedAsKeywordIn` parameter.
- `src/Mithril.Shared.Wpf/ItemDetailViewModel.cs` — surface new `ConsumedAsKeywordIn` collection from context.
- `src/Mithril.Shared.Wpf/ItemDetailView.xaml` — header `(N)` count suffix on "Used in"; new "Used as" section between "Used in" and "Skill requirements".
- `src/Silmarillion.Module/ViewModels/RecipeListRow.cs` — add `IngredientKeywords` property.
- `src/Silmarillion.Module/ViewModels/RecipesTabViewModel.cs` — `BuildAllRecipes` populates `IngredientKeywords` by flattening `RecipeKeywordIngredient.ItemKeys`.
- `src/Silmarillion.Module/ViewModels/ItemsTabViewModel.cs` — `BuildCrossLinkContext` populates `ConsumedAsKeywordIn` (intersect `item.Keywords` with `KeywordsUsedInRecipeSlots`).
- `src/Silmarillion.Module/SilmarillionModule.cs` — register `RecipeIngredientKeywordKindTarget` in DI alongside `ItemsKindTarget` / `RecipesKindTarget`.
- `tests/Mithril.Shared.Tests/ReferenceDataServiceTests.cs` — replace PR #259's three keyword-expansion tests with `KeywordsUsedInRecipeSlots` tests.
- `tests/Mithril.Shared.Tests/Reference/ReferenceDataServiceRecipeCrossLinkIndexTests.cs` — restore (or write fresh) "direct-refs-only" assertion that mirrors the test PR #259 had deleted.
- `tests/Silmarillion.Tests/ViewModels/ItemsTabViewModelTests.cs` — cover `ConsumedAsKeywordIn` projection.
- `tests/Silmarillion.Tests/ViewModels/RecipesTabViewModelTests.cs` — cover `RecipeListRow.IngredientKeywords` projection + a query-roundtrip test.
- `tests/Silmarillion.Tests/Navigation/SilmarillionReferenceNavigatorTests.cs` — cover the new kind-target dispatch.

**Deleted files:**
- `docs/agent-plans/silmarillion-254-widen-recipes-by-ingredient.md` — superseded by the new spec; replace with a one-liner pointer in the PR description.

---

## Task 0: Rebase onto origin/main

The branch is currently one commit behind `origin/main` (missing PR #261, which adds `IQueryStringValue` + collection-CONTAINS). This plan depends on those primitives.

**Files:** none (rebase only).

- [ ] **Step 1: Verify divergence**

```bash
git fetch origin
git log --oneline HEAD..origin/main
```

Expected: one commit on `origin/main` not on `HEAD`: `883e8cb feat(query): CONTAINS over collections of IQueryStringValue elements (#260) (#261)`.

- [ ] **Step 2: Rebase**

```bash
git rebase origin/main
```

Expected: clean rebase. The PR #259 commit `f43dac1 feat(mithril.reference): widen RecipesByIngredientItem to include keyword-matched recipes` replays cleanly on top of #261 (different files touched).

If conflicts arise (unexpected), abort and ask the user.

- [ ] **Step 3: Verify build still green**

```bash
dotnet build Mithril.slnx
```

Expected: 0 errors, 0 warnings.

- [ ] **Step 4: No commit needed**

Rebase rewrites history; the push at the very end of this plan does `--force-with-lease`.

---

## Task 1: EntityKind.RecipeIngredientKeyword variant

Adds the new enum value + factory. No behaviour yet — just the type for downstream tasks to reference.

**Files:**
- Modify: `src/Mithril.Shared/Reference/EntityRef.cs`
- Test: `tests/Mithril.Shared.Tests/Reference/EntityRefTests.cs` (create if absent; otherwise append)

- [ ] **Step 1: Write the failing test**

If `EntityRefTests.cs` doesn't exist, create it:

```csharp
using FluentAssertions;
using Mithril.Shared.Reference;
using Xunit;

namespace Mithril.Shared.Tests.Reference;

public class EntityRefTests
{
    [Fact]
    public void RecipeIngredientKeyword_factory_produces_expected_kind_and_internalname()
    {
        var reference = EntityRef.RecipeIngredientKeyword("Crystal");

        reference.Kind.Should().Be(EntityKind.RecipeIngredientKeyword);
        reference.InternalName.Should().Be("Crystal");
    }
}
```

- [ ] **Step 2: Run the failing test**

```bash
dotnet test tests/Mithril.Shared.Tests --filter "FullyQualifiedName~EntityRefTests"
```

Expected: FAIL with `EntityKind.RecipeIngredientKeyword` / `EntityRef.RecipeIngredientKeyword` not defined (CS0117).

- [ ] **Step 3: Implement**

In `src/Mithril.Shared/Reference/EntityRef.cs`, append `RecipeIngredientKeyword` to the `EntityKind` enum (last entry), then add the factory:

```csharp
public enum EntityKind
{
    Item,
    Recipe,
    Ability,
    Effect,
    Npc,
    Quest,
    Lorebook,
    Landmark,
    Area,
    PlayerTitle,
    StorageVault,
    /// <summary>
    /// Not an entity per se — a deep-link target for "open the Recipes tab filtered to recipes
    /// whose ingredient list mentions this keyword tag." InternalName carries the keyword
    /// (e.g. "Crystal"). Dispatched by RecipeIngredientKeywordKindTarget.
    /// </summary>
    RecipeIngredientKeyword,
}
```

And in the factory section:

```csharp
public static EntityRef RecipeIngredientKeyword(string keyword) =>
    new(EntityKind.RecipeIngredientKeyword, keyword);
```

- [ ] **Step 4: Run the test**

```bash
dotnet test tests/Mithril.Shared.Tests --filter "FullyQualifiedName~EntityRefTests"
```

Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add src/Mithril.Shared/Reference/EntityRef.cs tests/Mithril.Shared.Tests/Reference/EntityRefTests.cs
git commit -m "feat(mithril.reference): add EntityKind.RecipeIngredientKeyword variant"
```

---

## Task 2: Revert PR #259's widening + add KeywordsUsedInRecipeSlots

Replaces the merged-list approach with a flat keyword set.

**Files:**
- Modify: `src/Mithril.Shared/Reference/IReferenceDataService.cs`
- Modify: `src/Mithril.Shared/Reference/ReferenceDataService.cs`
- Modify: `tests/Mithril.Shared.Tests/ReferenceDataServiceTests.cs` (delete PR #259 tests, add new ones)
- Modify: `tests/Mithril.Shared.Tests/Reference/ReferenceDataServiceRecipeCrossLinkIndexTests.cs` (restore direct-refs-only assertion)

- [ ] **Step 1: Delete the PR #259 keyword-expansion tests**

In `tests/Mithril.Shared.Tests/ReferenceDataServiceTests.cs`, delete the three methods added in PR #259:
- `RecipesByIngredientItem_expands_keyword_ingredients_to_matching_items`
- `RecipesByIngredientItem_keyword_expansion_respects_AND_match`
- `RecipesByIngredientItem_dedupes_when_recipe_references_same_item_directly_and_by_keyword`

- [ ] **Step 2: Restore direct-refs-only test**

In `tests/Mithril.Shared.Tests/Reference/ReferenceDataServiceRecipeCrossLinkIndexTests.cs`, restore the test that PR #259 deleted (the one named `RecipesByIngredientItem_DoesNotIndexKeywordIngredients`), keeping the same fixture and assertion:

```csharp
[Fact]
public void RecipesByIngredientItem_DoesNotIndexKeywordIngredients()
{
    // Keyword ingredients are kind-based (e.g. any "Crystal") and don't map to a single
    // InternalName. Surfacing them as item-keyed entries would flood the reverse index;
    // they live on KeywordsUsedInRecipeSlots instead.
    WriteFixture(
        itemsJson: """
        {
          "item_100": { "Name": "Boots", "InternalName": "Boots" },
          "item_200": { "Name": "Rough Crystal", "InternalName": "RoughCrystal", "Keywords": ["Crystal"] }
        }
        """,
        recipesJson: """
        {
          "recipe_1": {
            "Name": "Enchant Boots",
            "InternalName": "EnchantBoots",
            "Skill": "Leatherworking",
            "Ingredients": [
              { "ItemCode": 100, "StackSize": 1 },
              { "Desc": "Aux Crystal", "ItemKeys": ["Crystal"], "StackSize": 1 }
            ]
          }
        }
        """);

    var svc = new ReferenceDataService(_cacheDir, NeverCallHttp(), bundledDir: _bundledDir);

    svc.RecipesByIngredientItem.Should().ContainKey("Boots");
    svc.RecipesByIngredientItem.Should().NotContainKey("RoughCrystal",
        because: "keyword-matched usage lives in KeywordsUsedInRecipeSlots, not the per-item dictionary");
}
```

- [ ] **Step 3: Write the failing tests for `KeywordsUsedInRecipeSlots`**

Add to `tests/Mithril.Shared.Tests/Reference/ReferenceDataServiceRecipeCrossLinkIndexTests.cs`:

```csharp
[Fact]
public void KeywordsUsedInRecipeSlots_collects_distinct_tags_across_all_RecipeKeywordIngredient_slots()
{
    WriteFixture(
        itemsJson: """
        { "item_100": { "Name": "Boots", "InternalName": "Boots" } }
        """,
        recipesJson: """
        {
          "recipe_singleton": {
            "Name": "R1", "InternalName": "R1", "Skill": "Leatherworking",
            "Ingredients": [
              { "ItemCode": 100, "StackSize": 1 },
              { "Desc": "Aux Crystal", "ItemKeys": ["Crystal"], "StackSize": 1 }
            ]
          },
          "recipe_tuple": {
            "Name": "R2", "InternalName": "R2", "Skill": "Leatherworking",
            "Ingredients": [
              { "Desc": "T2", "ItemKeys": ["Crystal", "Tier2"], "StackSize": 1 }
            ]
          }
        }
        """);

    var svc = new ReferenceDataService(_cacheDir, NeverCallHttp(), bundledDir: _bundledDir);

    svc.KeywordsUsedInRecipeSlots.Should().BeEquivalentTo(["Crystal", "Tier2"]);
}

[Fact]
public void KeywordsUsedInRecipeSlots_is_empty_when_no_recipes_reference_keyword_slots()
{
    WriteFixture(
        itemsJson: """{ "item_100": { "Name": "X", "InternalName": "X" } }""",
        recipesJson: """
        {
          "recipe_1": {
            "Name": "R1", "InternalName": "R1", "Skill": "Cooking",
            "Ingredients": [ { "ItemCode": 100, "StackSize": 1 } ]
          }
        }
        """);

    var svc = new ReferenceDataService(_cacheDir, NeverCallHttp(), bundledDir: _bundledDir);

    svc.KeywordsUsedInRecipeSlots.Should().BeEmpty();
}
```

- [ ] **Step 4: Run the failing tests**

```bash
dotnet test tests/Mithril.Shared.Tests --filter "FullyQualifiedName~ReferenceDataServiceRecipeCrossLinkIndexTests"
```

Expected: FAIL on the three new tests with "KeywordsUsedInRecipeSlots not found" (CS1061). The reverted `RecipesByIngredientItem_DoesNotIndexKeywordIngredients` will also FAIL because PR #259 still has the widening logic in place.

- [ ] **Step 5: Update the interface**

In `src/Mithril.Shared/Reference/IReferenceDataService.cs`, replace the PR #259-widened `RecipesByIngredientItem` docstring and add the new property. The full updated block:

```csharp
/// <summary>
/// Recipes indexed by the InternalName of any item they consume as an ingredient via a
/// direct <see cref="RecipeItemIngredient"/>. <see cref="RecipeKeywordIngredient"/> slots
/// are kind-based (e.g. "any Crystal") and don't map to a single InternalName — they're
/// surfaced through <see cref="KeywordsUsedInRecipeSlots"/> instead. Built whenever
/// items.json or recipes.json reloads. Defaults to empty so test fakes don't need to opt
/// into cross-linking.
/// </summary>
IReadOnlyDictionary<string, IReadOnlyList<Recipe>> RecipesByIngredientItem => EmptyRecipeIndex;

/// <summary>
/// The flat set of every distinct keyword tag that appears in any
/// <see cref="RecipeKeywordIngredient.ItemKeys"/> across all recipes. Powers the item-detail
/// "Used as" section: an item's chip set is <c>item.Keywords ∩ KeywordsUsedInRecipeSlots</c>,
/// so chips only appear for keywords that actually lead to at least one recipe. Built
/// whenever recipes.json reloads. Defaults to empty so test fakes don't need to opt in.
/// </summary>
IReadOnlyCollection<string> KeywordsUsedInRecipeSlots => Array.Empty<string>();
```

- [ ] **Step 6: Revert the widening and build the new set**

In `src/Mithril.Shared/Reference/ReferenceDataService.cs`:

1. Add a backing field next to `_recipesByIngredientItem`:

```csharp
private IReadOnlyCollection<string> _keywordsUsedInRecipeSlots = Array.Empty<string>();
```

2. Add the property override (placement: near other interface-property impls, e.g. directly under whatever line currently exposes `RecipesByIngredientItem`):

```csharp
public IReadOnlyCollection<string> KeywordsUsedInRecipeSlots => _keywordsUsedInRecipeSlots;
```

3. In `BuildRecipeCrossLinkIndices`, replace the entire body of the `foreach (var ing in ingredients)` switch with the pre-PR-#259 direct-refs-only loop, and accumulate keyword tags into a `HashSet<string>` in the outer scope. Concretely, the ingredients-side block becomes:

```csharp
// recipe.Ingredients is annotated non-nullable but JSON deserialization with a missing
// field yields null at runtime — guard rather than crash.
var ingredients = recipe.Ingredients ?? (IReadOnlyList<Mithril.Reference.Models.Recipes.RecipeIngredient>)Array.Empty<Mithril.Reference.Models.Recipes.RecipeIngredient>();
foreach (var ing in ingredients)
{
    switch (ing)
    {
        case Mithril.Reference.Models.Recipes.RecipeItemIngredient itemIng:
            if (_items.TryGetValue(itemIng.ItemCode, out var item) && !string.IsNullOrEmpty(item.InternalName))
                AddIngredientRecipe(ingredient, item.InternalName, recipe);
            break;

        case Mithril.Reference.Models.Recipes.RecipeKeywordIngredient kwIng:
            foreach (var key in kwIng.ItemKeys)
                keywordSet.Add(key);
            break;
    }
}
```

Where `keywordSet` is declared at the top of the method, alongside `produced` and `ingredient`:

```csharp
var produced = new Dictionary<string, List<Recipe>>(StringComparer.Ordinal);
var ingredient = new Dictionary<string, List<Recipe>>(StringComparer.Ordinal);
var keywordSet = new HashSet<string>(StringComparer.Ordinal);
```

And at the bottom of the method, alongside the two swap lines:

```csharp
_recipesByProducedItem = produced.ToDictionary(kv => kv.Key, kv => (IReadOnlyList<Recipe>)kv.Value, StringComparer.Ordinal);
_recipesByIngredientItem = ingredient.ToDictionary(kv => kv.Key, kv => (IReadOnlyList<Recipe>)kv.Value, StringComparer.Ordinal);
_keywordsUsedInRecipeSlots = keywordSet;
```

The local `AddIngredientRecipe` helper that PR #259 added stays — it's still used by the direct-ref path.

- [ ] **Step 7: Run the tests**

```bash
dotnet test tests/Mithril.Shared.Tests --filter "FullyQualifiedName~ReferenceDataServiceRecipeCrossLinkIndexTests"
```

Expected: all PASS, including the restored `RecipesByIngredientItem_DoesNotIndexKeywordIngredients`.

- [ ] **Step 8: Run the full Mithril.Shared.Tests suite to catch regressions**

```bash
dotnet test tests/Mithril.Shared.Tests
```

Expected: all PASS.

- [ ] **Step 9: Commit**

```bash
git add src/Mithril.Shared/Reference/IReferenceDataService.cs \
        src/Mithril.Shared/Reference/ReferenceDataService.cs \
        tests/Mithril.Shared.Tests/ReferenceDataServiceTests.cs \
        tests/Mithril.Shared.Tests/Reference/ReferenceDataServiceRecipeCrossLinkIndexTests.cs
git commit -m "refactor(mithril.reference): replace RecipesByIngredientItem widening with KeywordsUsedInRecipeSlots set"
```

---

## Task 3: Add ConsumedAsKeywordIn to ItemDetailContext

Wire the new collection through the context record and the view-model.

**Files:**
- Modify: `src/Mithril.Shared.Wpf/ItemDetailContext.cs`
- Modify: `src/Mithril.Shared.Wpf/ItemDetailViewModel.cs`

- [ ] **Step 1: Add the parameter to the record**

In `src/Mithril.Shared.Wpf/ItemDetailContext.cs`, add the new parameter to the existing cross-link section. The record positional-parameter list grows by one entry inserted between `ConsumedByRecipes` and `Sources`:

```csharp
IReadOnlyList<EntityChipVm>? ProducedByRecipes = null,
IReadOnlyList<EntityChipVm>? ConsumedByRecipes = null,
IReadOnlyList<EntityChipVm>? ConsumedAsKeywordIn = null,
IReadOnlyList<ItemSourceChipVm>? Sources = null)
```

(Reuses `EntityChipVm` — keyword chips set `IconId = 0` and a `Reference` of `EntityRef.RecipeIngredientKeyword(...)`. The existing `EntityChip` template binds to `DisplayName` / `IconId` already; `PositiveIntToVis` hides the icon when `IconId == 0`.)

- [ ] **Step 2: Surface on the VM**

In `src/Mithril.Shared.Wpf/ItemDetailViewModel.cs`, alongside the existing `ConsumedByRecipes = context.ConsumedByRecipes ?? [];` line in the constructor, add:

```csharp
ConsumedAsKeywordIn = context.ConsumedAsKeywordIn ?? [];
```

And alongside the existing `public IReadOnlyList<EntityChipVm> ConsumedByRecipes { get; }` property, add:

```csharp
/// <summary>
/// Per-keyword chips for the item-detail "Used as" section. One chip per keyword the
/// item carries that also appears in some recipe's keyword-slot tuple. Clicking deep-links
/// to the Recipes tab pre-filtered via QueryText = IngredientKeywords CONTAINS "&lt;keyword&gt;".
/// </summary>
public IReadOnlyList<EntityChipVm> ConsumedAsKeywordIn { get; }
```

- [ ] **Step 3: Run the build**

```bash
dotnet build Mithril.slnx
```

Expected: 0 errors, 0 warnings.

- [ ] **Step 4: Run the test suite to confirm no regressions**

```bash
dotnet test Mithril.slnx --no-build
```

Expected: all PASS — existing call sites that don't pass `ConsumedAsKeywordIn` get `null`, which surfaces as `[]` and the section's `Count > 0` Visibility binding hides it.

- [ ] **Step 5: Commit**

```bash
git add src/Mithril.Shared.Wpf/ItemDetailContext.cs src/Mithril.Shared.Wpf/ItemDetailViewModel.cs
git commit -m "feat(mithril.shared.wpf): add ConsumedAsKeywordIn to ItemDetailContext"
```

---

## Task 4: IngredientKeywordValue wrapper record

Lightweight `IQueryStringValue`-implementing record so `RecipeListRow.IngredientKeywords` is queryable via the existing collection-`CONTAINS` path.

**Files:**
- Create: `src/Silmarillion.Module/ViewModels/IngredientKeywordValue.cs`
- Test: `tests/Silmarillion.Tests/ViewModels/IngredientKeywordValueTests.cs`

- [ ] **Step 1: Write the failing test**

Create `tests/Silmarillion.Tests/ViewModels/IngredientKeywordValueTests.cs`:

```csharp
using FluentAssertions;
using Mithril.Reference;
using Silmarillion.ViewModels;
using Xunit;

namespace Silmarillion.Tests.ViewModels;

public class IngredientKeywordValueTests
{
    [Fact]
    public void QueryStringValue_returns_Tag()
    {
        var value = new IngredientKeywordValue("Crystal");

        value.QueryStringValue.Should().Be("Crystal");
    }

    [Fact]
    public void Implements_IQueryStringValue()
    {
        IQueryStringValue value = new IngredientKeywordValue("Crystal");

        value.QueryStringValue.Should().Be("Crystal");
    }
}
```

- [ ] **Step 2: Run the failing test**

```bash
dotnet test tests/Silmarillion.Tests --filter "FullyQualifiedName~IngredientKeywordValueTests"
```

Expected: FAIL — `IngredientKeywordValue` not defined.

- [ ] **Step 3: Implement**

Create `src/Silmarillion.Module/ViewModels/IngredientKeywordValue.cs`:

```csharp
using Mithril.Reference;

namespace Silmarillion.ViewModels;

/// <summary>
/// Lightweight wrapper that lets a recipe's flattened ingredient-keyword set
/// participate in the query engine's collection-<c>CONTAINS</c> path (see
/// <see cref="IQueryStringValue"/>, shipped in PR #261). Exposes the raw
/// keyword <see cref="Tag"/> as the queryable string.
/// </summary>
public sealed record IngredientKeywordValue(string Tag) : IQueryStringValue
{
    public string QueryStringValue => Tag;
}
```

- [ ] **Step 4: Run the test**

```bash
dotnet test tests/Silmarillion.Tests --filter "FullyQualifiedName~IngredientKeywordValueTests"
```

Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add src/Silmarillion.Module/ViewModels/IngredientKeywordValue.cs tests/Silmarillion.Tests/ViewModels/IngredientKeywordValueTests.cs
git commit -m "feat(silmarillion): IngredientKeywordValue wrapper (IQueryStringValue opt-in for ingredient-keyword chips)"
```

---

## Task 5: RecipeListRow.IngredientKeywords + projection

Extend `RecipeListRow` with the flat queryable collection and populate it in `RecipesTabViewModel.BuildAllRecipes`.

**Files:**
- Modify: `src/Silmarillion.Module/ViewModels/RecipeListRow.cs`
- Modify: `src/Silmarillion.Module/ViewModels/RecipesTabViewModel.cs`
- Test: `tests/Silmarillion.Tests/ViewModels/RecipesTabViewModelTests.cs`

- [ ] **Step 1: Write the failing test**

In `tests/Silmarillion.Tests/ViewModels/RecipesTabViewModelTests.cs`, add (mirroring the existing fixture style for the file):

```csharp
[Fact]
public void RecipeListRow_IngredientKeywords_flattens_RecipeKeywordIngredient_ItemKeys_distinctly()
{
    var refData = new FakeReferenceDataService
    {
        Recipes = new Dictionary<string, Recipe>(StringComparer.Ordinal)
        {
            ["recipe_1"] = new Recipe
            {
                Key = "recipe_1",
                InternalName = "EnchantBoots",
                Name = "Enchant Boots",
                Skill = "Leatherworking",
                Ingredients =
                [
                    new RecipeKeywordIngredient { ItemKeys = ["Crystal"], StackSize = 1 },
                    new RecipeKeywordIngredient { ItemKeys = ["Crystal", "Tier2"], StackSize = 1 },
                ],
            },
        },
    };
    var navigator = new FakeReferenceNavigator();

    var vm = new RecipesTabViewModel(refData, navigator);

    var row = vm.AllRecipes.Should().ContainSingle().Subject;
    row.IngredientKeywords.Select(k => k.Tag)
        .Should().BeEquivalentTo(["Crystal", "Tier2"]);
}
```

(Use whatever fake refData / navigator helpers the existing tests in this file already use; if none, define minimal ones following the existing pattern in `tests/Silmarillion.Tests/ViewModels/`.)

- [ ] **Step 2: Run the failing test**

```bash
dotnet test tests/Silmarillion.Tests --filter "FullyQualifiedName~RecipesTabViewModelTests.RecipeListRow_IngredientKeywords"
```

Expected: FAIL — `RecipeListRow.IngredientKeywords` doesn't exist.

- [ ] **Step 3: Extend the record**

In `src/Silmarillion.Module/ViewModels/RecipeListRow.cs`, add a parameter (last position, defaulted for backwards compatibility within this branch — though the `BuildAllRecipes` projection populates it everywhere we construct):

```csharp
public sealed record RecipeListRow(
    Recipe Recipe,
    string Name,
    int IconId,
    string? SkillDisplayName,
    int SkillLevelReq,
    IReadOnlyList<IngredientKeywordValue> IngredientKeywords);
```

- [ ] **Step 4: Populate it in `BuildAllRecipes`**

In `src/Silmarillion.Module/ViewModels/RecipesTabViewModel.cs`, update `BuildAllRecipes` so each row carries the flattened keyword set. The new projection inside the `Select`:

```csharp
.Select(r => new RecipeListRow(
    Recipe: r,
    Name: r.Name ?? r.InternalName ?? r.Key,
    IconId: r.IconId > 0 ? r.IconId : ResolveRecipeFallbackIcon(r, refData),
    SkillDisplayName: ResolveSkillDisplayName(r, refData),
    SkillLevelReq: r.SkillLevelReq,
    IngredientKeywords: BuildIngredientKeywords(r)))
```

And add the helper at class scope:

```csharp
private static IReadOnlyList<IngredientKeywordValue> BuildIngredientKeywords(Recipe recipe)
{
    if (recipe.Ingredients is null) return [];
    return recipe.Ingredients
        .OfType<RecipeKeywordIngredient>()
        .SelectMany(slot => slot.ItemKeys)
        .Distinct(StringComparer.Ordinal)
        .Select(tag => new IngredientKeywordValue(tag))
        .ToList();
}
```

(Reuse whatever `ResolveRecipeFallbackIcon` / `ResolveSkillDisplayName` helpers the file already has — don't redefine. If the current `BuildAllRecipes` body has them inline, factor those out only if it improves readability.)

- [ ] **Step 5: Run the test**

```bash
dotnet test tests/Silmarillion.Tests --filter "FullyQualifiedName~RecipesTabViewModelTests"
```

Expected: PASS — including all existing tests in the file (the new positional parameter must be propagated; if any existing test constructs `RecipeListRow` directly, fix the call site to pass `IngredientKeywords: []`).

- [ ] **Step 6: Run the broader suite**

```bash
dotnet test Mithril.slnx
```

Expected: all PASS. Confirms `RecipeListRow`'s new positional parameter didn't break other consumers.

- [ ] **Step 7: Commit**

```bash
git add src/Silmarillion.Module/ViewModels/RecipeListRow.cs \
        src/Silmarillion.Module/ViewModels/RecipesTabViewModel.cs \
        tests/Silmarillion.Tests/ViewModels/RecipesTabViewModelTests.cs
git commit -m "feat(silmarillion): RecipeListRow.IngredientKeywords flattens RecipeKeywordIngredient slots for query CONTAINS"
```

---

## Task 6: ItemsTabViewModel populates ConsumedAsKeywordIn

Build the per-item keyword-chip set in `BuildCrossLinkContext`.

**Files:**
- Modify: `src/Silmarillion.Module/ViewModels/ItemsTabViewModel.cs`
- Test: `tests/Silmarillion.Tests/ViewModels/ItemsTabViewModelTests.cs`

- [ ] **Step 1: Write the failing test**

In `tests/Silmarillion.Tests/ViewModels/ItemsTabViewModelTests.cs`, add:

```csharp
[Fact]
public void BuildCrossLinkContext_emits_keyword_chips_for_intersection_of_item_Keywords_and_KeywordsUsedInRecipeSlots()
{
    var refData = new FakeReferenceDataService
    {
        ItemsByInternalName = new Dictionary<string, Item>(StringComparer.Ordinal)
        {
            ["MassiveTourmaline"] = new Item
            {
                InternalName = "MassiveTourmaline",
                Name = "Massive Tourmaline",
                Keywords = [new ItemKeyword("Crystal", 0), new ItemKeyword("Bogus", 0)],
            },
        },
        KeywordsUsedInRecipeSlots = new HashSet<string>(StringComparer.Ordinal) { "Crystal" },
    };
    var navigator = new FakeReferenceNavigator { CanOpenAlways = true };

    var vm = new ItemsTabViewModel(refData, navigator);
    vm.SelectedRow = vm.AllItems.First(i => i.InternalName == "MassiveTourmaline");

    vm.DetailViewModel.Should().NotBeNull();
    vm.DetailViewModel!.ConsumedAsKeywordIn.Should().ContainSingle()
        .Which.Reference.Should().Be(EntityRef.RecipeIngredientKeyword("Crystal"));
    vm.DetailViewModel.ConsumedAsKeywordIn.Single().DisplayName.Should().Be("Crystal");
    vm.DetailViewModel.ConsumedAsKeywordIn.Single().IconId.Should().Be(0);
}
```

(Adapt fake-class shape to whatever conventions the existing tests use. The point: `Crystal` survives, `Bogus` is filtered out because it isn't in `KeywordsUsedInRecipeSlots`.)

- [ ] **Step 2: Run the failing test**

```bash
dotnet test tests/Silmarillion.Tests --filter "FullyQualifiedName~ItemsTabViewModelTests.BuildCrossLinkContext_emits_keyword_chips"
```

Expected: FAIL — `ConsumedAsKeywordIn` empty (default).

- [ ] **Step 3: Implement**

In `src/Silmarillion.Module/ViewModels/ItemsTabViewModel.cs`, extend `BuildCrossLinkContext`:

```csharp
private ItemDetailContext BuildCrossLinkContext(Item item)
{
    if (string.IsNullOrEmpty(item.InternalName))
    {
        return ItemDetailContext.Empty;
    }
    return new ItemDetailContext(
        ProducedByRecipes: BuildRecipeChips(_refData.RecipesByProducedItem, item.InternalName!),
        ConsumedByRecipes: BuildRecipeChips(_refData.RecipesByIngredientItem, item.InternalName!),
        ConsumedAsKeywordIn: BuildKeywordChips(item),
        Sources: BuildSourceChips(item.InternalName!));
}

private IReadOnlyList<EntityChipVm>? BuildKeywordChips(Item item)
{
    if (item.Keywords is null || item.Keywords.Count == 0)
        return null;
    var used = _refData.KeywordsUsedInRecipeSlots;
    if (used.Count == 0) return null;
    var chips = item.Keywords
        .Where(k => used.Contains(k.Tag))
        .Select(k => new EntityChipVm(
            DisplayName: k.Tag,
            IconId: 0,
            Reference: EntityRef.RecipeIngredientKeyword(k.Tag),
            IsNavigable: _navigator.CanOpen(EntityRef.RecipeIngredientKeyword(k.Tag))))
        .ToList();
    return chips.Count == 0 ? null : chips;
}
```

- [ ] **Step 4: Run the test**

```bash
dotnet test tests/Silmarillion.Tests --filter "FullyQualifiedName~ItemsTabViewModelTests"
```

Expected: PASS.

- [ ] **Step 5: Run the full suite**

```bash
dotnet test Mithril.slnx
```

Expected: all PASS.

- [ ] **Step 6: Commit**

```bash
git add src/Silmarillion.Module/ViewModels/ItemsTabViewModel.cs \
        tests/Silmarillion.Tests/ViewModels/ItemsTabViewModelTests.cs
git commit -m "feat(silmarillion): populate ConsumedAsKeywordIn in item-detail context"
```

---

## Task 7: RecipeIngredientKeywordKindTarget + DI registration

Make the new EntityRef variant actually dispatchable.

**Files:**
- Create: `src/Silmarillion.Module/Navigation/RecipeIngredientKeywordKindTarget.cs`
- Modify: `src/Silmarillion.Module/SilmarillionModule.cs`
- Test: `tests/Silmarillion.Tests/Navigation/SilmarillionReferenceNavigatorTests.cs`

- [ ] **Step 1: Write the failing test**

In `tests/Silmarillion.Tests/Navigation/SilmarillionReferenceNavigatorTests.cs`, add (mirroring the existing fixture style):

```csharp
[Fact]
public void Open_RecipeIngredientKeyword_selects_recipes_tab_and_sets_query_text()
{
    var refData = new FakeReferenceDataService();
    var navigator = BuildNavigatorWithAllKindTargets(refData, out var silmarillionVm);

    navigator.Open(EntityRef.RecipeIngredientKeyword("Crystal"));

    silmarillionVm.SelectedTabIndex.Should().Be(1, because: "Recipes tab is index 1");
    silmarillionVm.Recipes.QueryText.Should().Be("IngredientKeywords CONTAINS \"Crystal\"");
}
```

(If `BuildNavigatorWithAllKindTargets` doesn't exist as a helper in this test file, use the same DI-style construction the other tests in the file use — instantiating the navigator with the eager constructor and a list of kind targets that include the new one.)

- [ ] **Step 2: Run the failing test**

```bash
dotnet test tests/Silmarillion.Tests --filter "FullyQualifiedName~SilmarillionReferenceNavigatorTests.Open_RecipeIngredientKeyword"
```

Expected: FAIL — `RecipeIngredientKeywordKindTarget` not registered (the navigator's `CanOpen` returns false and `Open` does nothing for the keyword Kind, or the `SilmarillionViewModel.OnNavigated` path doesn't have a target to consult).

- [ ] **Step 3: Implement the kind target**

Create `src/Silmarillion.Module/Navigation/RecipeIngredientKeywordKindTarget.cs`:

```csharp
using Mithril.Shared.Diagnostics;
using Mithril.Shared.Reference;
using Silmarillion.ViewModels;

namespace Silmarillion.Navigation;

/// <summary>
/// <see cref="IReferenceKindTarget"/> for <see cref="EntityKind.RecipeIngredientKeyword"/>.
/// Flips to the Recipes tab and pre-populates <see cref="RecipesTabViewModel.QueryText"/>
/// with <c>IngredientKeywords CONTAINS "&lt;keyword&gt;"</c>, leveraging PR #261's
/// collection-CONTAINS support. The chip's display label and the keyword carried in
/// <see cref="EntityRef.InternalName"/> are the same string.
/// </summary>
public sealed class RecipeIngredientKeywordKindTarget : IReferenceKindTarget
{
    private readonly RecipesTabViewModel _vm;
    private readonly IDiagnosticsSink? _diag;

    public RecipeIngredientKeywordKindTarget(RecipesTabViewModel vm, IDiagnosticsSink? diag = null)
    {
        _vm = vm;
        _diag = diag;
    }

    public EntityKind Kind => EntityKind.RecipeIngredientKeyword;

    public int TabIndex => 1; // same tab as Recipes

    public bool TrySelectByInternalName(string internalName)
    {
        // Quote the keyword so a hyphen, space, or other token boundary inside the tag
        // doesn't break the query parser.
        var query = $"IngredientKeywords CONTAINS \"{internalName}\"";
        _diag?.Info("Silmarillion.Nav", $"RecipeIngredientKeyword.TrySelect '{internalName}' → setting QueryText.");
        _vm.QueryText = query;
        return true;
    }
}
```

- [ ] **Step 4: Register in DI**

In `src/Silmarillion.Module/SilmarillionModule.cs`, alongside the existing `services.AddSingleton<IReferenceKindTarget, RecipesKindTarget>();` (or however the module currently registers kind targets), add:

```csharp
services.AddSingleton<IReferenceKindTarget, RecipeIngredientKeywordKindTarget>();
```

If the test helper in step 1 constructs the navigator directly (bypassing DI), make sure the helper also includes the new target.

- [ ] **Step 5: Run the test**

```bash
dotnet test tests/Silmarillion.Tests --filter "FullyQualifiedName~SilmarillionReferenceNavigatorTests"
```

Expected: PASS.

- [ ] **Step 6: Run the full suite**

```bash
dotnet test Mithril.slnx
```

Expected: all PASS.

- [ ] **Step 7: Commit**

```bash
git add src/Silmarillion.Module/Navigation/RecipeIngredientKeywordKindTarget.cs \
        src/Silmarillion.Module/SilmarillionModule.cs \
        tests/Silmarillion.Tests/Navigation/SilmarillionReferenceNavigatorTests.cs
git commit -m "feat(silmarillion): RecipeIngredientKeywordKindTarget — keyword chip → Recipes tab QueryText"
```

---

## Task 8: XAML — "Used in (N)" count suffix + "Used as" section

Render the new section, add the count, and reuse the existing chip template for the keyword chips.

**Files:**
- Modify: `src/Mithril.Shared.Wpf/ItemDetailView.xaml`

- [ ] **Step 1: Add `(N)` count to the "Used in" header**

In `src/Mithril.Shared.Wpf/ItemDetailView.xaml`, locate the existing "Used in" section (search for `Text="Used in"`). Replace the existing single-line `TextBlock` header with a header that includes the count, e.g.:

```xml
<TextBlock FontWeight="SemiBold"
           Foreground="#88FFFFFF"
           FontSize="{DynamicResource AppFontSizeHint}" Margin="0,0,0,4">
    <Run Text="Used in" />
    <Run Text="{Binding ConsumedByRecipes.Count, StringFormat=' ({0})'}" />
</TextBlock>
```

(Keep all other surrounding markup — Visibility binding, ItemsControl, chip template — unchanged.)

- [ ] **Step 2: Add the new "Used as" section**

Immediately after the existing "Used in" `StackPanel`'s closing tag, insert the new section:

```xml
<!-- Used as: keyword chips — items that satisfy keyword-based slots in recipes -->
<StackPanel Margin="0,10,0,0"
            Visibility="{Binding ConsumedAsKeywordIn.Count, Converter={StaticResource PositiveIntToVis}}">
    <TextBlock Text="Used as" FontWeight="SemiBold"
               Foreground="#88FFFFFF"
               FontSize="{DynamicResource AppFontSizeHint}" Margin="0,0,0,4"/>
    <ItemsControl ItemsSource="{Binding ConsumedAsKeywordIn}">
        <ItemsControl.ItemsPanel>
            <ItemsPanelTemplate><WrapPanel/></ItemsPanelTemplate>
        </ItemsControl.ItemsPanel>
        <ItemsControl.ItemTemplate>
            <DataTemplate>
                <c:EntityChip ClickCommand="{Binding DataContext.OpenEntityCommand, RelativeSource={RelativeSource AncestorType={x:Type local:ItemDetailView}}}"/>
            </DataTemplate>
        </ItemsControl.ItemTemplate>
    </ItemsControl>
</StackPanel>
```

The existing `EntityChip` template binds to `DisplayName` / `IconId` / `IsNavigable` already, and `PositiveIntToVis` on the icon hides it when `IconId == 0`. No new template needed.

- [ ] **Step 3: Build**

```bash
dotnet build Mithril.slnx
```

Expected: 0 errors, 0 warnings.

- [ ] **Step 4: Manual smoke test**

```bash
dotnet run --project src/Mithril.Shell
```

In the running shell:
- Silmarillion tab → Items → open **Massive Tourmaline** (or any item the screenshot showed flooded).
- Confirm: "Used in" section shows the small direct-ref count (likely zero, in which case the whole section is hidden — that's correct). The "Used as" section appears with the small chip set (e.g. `[ Crystal ]`).
- Click the `Crystal` chip. The Recipes tab opens, query box shows `IngredientKeywords CONTAINS "Crystal"`, and the filtered recipe list materialises instantly.
- Edit the query box to `IngredientKeywords CONTAINS "Crystal" AND Skill = "Calligraphy"`. Confirm the list re-filters.
- Clear the query box. Full recipe list returns.
- Navigate back (back button) to the Tourmaline detail. Section still renders.

If anything looks visually off (e.g. spacing, font weight mismatch with other sections), tweak inline and re-run.

- [ ] **Step 5: Commit**

```bash
git add src/Mithril.Shared.Wpf/ItemDetailView.xaml
git commit -m "feat(silmarillion): item-detail Used in (N) count + new Used as keyword-chip section"
```

---

## Task 9: Delete the superseded agent-plan file

PR #259 committed `docs/agent-plans/silmarillion-254-widen-recipes-by-ingredient.md` describing the merged-list approach. That approach is now reverted; the new design is captured in the spec under `docs/superpowers/specs/`.

**Files:**
- Delete: `docs/agent-plans/silmarillion-254-widen-recipes-by-ingredient.md`

- [ ] **Step 1: Delete the file**

```bash
git rm docs/agent-plans/silmarillion-254-widen-recipes-by-ingredient.md
```

- [ ] **Step 2: Commit**

```bash
git commit -m "docs: drop superseded silmarillion-254 widen-by-ingredient plan (replaced by used-in-keyword-collapse spec)"
```

---

## Task 10: Add the new spec + plan to the PR

The spec at `docs/superpowers/specs/2026-05-13-silmarillion-used-in-keyword-collapse-design.md` and this plan at `docs/superpowers/plans/2026-05-13-silmarillion-used-in-keyword-collapse.md` are currently untracked. Commit them so the PR carries its own design rationale.

- [ ] **Step 1: Add and commit**

```bash
git add docs/superpowers/specs/2026-05-13-silmarillion-used-in-keyword-collapse-design.md \
        docs/superpowers/plans/2026-05-13-silmarillion-used-in-keyword-collapse.md
git commit -m "docs: spec + plan for silmarillion Used-in keyword collapse — #254"
```

---

## Task 11: Force-push the amended branch + update PR #259

The branch was rebased onto `origin/main` in Task 0 and the new commits diverge from the previously pushed tip. Use `--force-with-lease` to avoid clobbering anyone else's work.

- [ ] **Step 1: Run the full test suite one final time**

```bash
dotnet test Mithril.slnx
```

Expected: all PASS.

- [ ] **Step 2: Force-push**

```bash
git push --force-with-lease origin feat/254-recipes-by-ingredient-keyword-expansion
```

Expected: success. If `--force-with-lease` reports a remote-side update, abort and investigate — don't `--force`.

- [ ] **Step 3: Update PR #259 title and description**

```bash
gh pr edit 259 --title "feat(silmarillion): collapse keyword-matched recipes into per-keyword Used as chips — #254"
```

Then update the body via `gh pr edit 259 --body "$(cat <<'EOF' …)"` with content matching the new approach. Template:

```
## Summary

- Earlier in this PR, `RecipesByIngredientItem` was widened to fan keyword-slot recipes into the per-item dictionary. For broad-keyword items (e.g. Massive Tourmaline / "any Crystal" enchanting), this surfaced 500+ recipe chips in the item-detail "Used in" section — correct data, unusably slow render, and even when fast it was unscannable.
- This amend replaces the widening with two layers:
  - `RecipesByIngredientItem` reverts to direct ItemCode refs only.
  - New flat `IReferenceDataService.KeywordsUsedInRecipeSlots` powers a new item-detail "Used as" section. Each keyword the item carries that also appears in some recipe slot becomes a single chip.
- Keyword chips deep-link to the Recipes tab with `QueryText = IngredientKeywords CONTAINS "<keyword>"` pre-filled, via a new `EntityKind.RecipeIngredientKeyword` variant and matching `IReferenceKindTarget`. Builds on #260/#261's `IQueryStringValue` + collection-CONTAINS support.
- Chip count for Massive Tourmaline drops from ~547 to single digits.

Closes #254.

## Test Plan
- [x] `dotnet build Mithril.slnx` clean.
- [x] `dotnet test Mithril.slnx` — full suite green.
- [x] Manual: Silmarillion → Items → Massive Tourmaline → "Used as" shows `[ Crystal ]` (plus any other declared keywords intersected with recipe usage). Clicking opens Recipes tab pre-filtered. Query box can be refined / cleared normally.
```

Confirm the PR page reflects the new shape, then ping for review.

---

## Self-Review

Skim the spec against this plan:

- **Spec § Approach** — Task 6 (chip projection) + Task 8 (XAML) implement the per-keyword chip flow. ✓
- **Spec § Reference-data layer** — Task 2 implements both the reverted `RecipesByIngredientItem` and the new `KeywordsUsedInRecipeSlots`. ✓
- **Spec § Silmarillion items tab — VM** — Tasks 3 (context property) + 6 (projection) cover. The spec called out a separate `KeywordChipVm` type; the plan instead reuses `EntityChipVm` (with `IconId = 0`) to minimise surface — same wire shape, no template variant needed. Updated in the spec self-review and reflected here.
- **Spec § Navigator + deep-link** — Tasks 1 (EntityKind variant) + 7 (kind target + DI). ✓
- **Spec § Recipes tab — queryable ingredient keywords** — Tasks 4 (`IngredientKeywordValue`) + 5 (`RecipeListRow.IngredientKeywords`). ✓
- **Spec § XAML** — Task 8. ✓
- **Spec § Out of scope** — virtualization, `MithrilVirtualizingWrapPanel` sunset, diagnostics — all explicitly not in this plan, matches.
- **Spec § Tests** — replaced PR #259 keyword-expansion tests covered in Task 2; new VM / navigator / `IngredientKeywordValue` / `RecipeListRow` tests covered in Tasks 4–7.
- **Spec § Files touched** — every entry in the spec's table maps to a Task above.

**Placeholder scan:** no TBDs, no "implement appropriately", no untyped references to undefined helpers (each task either uses existing helpers visible to the file or defines the helper inline).

**Type / signature consistency:**
- `EntityRef.RecipeIngredientKeyword(string)` — defined Task 1, referenced Tasks 6, 7. ✓
- `KeywordsUsedInRecipeSlots: IReadOnlyCollection<string>` — defined Task 2, referenced Task 6. ✓
- `ItemDetailContext.ConsumedAsKeywordIn` — added Task 3, populated Task 6, rendered Task 8. ✓
- `RecipeListRow.IngredientKeywords: IReadOnlyList<IngredientKeywordValue>` — defined Task 5, type from Task 4. ✓
- `RecipeIngredientKeywordKindTarget` — defined Task 7, referenced in DI same task. ✓
