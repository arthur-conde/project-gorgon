# Silmarillion "Used in" — collapse keyword-matched recipes into keyword chips

**Tracked in:** #254 (PR #259, in flight — to be amended).
**Companion:** #260 / PR #261 (shipped — `IQueryStringValue` + collection `CONTAINS`).

## Problem

PR #259 widens `IReferenceDataService.RecipesByIngredientItem` so that opening an item that's keyword-matched (e.g. Massive Tourmaline, matched as "any Crystal") surfaces every recipe that consumes it via a keyword slot. Behaviourally correct, but the "Used in" section now renders **500+ chips** for items matched by broad keywords like `Crystal`. The page is sluggish to render and unusable to scan — the keyword-matched bucket drowns the per-item signal.

Virtualization is not the answer here. Even fully virtualized, scanning 500 chips that are mostly different *enchanted-target-item × Mk × Enchanted/Max-Enchanted* permutations of "any Crystal" isn't useful. The user can't tell at a glance that "this Tourmaline is used by enchanting recipes across these keyword kinds" because the signal is buried in target-item-level chip volume.

## Approach

**Show keyword matches as one chip per keyword, not one chip per recipe.** When a recipe consumes Tourmaline through a `RecipeKeywordIngredient` whose `ItemKeys` AND-match Tourmaline's keywords, that recipe contributes to a **single shared chip** for the matched keyword, not its own personal chip in the "Used in" list.

The "Used in" section splits into two:

- **Used in (N)** — recipes that name this item by `ItemCode` directly. Recipe chips, same as today. Clicking a chip → recipe detail. Header carries the count.
- **Used as** — one chip per keyword on the item that's also used in at least one recipe slot. Plain keyword-label chips (no count, no item icon). Clicking a chip → Recipes tab with `IngredientKeywords CONTAINS "<keyword>"` pre-filled in the query box, leveraging the now-supported collection `CONTAINS` from #260/#261.

For Massive Tourmaline (carries `Keywords = ["Crystal"]`, possibly others): "Used in" stays small (0–3 direct chips), "Used as" shows ~1–3 keyword chips. Total chip count collapses from 547 to single digits.

This also reads more accurately. A slot accepting "any Crystal" isn't picking Tourmaline specifically; framing it as `Crystal` (a kind) rather than as 500 individual recipe chips (instances) matches the data's actual semantics.

## Design

### Reference-data layer (`Mithril.Shared.Reference`)

`IReferenceDataService` exposes:

```csharp
// Reverted to pre-#259 shape — direct ItemCode refs only.
IReadOnlyDictionary<string, IReadOnlyList<Recipe>> RecipesByIngredientItem { get; }

// New — set of every distinct keyword tag that appears in any RecipeKeywordIngredient.ItemKeys
// across all recipes. Used to filter the per-item chip set to "keywords that actually lead
// somewhere" (no "Tourmaline × 0" chips for keywords no recipe slot consumes).
IReadOnlyCollection<string> KeywordsUsedInRecipeSlots => Array.Empty<string>();
```

Both are populated in `ReferenceDataService.BuildRecipeCrossLinkIndices`. The keyword set is a `HashSet<string>` (ordinal) built in the same single walk over recipe ingredients that builds the item dictionaries.

The interface default for `KeywordsUsedInRecipeSlots` is an empty array so test fakes don't need to opt in (mirrors the existing pattern for `RecipesByProducedItem` / `RecipesByIngredientItem`).

PR #259's switch-on-ingredient-subclass logic that fanned `RecipeKeywordIngredient` slots into the per-item dictionary **is removed**. The widening was correct in spirit but produces the wrong primitive for this UI. The new keyword set is built in the same loop with a single `Add` per slot.

### Silmarillion items tab — VM

`ItemDetailContext` gains a third cross-link collection. **Reuse `EntityChipVm`** for keyword chips — no new chip-VM type. The existing `EntityChip` template already binds `DisplayName` / `IconId` / `IsNavigable`; set `IconId = 0` and the `PositiveIntToVis` converter hides the icon, leaving a plain text chip. The keyword chip's identity is carried by `EntityRef.Kind == RecipeIngredientKeyword`, so a consumer that needs to differentiate can switch on Kind.

```csharp
public sealed record ItemDetailContext(
    IReadOnlyList<EntityChipVm>? ProducedByRecipes,
    IReadOnlyList<EntityChipVm>? ConsumedByRecipes,        // direct refs — recipe chips
    IReadOnlyList<EntityChipVm>? ConsumedAsKeywordIn,      // keyword refs — keyword chips (IconId = 0)
    IReadOnlyList<ItemSourceChipVm>? Sources);
```

`ItemsTabViewModel.BuildCrossLinkContext` builds the keyword-chip list by intersecting `item.Keywords` (raw item tags) with `_refData.KeywordsUsedInRecipeSlots`. Enriched/virtual keywords from `ItemKeywordSynthesis.Enrich` are **not** surfaced — chip set stays scoped to keywords the item author actually declared on the item, to avoid noise like `EquipmentSlot:MainHand × every weapon`.

