# Silmarillion Recipe MaxUses Chip Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Surface a recipe's per-character lifetime use cap (`Recipe.MaxUses`) as a chip in Silmarillion's recipe-detail pane.

**Architecture:** Add one computed string property `MaxUsesChip` to the existing `RecipeDetailViewModel` projection (mirroring `SkillRequirementChip`: a string that is empty when absent, so the view reuses the string-only `NullOrEmptyToVis` converter). Add a second chip `Border` to `RecipeDetailView.xaml`, co-located with the skill-requirement chip in a horizontal `WrapPanel`.

**Tech Stack:** C# / .NET 10, WPF, CommunityToolkit.Mvvm, xunit + FluentAssertions.

**Spec:** [docs/superpowers/specs/2026-05-15-silmarillion-recipe-maxuses-design.md](../specs/2026-05-15-silmarillion-recipe-maxuses-design.md)

---

## File Structure

- Modify: `src/Silmarillion.Module/ViewModels/RecipeDetailViewModel.cs` — add `MaxUsesChip` computed property (~3 lines + doc comment).
- Modify: `src/Silmarillion.Module/Views/RecipeDetailView.xaml` — wrap the existing skill-requirement chip and a new MaxUses chip in a `WrapPanel`.
- Modify: `tests/Silmarillion.Tests/ViewModels/RecipeDetailViewModelTests.cs` — extend the `SampleRecipe` helper with an optional `maxUses` param; add one `[Theory]` covering null/0/1/4.

No new files. The VM is shared by the master-detail right pane and the popup `RecipeDetailWindow`, so the chip surfaces in both with no extra work.

---

### Task 1: `MaxUsesChip` view-model property (TDD)

**Files:**
- Modify: `src/Silmarillion.Module/ViewModels/RecipeDetailViewModel.cs` (add property after `SkillRequirementChip`, ~line 75)
- Test: `tests/Silmarillion.Tests/ViewModels/RecipeDetailViewModelTests.cs`

- [ ] **Step 1: Add an optional `maxUses` parameter to the test's `SampleRecipe` helper**

In `tests/Silmarillion.Tests/ViewModels/RecipeDetailViewModelTests.cs`, replace the `SampleRecipe` helper (lines 12–28) with this version (adds the `maxUses` param + initializer line; everything else unchanged):

```csharp
    private static Recipe SampleRecipe(
        string internalName = "MakeTomatoSauce",
        string? skill = "Cooking",
        int skillLevel = 12,
        string? description = null,
        IReadOnlyList<string>? resultEffects = null,
        int? maxUses = null) => new()
    {
        Key = "recipe_1",
        InternalName = internalName,
        Name = "Make Tomato Sauce",
        Description = description ?? "Crush 3 tomatoes into sauce.",
        Skill = skill,
        SkillLevelReq = skillLevel,
        IconId = 4242,
        Ingredients = [],
        ResultEffects = resultEffects,
        MaxUses = maxUses,
    };
```

- [ ] **Step 2: Write the failing test**

Append this test to the `RecipeDetailViewModelTests` class in the same file (before the closing brace, after `ProducedItems_AreExposedAsProvided`):

```csharp
    [Theory]
    [InlineData(null, "")]
    [InlineData(0, "")]
    [InlineData(1, "Limited to 1 use")]
    [InlineData(4, "Limited to 4 uses")]
    public void MaxUsesChip_RendersLifetimeCap_OrEmptyWhenAbsent(int? maxUses, string expected)
    {
        // MaxUses appears only on Research-keyword recipes; it is a per-character
        // lifetime cap. Absent (null) or non-positive => no chip. 1 => singular.
        var vm = new RecipeDetailViewModel(
            SampleRecipe(maxUses: maxUses),
            ingredients: [],
            producedItems: [],
            resultEffectsText: []);

        vm.MaxUsesChip.Should().Be(expected);
    }
```

- [ ] **Step 3: Run the test to verify it fails**

Run: `dotnet test tests/Silmarillion.Tests --filter "FullyQualifiedName~RecipeDetailViewModelTests.MaxUsesChip"`

Expected: FAIL — compile error, `'RecipeDetailViewModel' does not contain a definition for 'MaxUsesChip'`.

- [ ] **Step 4: Implement `MaxUsesChip`**

In `src/Silmarillion.Module/ViewModels/RecipeDetailViewModel.cs`, immediately after the `SkillRequirementChip` property (the block ending at line 75), add:

```csharp

    /// <summary>
    /// Per-character lifetime use cap, e.g. "Limited to 2 uses". Only Research-keyword
    /// recipes (WeatherWitching/FireMagic/IceMagic) carry <see cref="Recipe.MaxUses"/>;
    /// it is never per-day/per-session. Empty string when absent or non-positive — the
    /// view hides the chip on empty (string-only <c>NullOrEmptyToVis</c>, matching
    /// <see cref="SkillRequirementChip"/>). <c>MaxUses == 1</c> renders singular.
    /// </summary>
    public string MaxUsesChip =>
        Recipe.MaxUses is int n && n > 0
            ? $"Limited to {n} use{(n == 1 ? "" : "s")}"
            : "";
```

- [ ] **Step 5: Run the test to verify it passes**

Run: `dotnet test tests/Silmarillion.Tests --filter "FullyQualifiedName~RecipeDetailViewModelTests.MaxUsesChip"`

