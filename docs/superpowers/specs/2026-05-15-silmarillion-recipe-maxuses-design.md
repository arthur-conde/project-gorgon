# Silmarillion recipe detail — surface `recipes[].MaxUses`

**Status:** Approved design, pending implementation plan.

## Problem

`Mithril.Reference.Models.Recipes.Recipe.MaxUses` (`int?`, [Recipe.cs:75](../../../src/Mithril.Reference/Models/Recipes/Recipe.cs#L75)) is parsed from `recipes.json` but rendered nowhere in the UI. Silmarillion's recipe-detail pane omits it entirely.

## Semantics (verified against bundled `recipes.json`)

`MaxUses` is present on **91 of 4427** recipes. Every one is a `Research`-keyword recipe, across exactly three skills: WeatherWitching (37), FireMagic (30), IceMagic (24). Values range 1–10. None produce `ResultItems`; all carry `ResultEffects`.

Two description shapes, **same meaning**:

- **Repeatable discovery** (~78): *"You can discover N abilities by repeatedly completing this recipe"* → `MaxUses = N`.
- **One-time / limited ritual** (~13, mostly `MaxUses: 1`, one `=10`): *"The ritual need only be performed once; the effects are permanent"* (e.g. +5 Max Tempest Energy).

In every case `MaxUses` is a **per-character lifetime cap** on how many times the recipe yields its benefit. It is never per-day, per-session, or per-instance. Therefore a single "Limited to N use(s)" framing is accurate across the entire set. `MaxUses: 1` must read **"Limited to 1 use"** (singular), all others plural.

## Scope

In scope: **display only**, **Silmarillion only**. Add a recipe-detail chip showing the lifetime cap, present only when `MaxUses` is non-null and positive (~2% of recipes; chip absent otherwise).

Out of scope (deliberately deferred): Elrond. Elrond's `CompletionsToLevel` / efficiency math is wrong for a capped research recipe (you cannot grind a 2-use recipe to level), and Elrond should eventually surface and respect the cap — but Elrond is undergoing a larger refactor, so this is left to that effort rather than a standalone follow-up.

## Design

### `RecipeDetailViewModel.cs`

Add one computed string property, mirroring the existing `SkillRequirementChip` convention (string + empty-when-absent, so the view uses the string-only `NullOrEmptyToVis` and avoids the `int?`-converter trap recorded in project memory):

```csharp
public string MaxUsesChip =>
    Recipe.MaxUses is int n && n > 0
        ? $"Limited to {n} use{(n == 1 ? "" : "s")}"
        : "";
```

### `RecipeDetailView.xaml`

Wrap the existing skill-requirement `Border` chip plus a new chip of identical style in a horizontal `WrapPanel`, so they read as a row of recipe attributes:

```
[Cooking 30]  [Limited to 2 uses]
```

The new chip:
- Same `Background`/`BorderBrush`/`CornerRadius`/`Padding`/`Foreground`/`FontSize` as the skill-requirement chip.
- `Visibility="{Binding MaxUsesChip, Converter={StaticResource NullOrEmptyToVis}}"` — collapsed on the ~98% of recipes with no cap.
- `Text="{Binding MaxUsesChip}"`.

Both chips remain left-aligned; the `WrapPanel` keeps them on one line when space allows and wraps gracefully if not.

## Testing

Unit test `MaxUsesChip` on `RecipeDetailViewModel`:

| `Recipe.MaxUses` | Expected `MaxUsesChip` |
|---|---|
| `null` | `""` |
| `0` | `""` |
| `1` | `"Limited to 1 use"` |
| `4` | `"Limited to 4 uses"` |

No view-layer test — consistent with the pane's existing VM-only coverage.

## Risks / notes

- `NullOrEmptyToVis` is string-only (project memory: bitten twice on object-typed bindings). The string-property approach is deliberately chosen to stay on the safe, already-proven path used by `SkillRequirementChip`.
- The `RecipeDetailViewModel` is shared by both the master-detail right pane and the popup `RecipeDetailWindow`; the chip surfaces in both with no extra work.