### Navigator + deep-link

The keyword chip's click pre-populates the Recipes tab's existing `QueryText` rather than introducing a new filter property or banner UI. Implementation: an `EntityRef.RecipeIngredientKeyword(string keyword)` variant whose dispatch handler in `SilmarillionReferenceNavigator` flips the active tab to Recipes and sets the recipes-tab VM's `QueryText` to `IngredientKeywords CONTAINS "<keyword>"`.

This composes naturally with the existing query system — the user can refine (`Skill = "Calligraphy" AND IngredientKeywords CONTAINS "Crystal"`), clear via the existing query-box, and bookmark via the existing query UX. No new filter UI.

The precise shape of the new `EntityRef` variant — whether it slots into an existing discriminated-union style or extends a registry-driven dispatcher — is an implementation detail resolved during plan-writing once the current `EntityRef` shape is read. The spec commits only to "an `EntityRef`-like deep-link target that the navigator dispatches by flipping to Recipes tab and setting `QueryText`."

### Recipes tab — queryable ingredient keywords

`RecipeListRow` gains an `IngredientKeywords` property:

```csharp
public sealed record RecipeListRow(
    Recipe Recipe,
    string Name,
    int IconId,
    string? SkillDisplayName,
    int SkillLevelReq,
    IReadOnlyList<IngredientKeywordValue> IngredientKeywords);   // new

// Wrapper so query system's CONTAINS picks the string up via IQueryStringValue.
// Lives in Silmarillion.Module (UI projection — not shared infra).
public sealed record IngredientKeywordValue(string Tag) : IQueryStringValue
{
    public string QueryStringValue => Tag;
}
```