Expected: PASS — 4 cases (null, 0, 1, 4).

- [ ] **Step 6: Commit**

```bash
git add src/Silmarillion.Module/ViewModels/RecipeDetailViewModel.cs tests/Silmarillion.Tests/ViewModels/RecipeDetailViewModelTests.cs
git commit -m "feat(silmarillion): MaxUsesChip on recipe-detail VM

Co-Authored-By: Claude Opus 4.7 (1M context) <noreply@anthropic.com>"
```

---

### Task 2: Render the MaxUses chip in the recipe-detail view

**Files:**
- Modify: `src/Silmarillion.Module/Views/RecipeDetailView.xaml` (the skill-requirement chip block, lines 44–50)

- [ ] **Step 1: Wrap both chips in a horizontal `WrapPanel`**

In `src/Silmarillion.Module/Views/RecipeDetailView.xaml`, replace the skill-requirement chip block (the `<!-- Skill requirement chip -->` comment and the `Border` immediately following it, lines 44–50) with:

```xml
            <!-- Recipe attribute chips: skill requirement + (Research-only) lifetime
                 use cap. WrapPanel keeps them on one line and wraps if cramped. Each
                 chip self-hides on an empty string via NullOrEmptyToVis. -->
            <WrapPanel Orientation="Horizontal" Margin="0,0,0,10">
                <Border Background="#FF252525" BorderBrush="#33FFFFFF" BorderThickness="1"
                        CornerRadius="3" Padding="6,2" HorizontalAlignment="Left" Margin="0,0,6,0"
                        Visibility="{Binding SkillRequirementChip, Converter={StaticResource NullOrEmptyToVis}}">
                    <TextBlock Text="{Binding SkillRequirementChip}" Foreground="#CCFFFFFF"
                               FontSize="{DynamicResource AppFontSizeHint}"/>
                </Border>
                <Border Background="#FF252525" BorderBrush="#33FFFFFF" BorderThickness="1"
                        CornerRadius="3" Padding="6,2" HorizontalAlignment="Left" Margin="0,0,6,0"
                        Visibility="{Binding MaxUsesChip, Converter={StaticResource NullOrEmptyToVis}}">
                    <TextBlock Text="{Binding MaxUsesChip}" Foreground="#CCFFFFFF"
                               FontSize="{DynamicResource AppFontSizeHint}"/>
                </Border>
            </WrapPanel>
```

Note: the per-chip bottom margin previously on the single `Border` (`Margin="0,0,0,10"`) moves to the `WrapPanel`; chips use `Margin="0,0,6,0"` for inter-chip spacing.

- [ ] **Step 2: Build to verify XAML compiles**

Run: `dotnet build src/Silmarillion.Module`

Expected: Build succeeded, 0 errors. (Warnings-as-errors is on; a XAML binding typo would fail here.)

- [ ] **Step 3: Run the full Silmarillion test suite (regression check)**

Run: `dotnet test tests/Silmarillion.Tests`

Expected: PASS — all tests, including the Task 1 `MaxUsesChip` theory.

- [ ] **Step 4: Manual visual verification**

Run: `dotnet run --project src/Mithril.Shell`

In the Silmarillion tab → Recipes, open a recipe with no cap (e.g. "Make Tomato Sauce" / any Cooking recipe): only the skill chip shows. Open a Research recipe (search `WeatherWitching` — e.g. internal name `WeatherWitching1`): expect a second chip `[Limited to 2 uses]` beside the skill chip. Open `WeatherWitching_LitanyOfStillness` (MaxUses 1): expect `[Limited to 1 use]` (singular). Confirm the chips sit on one row and the skill chip is unchanged.

- [ ] **Step 5: Commit**

```bash
git add src/Silmarillion.Module/Views/RecipeDetailView.xaml
git commit -m "feat(silmarillion): recipe-detail MaxUses cap chip

Co-Authored-By: Claude Opus 4.7 (1M context) <noreply@anthropic.com>"
```

---

## Self-Review

**Spec coverage:**
- "Add computed string property mirroring `SkillRequirementChip`" → Task 1, Step 4. ✓
- Singular/plural ("1 use" vs "N uses") → Task 1 Step 4 ternary; tested Step 2 (`1`→singular, `4`→plural). ✓
- "null/0 → empty, chip absent" → tested Task 1 Step 2 (`null`, `0` → `""`); view hides via `NullOrEmptyToVis` Task 2 Step 1. ✓
- "WrapPanel row: `[Cooking 30] [Limited to 2 uses]`, identical chip style, `NullOrEmptyToVis`" → Task 2 Step 1. ✓
- "Unit test the four cases, no view-layer test" → Task 1 `[Theory]`; Task 2 uses build + manual verify only. ✓
- Elrond out of scope → no Elrond task. ✓ (deferred to its larger refactor per spec)

**Placeholder scan:** No TBD/TODO/"handle edge cases"/vague steps. All code blocks are complete and copy-pasteable. ✓

**Type consistency:** Property is `MaxUsesChip` (string) everywhere — VM definition (Task 1 S4), test assertions (Task 1 S2), both XAML bindings (Task 2 S1). `Recipe.MaxUses` is `int?` (Recipe.cs:75); pattern-match `is int n && n > 0` handles null+non-positive. Test helper param `int? maxUses` matches. ✓
