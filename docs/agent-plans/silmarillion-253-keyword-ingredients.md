# Silmarillion: keyword ingredients dropped from recipe ingredients list

**Tracked in:** #253.

## Context

`RecipesTabViewModel.BuildIngredientChips` ([src/Silmarillion.Module/ViewModels/RecipesTabViewModel.cs:142-148](../../src/Silmarillion.Module/ViewModels/RecipesTabViewModel.cs#L142-L148)) filters ingredients with `.OfType<RecipeItemIngredient>()`, silently discarding every `RecipeKeywordIngredient` slot — the `{ "ItemKeys": [...] }` AND-matched keyword shape used by ~20% of all ingredient slots (3,803 of 19,019 per the audit comment in [src/Mithril.Reference/Serialization/Converters/RecipeIngredientConverter.cs:17-19](../../src/Mithril.Reference/Serialization/Converters/RecipeIngredientConverter.cs#L17-L19)). Every enchanting recipe ending `…E` and many crafting recipes (e.g. anything taking "Any Crystal") currently renders missing ingredients in Silmarillion's recipe detail.

#207's spec described recipe ingredients as "list of clickable item chips" without distinguishing the keyword shape, so the gap landed in v1 unnoticed rather than as a deliberate scope cut.

Three sibling modules already handle the keyword shape consistently via `kwIng.Desc ?? ItemKeywordIndex.Humanise(kwIng.ItemKeys)`:

- [`Celebrimbor.RecipeRowViewModel.ProjectIngredientChip`](../../src/Celebrimbor.Module/ViewModels/RecipeRowViewModel.cs#L33-L52) — `case RecipeKeywordIngredient kwIng` with an "any {…}" label
- [`Elrond.SkillAdvisorEngine`](../../src/Elrond.Module/Services/SkillAdvisorEngine.cs#L61-L63)
- [`Bilbo.CraftableRecipeCalculator`](../../src/Bilbo.Module/Domain/CraftableRecipeCalculator.cs#L48-L52)

Silmarillion is the outlier. The fix is to replace the `OfType` filter with a `switch` mirroring Celebrimbor's shape.

## Approach

**Switch over `RecipeIngredient`, not filter.** Replace the `.OfType<RecipeItemIngredient>()` projection with a switch that emits a chip for each known subclass:

- `RecipeItemIngredient` → existing path (resolve item, navigable `EntityChipVm`).
- `RecipeKeywordIngredient` with non-empty `ItemKeys` → non-navigable `EntityChipVm` captioned `kwIng.Desc ?? "any " + ItemKeywordIndex.Humanise(kwIng.ItemKeys)`.
- `default` → null (skipped by the existing `.Where(c => c is not null)` filter); preserves forward compatibility with any future ingredient subclass.

**Design call required: `EntityChipVm.Reference` is non-nullable.** ([src/Mithril.Shared.Wpf/EntityChipVm.cs:12-16](../../src/Mithril.Shared.Wpf/EntityChipVm.cs#L12-L16).) Keyword ingredients have no entity to point at — keyword-sets aren't a tabbed `EntityKind`. Two options:

1. **Synthesize an anchor `EntityRef`** (e.g. `EntityRef.Item("")` or a sentinel) and rely on `IsNavigable=false` to suppress the click handler in the `EntityChip` control. Smallest diff; keeps `EntityChipVm` shape stable. **Recommended for this PR** — matches the spirit of the v1 "degrade non-tabbed kinds to plain text" pattern that's already shipping.
2. **Make `EntityChipVm.Reference` nullable** to match the existing `ItemSourceChipVm` shape ([same file lines 24-29](../../src/Mithril.Shared.Wpf/EntityChipVm.cs#L24-L29)). Tidier long-term but touches every chip consumer and the `EntityChip` control's binding. **Defer to a follow-up issue** if the codebase wants consistency between the two chip types.

Before committing to option 1, **inspect `src/Mithril.Shared.Wpf/EntityChip.xaml`** to confirm it doesn't blow up dereferencing `Reference` when `IsNavigable=false`. If the control unconditionally binds `Reference.InternalName` (or similar) for tooltips/automation, option 1 needs a careful sentinel choice or option 2 becomes mandatory.

**Out of scope** (intentionally — flagged in #253):

- An `EntityRef.KeywordSet(...)` kind so keyword chips become navigable to a filtered item list. Filed separately if desired.
- The companion `IReferenceDataService.RecipesByIngredientItem` reverse-index widening — that's **#254** (`module:mithril.reference`), the service-layer half of this gap. Independent PR.
- Iconography for keyword-set chips. Default to `IconId: 0` (no icon) for v1; the existing chip control already handles `IconId == 0` gracefully (`RecipeRowViewModel` does the same).

## Files to modify

### 1. `RecipesTabViewModel.BuildIngredientChips` — switch instead of filter

[src/Silmarillion.Module/ViewModels/RecipesTabViewModel.cs:142-148](../../src/Silmarillion.Module/ViewModels/RecipesTabViewModel.cs#L142-L148).

Current:

```csharp
private IReadOnlyList<EntityChipVm> BuildIngredientChips(Recipe recipe) =>
    (recipe.Ingredients ?? (IReadOnlyList<RecipeIngredient>)Array.Empty<RecipeIngredient>())
        .OfType<RecipeItemIngredient>()
        .Select(ing => BuildItemChip(ing.ItemCode, ing.StackSize, percentChance: null))
        .Where(c => c is not null)
        .Select(c => c!)
        .ToList();
```

Replace with a switch yielding chips per subclass. Sketch (the agent should adapt names to match existing style):

```csharp
private IReadOnlyList<EntityChipVm> BuildIngredientChips(Recipe recipe) =>
    (recipe.Ingredients ?? (IReadOnlyList<RecipeIngredient>)Array.Empty<RecipeIngredient>())
        .Select(BuildIngredientChip)
        .Where(c => c is not null)
        .Select(c => c!)
        .ToList();

private EntityChipVm? BuildIngredientChip(RecipeIngredient ingredient) => ingredient switch
{
    RecipeItemIngredient itemIng => BuildItemChip(itemIng.ItemCode, itemIng.StackSize, percentChance: null),
    RecipeKeywordIngredient kwIng when kwIng.ItemKeys.Count > 0 => BuildKeywordChip(kwIng),
    _ => null,
};

private static EntityChipVm BuildKeywordChip(RecipeKeywordIngredient kwIng)
{
    var label = kwIng.Desc ?? $"any {ItemKeywordIndex.Humanise(kwIng.ItemKeys)}";
    // Anchor ref: non-navigable, see "Design call" above. Confirm EntityChip.xaml tolerates this.
    return new EntityChipVm(label, IconId: 0, Reference: SentinelKeywordRef, IsNavigable: false);
}

private static readonly EntityRef SentinelKeywordRef = EntityRef.Item(string.Empty);
```

`ItemKeywordIndex` lives in `Mithril.Shared.Reference` — add a `using` if not already present (Celebrimbor's row VM imports it; mirror that).

### 2. Audit `EntityChip` control for `Reference` dereferences

[src/Mithril.Shared.Wpf/EntityChip.xaml](../../src/Mithril.Shared.Wpf/EntityChip.xaml) (and the `.cs` code-behind if any).

Skim for any binding that reads `Reference.InternalName` / `Reference.Kind` unconditionally. If found and `IsNavigable=false` doesn't already gate it, either:

- Add an `IsNavigable=false` guard in the binding (`Visibility` / fallback), or
- Promote `Reference` to nullable per option 2 above.

Don't proceed until this audit is done — a sentinel `EntityRef.Item("")` should be safe but it's worth eyeballing.

### 3. Test fixture — `RecipesTabViewModelTests`

[tests/Silmarillion.Tests/ViewModels/RecipesTabViewModelTests.cs](../../tests/Silmarillion.Tests/ViewModels/RecipesTabViewModelTests.cs) — existing file, follow the `StubReferenceData` + `Recipe { ... Ingredients = [...] }` pattern shown at lines 33-46.

Add at minimum:

- A test where a recipe has one `RecipeItemIngredient` AND one `RecipeKeywordIngredient`. Assert the detail VM exposes **both** chips (count = 2), the keyword chip's `DisplayName` matches `kwIng.Desc` when set, and `IsNavigable` is `false` for the keyword chip.
- A test where a recipe has a `RecipeKeywordIngredient` with `Desc = null` and multiple `ItemKeys`. Assert the chip's `DisplayName` is the `"any "` + humanised form.

Reuse `NavFactory.WithKinds(...)` to keep `IsNavigable` assertions stable.

`Recipe.Ingredients` is `IReadOnlyList<RecipeIngredient>` — instantiate with `[new RecipeKeywordIngredient { Desc = ..., ItemKeys = [...], StackSize = 1 }]`. Inspect `RecipeKeywordIngredient`'s actual property names in `src/Mithril.Reference/Models/Recipes/` if the constructor shape differs.

## Verification

1. **Build:** `dotnet build Mithril.slnx` — warnings-as-errors is on, so no new CS warnings.
2. **Tests:** `dotnet test tests/Silmarillion.Tests --filter "FullyQualifiedName~RecipesTabViewModelTests"` — new tests pass, existing tests still pass.
3. **Full test suite:** `dotnet test Mithril.slnx` — confirm nothing broke (especially Celebrimbor / Bilbo / Elrond, which share the keyword-ingredient pattern but project differently).
4. **Manual repro:**
   - `dotnet run --project src/Mithril.Shell`
   - Open Silmarillion → Recipes tab.
   - Query for a recipe ending `E` (any enchanting recipe). Pre-fix: ingredients section shows N − 1 chips. Post-fix: shows N chips, with the keyword slot captioned `"any Crystal"` (or similar).
   - Hover the keyword chip — confirm no click navigation occurs (it's non-navigable by design).
5. **Confirm no regression on item recipes** — pick a plain crafting recipe with only item ingredients; chip count and click behaviour unchanged.

## Commit / PR shape

Single commit on a feature branch (`fix/silmarillion-keyword-ingredients` or similar), opened as a PR against `main`. Branch policy blocks direct pushes to main. The roadmap doc at `docs/silmarillion-roadmap.md` is not part of this fix — leave it for its own PR if not already merged.

Closes #253. **Does not** close #254 (the service-layer companion).