`BuildAllRecipes` populates `IngredientKeywords` by flattening every `RecipeKeywordIngredient.ItemKeys` entry across all of the recipe's slots, deduping by tag. The query compiler's collection-`CONTAINS` (per #261) picks it up automatically because the element type implements `IQueryStringValue`.

### XAML — `ItemDetailView`

Two changes to the "Used in" region:

1. Existing `Used in` `TextBlock` header gains a `(N)` count suffix bound to `ConsumedByRecipes.Count`, visible only when count > 0.
2. New `StackPanel` below it titled `Used as`, visible when `ConsumedAsKeywordIn` is non-empty. `ItemsControl` + `WrapPanel`, same shape as the existing chip flows. Chip template: same `EntityChip` user control with a plain keyword label and no icon (`IconId = 0` — the existing `PositiveIntToVis` converter hides the icon image).

No new panel or container types. The XAML diff is small.

### Out of scope

- **Virtualization** — chip count collapse makes it unnecessary.
- **Sunsetting `MithrilVirtualizingWrapPanel`** — separate, when touched (memory entry already records the policy).
- **Render diagnostics** — chip count collapse removes the urgency; revisit if a future broad-keyword item still produces too many chips.
- **Slot-tuple precision** — multi-key tuples (`["Crystal","Tier2"]`) collapse into the contributing keywords' chips (Crystal, Tier2) individually. The query `IngredientKeywords CONTAINS "Crystal"` matches all slots containing Crystal — including the multi-key ones. Acceptable; preserves chip simplicity and the chip-click → query-match invariant.
- **Direct-ref chip dedup vs keyword-ref chip overlap** — a recipe that consumes the item *both* directly and via a keyword slot appears in "Used in" (direct) once. The keyword chip's filter would also surface it. That's correct; both axes are real.

## Tests

Replace the test suite added in #259:

- **Reverted index** — `RecipesByIngredientItem` returns direct-ItemCode refs only. The three keyword-expansion tests in `ReferenceDataServiceTests.cs` and the deleted assertion in `ReferenceDataServiceRecipeCrossLinkIndexTests.cs` are dropped.
- **`KeywordsUsedInRecipeSlots`** — fixture: two recipes, one with `RecipeKeywordIngredient { ItemKeys: ["Crystal"] }`, one with `["Crystal","Tier2"]`. Assert the set contains `{ "Crystal", "Tier2" }` with no other entries (no inflation from item Keywords, no dedup miss).
- **VM chip projection** — fixture: item with `Keywords = ["Crystal","Bogus"]`, recipe service with `KeywordsUsedInRecipeSlots = { "Crystal" }`. Assert `ConsumedAsKeywordIn` contains a single chip for `Crystal`, no `Bogus` chip (filtered out — no recipe consumes it).
- **`RecipeListRow.IngredientKeywords` queryable** — fixture: recipe with slots `{ItemKeys: ["Crystal"]}` and `{ItemKeys: ["Tier2"]}`. Assert `IngredientKeywords` projects to two entries with the right tags. A `QueryCompiler` test against `IngredientKeywords CONTAINS "Crystal"` returns the recipe.
- **Navigator round-trip** — `SilmarillionReferenceNavigator.Open(EntityRef.RecipeIngredientKeyword("Crystal"))` switches to Recipes tab and sets `QueryText = "IngredientKeywords CONTAINS \"Crystal\""`.
- **Detail-view rendering** — existing `ItemDetailView` test (if present; create if not) covers "Used in" count suffix and "Used as" section visibility binding.

## Files touched

| Path | Change |
|---|---|
| `src/Mithril.Shared/Reference/IReferenceDataService.cs` | Add `KeywordsUsedInRecipeSlots`; revert docstring for `RecipesByIngredientItem`. |
| `src/Mithril.Shared/Reference/ReferenceDataService.cs` | `BuildRecipeCrossLinkIndices`: drop the keyword fan-out into the item dict; populate the new keyword set. |
| `src/Mithril.Shared.Wpf/ItemDetailContext.cs` | Add `ConsumedAsKeywordIn` parameter (reuses `EntityChipVm`). |
| `src/Mithril.Shared.Wpf/ItemDetailViewModel.cs` | Surface the new collection through the view-model. |
| `src/Mithril.Shared.Wpf/ItemDetailView.xaml` | Header `(N)` count suffix on "Used in"; new "Used as" section. |
| `src/Silmarillion.Module/ViewModels/ItemsTabViewModel.cs` | Populate `ConsumedAsKeywordIn`. |
| `src/Silmarillion.Module/ViewModels/RecipeListRow.cs` | Add `IngredientKeywords` projection. |
| `src/Silmarillion.Module/ViewModels/RecipesTabViewModel.cs` | `BuildAllRecipes` populates the new field. |
| `src/Silmarillion.Module/ViewModels/IngredientKeywordValue.cs` (new) | `IQueryStringValue` wrapper record. |
| `src/Silmarillion.Module/Navigation/SilmarillionReferenceNavigator.cs` | Dispatch new `EntityRef` variant to Recipes tab + set QueryText. |
| `src/Mithril.Shared.Wpf/EntityRef.cs` | New variant. |
| `tests/Mithril.Shared.Tests/ReferenceDataServiceTests.cs` | Replace #259 keyword-expansion tests with `KeywordsUsedInRecipeSlots` coverage. |
| `tests/Mithril.Shared.Tests/Reference/ReferenceDataServiceRecipeCrossLinkIndexTests.cs` | Restore a no-keyword-expansion assertion (verify the widening is gone). |
| `tests/Silmarillion.Tests/ViewModels/ItemsTabViewModelTests.cs` | Cover `ConsumedAsKeywordIn` projection from item Keywords ∩ `KeywordsUsedInRecipeSlots`. |
| `tests/Silmarillion.Tests/ViewModels/RecipesTabViewModelTests.cs` | Cover `RecipeListRow.IngredientKeywords` projection + `IngredientKeywords CONTAINS "<tag>"` query path. |
| `tests/Silmarillion.Tests/Navigation/SilmarillionReferenceNavigatorTests.cs` | Cover the `EntityRef.RecipeIngredientKeyword("Crystal")` dispatch → Recipes tab + `QueryText` set. |
| `docs/agent-plans/silmarillion-254-widen-recipes-by-ingredient.md` | Replaced by the new plan derived from this spec. |

## PR shape

Amend in place on the existing branch `feat/254-recipes-by-ingredient-keyword-expansion`, force-push, update PR #259 title + description to reflect the new approach. Branch policy intact (still a PR against main, not a direct push).

Suggested commit message:

```
feat(silmarillion): collapse keyword-matched recipes into per-keyword chips — #254

Replaces the merged-index approach from earlier in this PR with a split:
- RecipesByIngredientItem reverts to direct ItemCode refs only.
- New KeywordsUsedInRecipeSlots powers a per-item-keyword chip set in the
  item-detail "Used as" section.
- Keyword chips deep-link to the Recipes tab via QueryText = "IngredientKeywords
  CONTAINS <keyword>", leveraging #260/#261's collection CONTAINS support.

For broad-keyword items (Massive Tourmaline ≈ Crystal), chip count collapses
from 500+ to single digits. No virtualization required.

Closes #254.
```

## Verification

1. **Build:** `dotnet build Mithril.slnx` — warnings-as-errors clean.
2. **Targeted:** `dotnet test tests/Mithril.Shared.Tests --filter "FullyQualifiedName~ReferenceDataService"` plus `tests/Silmarillion.Tests`.
3. **Full suite:** `dotnet test Mithril.slnx`.
4. **Manual:**
   - `dotnet run --project src/Mithril.Shell`.
   - Silmarillion → Items tab → Massive Tourmaline. "Used in" header shows direct-ref count (likely small or zero). "Used as" shows `Crystal` (and any other declared keywords intersected with recipe usage).
   - Click `Crystal` keyword chip. Recipes tab opens with `QueryText = IngredientKeywords CONTAINS "Crystal"` and the filtered list materialises instantly.
   - Refine query manually (e.g. add `AND Skill = "Calligraphy"`). Filter composes.
   - Clear query box. Full recipe list returns.
   - Open a direct-ref recipe from the "Used in" section. Recipe detail opens (existing behaviour).
   - Verify no chip duplication: open a Crystal that's both directly referenced by some recipe AND keyword-matched — the direct-ref recipe is in "Used in" once; the `Crystal` chip is in "Used as" once.
