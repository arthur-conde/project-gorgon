# mithril.reference: widen RecipesByIngredientItem to include keyword-matched recipes

**Tracked in:** #254 (companion to closed #253).

## Context

`IReferenceDataService.RecipesByIngredientItem[itemName]` returns every recipe whose ingredient list references the item via `RecipeItemIngredient.ItemCode`. Recipes that consume the item via a matching `RecipeKeywordIngredient` (the `{ "ItemKeys": [...] }` AND-matched keyword shape) are deliberately excluded — see the docstring at [src/Mithril.Shared/Reference/IReferenceDataService.cs:43-50](../../src/Mithril.Shared/Reference/IReferenceDataService.cs#L43-L50):

> `RecipeKeywordIngredient` entries are kind-based, not item-based, and are excluded.

The consequence shows up in Silmarillion's item-detail "Used in" section ([src/Silmarillion.Module/ViewModels/ItemsTabViewModel.cs:109](../../src/Silmarillion.Module/ViewModels/ItemsTabViewModel.cs#L109), which reads `RecipesByIngredientItem`). Opening a Crystal doesn't surface any enchanting recipe that consumes "any Crystal" via `ItemKeys: ["Crystal"]` — from the user's perspective the recipe trail is broken: they can see the produced enchanted item, but can't reverse-engineer which recipes use this Crystal.

**Scale:** per the audit comment in [RecipeIngredientConverter.cs:17-19](../../src/Mithril.Reference/Serialization/Converters/RecipeIngredientConverter.cs#L17-L19), ~20% of all ingredient slots (3,803 / 19,019) are keyword-based. Most are the Crystal / Wax / Augment slots driving enchanting — the slots a player most wants to reverse-look-up.

#253 (now closed via PR #257) fixed the view-side half of this gap: `RecipesTabViewModel.BuildIngredientChips` no longer drops keyword ingredients from the **forward** view (the recipe's ingredient list). #254 closes the **reverse** half — the index that powers `Item → recipes that consume me`.

This is pure service-layer work in `Mithril.Shared.Reference`. The Silmarillion "Used in" section auto-benefits without UI changes; no consumer-side modification needed.

## Approach

**Widen the existing build logic; do not add a sibling index.** Same key (`item.InternalName`), same value (`IReadOnlyList<Recipe>`), just more entries — every keyword ingredient slot fans out into N entries, one per item the `ItemsMatching` lookup returns.

The fix is exactly at [src/Mithril.Shared/Reference/ReferenceDataService.cs:484-495](../../src/Mithril.Shared/Reference/ReferenceDataService.cs#L484-L495), inside `BuildRecipeCrossLinkIndices`:

```csharp
// CURRENT (lines 484-495)
foreach (var ing in ingredients.OfType<Mithril.Reference.Models.Recipes.RecipeItemIngredient>())
{
    if (!_items.TryGetValue(ing.ItemCode, out var item) || string.IsNullOrEmpty(item.InternalName))
        continue;
    if (!ingredient.TryGetValue(item.InternalName, out var list))
    {
        list = new List<Recipe>();
        ingredient[item.InternalName] = list;
    }
    if (!list.Contains(recipe))
        list.Add(recipe);
}
```

Replace with a switch that branches on the polymorphic ingredient subclass — item-keyed today, keyword-keyed via `_keywordIndex.ItemsMatching` going forward. Use the same `internalName → recipe` accumulator; the `!list.Contains(recipe)` de-dupe already covers the case where a recipe references the same item via both an `ItemCode` slot and a `Keywords`-matched slot.

```csharp
// SKETCH — adapt names to existing style
foreach (var ing in ingredients)
{
    switch (ing)
    {
        case RecipeItemIngredient itemIng:
            if (_items.TryGetValue(itemIng.ItemCode, out var item) && !string.IsNullOrEmpty(item.InternalName))
                AddTo(ingredient, item.InternalName, recipe);
            break;

        case RecipeKeywordIngredient kwIng when kwIng.ItemKeys.Count > 0:
            foreach (var matchedItem in _keywordIndex.ItemsMatching(kwIng.ItemKeys))
            {
                if (!string.IsNullOrEmpty(matchedItem.InternalName))
                    AddTo(ingredient, matchedItem.InternalName, recipe);
            }
            break;

        // default: any future ingredient subclass — skipped, future-proof.
    }
}
```

(`AddTo` is the existing pattern in the function — get-or-create the list, contains-then-add. Extract a local helper if it improves readability; the current inline form is fine.)

**Keyword-index ordering is the one non-trivial concern.** `BuildRecipeCrossLinkIndices` is called from both `ParseAndSwapItems` and `ParseAndSwapRecipes`. `_keywordIndex` is rebuilt during `ParseAndSwapItems` — verify it's swapped in **before** `BuildRecipeCrossLinkIndices` runs there, or the items-refresh path will use a stale keyword index. Likely fine as written (the existing build also reads `_items`, which has the same ordering constraint), but worth eyeballing the swap order.

**Update the interface docstring.** Drop the `RecipeItemIngredient`-only qualifier at [IReferenceDataService.cs:43-50](../../src/Mithril.Shared/Reference/IReferenceDataService.cs#L43-L50); rephrase to explain that keyword ingredients expand via `ItemKeywordIndex.ItemsMatching`. Keep the "Defaults to empty so test fakes don't need to opt into cross-linking" line — it still applies.

**Performance sanity check.** `ItemKeywordIndex.ItemsMatching` ([src/Mithril.Shared/Reference/ItemKeywordIndex.cs:44](../../src/Mithril.Shared/Reference/ItemKeywordIndex.cs#L44)) is `O(k)` over matched-item counts for `k` keys. For ~3,803 keyword slots × average matched-item count (likely tens for common keys like "Crystal", possibly hundreds for broad keys like "WeaponEquipment"), the per-refresh additional work is bounded but non-trivial. The whole `BuildRecipeCrossLinkIndices` already runs synchronously during item / recipe swap, so this is on the parse path, not a hot loop — measure once with a stopwatch on a real CDN load if you want to be sure, but no preemptive optimisation needed.

If index-build time materially regresses, fallback is a lazy sibling lookup (`RecipesByKeywordIngredient` keyed by keyword set) — but treat that as a follow-up, not v1 scope.

**Out of scope** (per #254):

- Companion bug #253 — already shipped via PR #257.
- Celebrimbor's `RecipeAggregator.BuildProducerLookup` — uses `MakeKeywordKey`; per #207's note, intentionally one-to-one for crafting-plan use cases.
- `EntityRef.KeywordSet(...)` — no entity kind needed; index stays keyed by item InternalName.
- Celebrimbor's #46 ("Sources tab support for keyword rows") — different module's aggregation UX, not the reverse-index plumbing.

## Files to modify

### 1. `BuildRecipeCrossLinkIndices` — switch instead of OfType

[src/Mithril.Shared/Reference/ReferenceDataService.cs:459-500](../../src/Mithril.Shared/Reference/ReferenceDataService.cs#L459-L500). Replace lines 484-495 per the sketch above. The `_keywordIndex` field is already a member of the class ([line 164](../../src/Mithril.Shared/Reference/ReferenceDataService.cs#L164)) — no DI plumbing needed.

The `RecipeKeywordIngredient` and `RecipeItemIngredient` types live in `Mithril.Reference.Models.Recipes`; the `using` for that namespace is already present at the top of `ReferenceDataService.cs`.

### 2. Interface docstring

[src/Mithril.Shared/Reference/IReferenceDataService.cs:43-50](../../src/Mithril.Shared/Reference/IReferenceDataService.cs#L43-L50). Replace the parenthetical with a sentence acknowledging the keyword-expansion semantics. Sketch:

```csharp
/// <summary>
/// Recipes indexed by the InternalName of any item they consume as an ingredient.
/// Includes both <see cref="RecipeItemIngredient"/> (direct item-code references)
/// and <see cref="RecipeKeywordIngredient"/> (keyword-matched slots — each slot
/// fans out to every item whose enriched keywords AND-match the slot's ItemKeys
/// via <see cref="ItemKeywordIndex.ItemsMatching"/>). Built whenever items.json
/// or recipes.json reloads. Powers the "Used in" cross-link section. Defaults to
/// empty so test fakes don't need to opt into cross-linking.
/// </summary>
```

### 3. Tests

**Modify:** [tests/Mithril.Shared.Tests/ReferenceDataServiceTests.cs](../../tests/Mithril.Shared.Tests/ReferenceDataServiceTests.cs)

Add a fixture pair and assertion:

- Item with `Keywords: ["Crystal"]` and a known `ItemCode` + `InternalName`.
- Recipe with one `RecipeKeywordIngredient { ItemKeys: ["Crystal"] }` slot.
- After loading both, `service.RecipesByIngredientItem[itemInternalName]` should contain the recipe.

A second test covering the AND-match semantics is worth adding: item with `Keywords: ["Crystal"]` only (no second keyword), recipe with `ItemKeys: ["Crystal", "Tier2"]` — recipe should **not** surface (the AND constraint isn't satisfied). This documents that the expansion respects the existing index semantics, not a looser any-match.

A third test: same item appears under the recipe via **both** an `ItemCode` slot **and** a matching keyword slot. Assert it appears in the list exactly once (the existing `!list.Contains` de-dupes correctly).

Existing `ItemKeywordIndex` test coverage at [tests/Mithril.Shared.Tests/Reference/ItemKeywordIndexTests.cs](../../tests/Mithril.Shared.Tests/Reference/ItemKeywordIndexTests.cs) is the shape to mirror for fixture construction.

**No test-fake changes needed.** Every consumer fake (~25 sites in test code) already returns an empty `RecipesByIngredientItem` via the interface default; they're unaffected by the build-logic widening.

## Verification

1. **Build:** `dotnet build Mithril.slnx` — warnings-as-errors is on.
2. **Targeted:** `dotnet test tests/Mithril.Shared.Tests --filter "FullyQualifiedName~ReferenceDataServiceTests"` — new tests pass.
3. **Full suite:** `dotnet test Mithril.slnx` — confirm nothing regressed. Pay attention to consumers that read `RecipesByIngredientItem`: Silmarillion's item-detail tests, Celebrimbor, Bilbo, Elrond. A widened index returning *more* recipes shouldn't break callers, but if any test asserts exact recipe counts under a specific item key, those assertions need updating to reflect the new keyword-matched entries.
4. **CDN parity:** `dotnet test tests/Mithril.Reference.Tests` — confirm the bundled-JSON validation harness still passes (no parser change, but defensive).
5. **Manual repro:**
   - `dotnet run --project src/Mithril.Shell`.
   - Silmarillion → Items tab → open any Crystal item (e.g. "Crystal" or a specific tier).
   - "Used in" section should list every enchanting recipe that has `ItemKeys: ["Crystal"]` in its ingredient list — pre-fix this section is empty / missing those recipes.
   - Cross-check by opening one of the surfaced enchanting recipes (`*E` suffix) on the Recipes tab — its ingredient list should now show both the item slot and the keyword slot as chips (the #253 fix that already shipped).
   - Confirm no duplicates: an item that's both `ItemCode`-referenced and keyword-matched by the same recipe appears in "Used in" exactly once.
6. **Perf check (optional):** stopwatch `BuildRecipeCrossLinkIndices` on a real CDN load — should remain sub-second. If it regresses meaningfully (>200ms), reconsider the lazy-sibling-index fallback noted above before merging.

## Commit / PR shape

Single PR against `main` (branch policy blocks direct push). Suggested branch: `feat/254-recipes-by-ingredient-keyword-expansion`. Conventional-commit message: `feat(mithril.reference): widen RecipesByIngredientItem to include keyword-matched recipes — #254`.

Likely diff size: ~50–80 lines across the build logic, interface docstring, and three new tests.

Closes #254. **Does not** close any silmarillion-labeled issue — the propagation through the "Used in" section is automatic.
