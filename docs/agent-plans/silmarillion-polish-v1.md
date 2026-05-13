# Silmarillion polish v1 + DeepLink registry refactor

**Tracked in:** #229, #231, #234 (bundled); precursor refactor unfiled.

## Context

Three #207 follow-up polish issues for the Silmarillion reference browser, bundled with a precursor refactor of [DeepLinkRouter.cs](../../src/Mithril.Shared.Wpf/Modules/DeepLinkRouter.cs):

- **#229** — add `mithril://silmarillion/item/<name>` and `mithril://silmarillion/recipe/<name>` module-scoped routes alongside the existing `mithril://item/` / `mithril://recipe/` schemes.
- **#231** — replace the master-detail card layout (~80px per row, ~17 visible) with a compact two-line row layout (~36px per row, ~40+ visible).
- **#234** — move the cross-link sections (Sources / Produced by / Used in) from the bottom of [ItemDetailView.xaml](../../src/Mithril.Shared.Wpf/ItemDetailView.xaml) to immediately after Description, so sparse items don't force the user's eye past a dead zone of empty Effects-region to reach the recipes that produce/consume them.

The bundled refactor: `DeepLinkRouter`'s big `switch (action)` has grown to six structurally-identical cases (validate payload regex → null-check optional import target → call one method on it → log a diagnostic), and #229 is the first multi-segment route that doesn't fit the existing shape. Adding case #7 inline would mean a nested switch; extracting to a handler registry is the natural home for the new route. Bundled here rather than deferred because the registry is *what makes #229 clean*.

#235 (recipe Sources section + `IReferenceDataService.RecipeSources` plumbing) is intentionally **out of scope** for this PR — it's the only follow-up that needs new reference-data infrastructure and benefits from being a separate review.

## Approach

**The router becomes a dispatcher.** `IDeepLinkHandler` defines `Action` (the URI host segment) and `TryHandle(string subPath, IDiagnosticsSink? diag)`. Each existing branch becomes a handler class owning its payload grammar; the router takes `IEnumerable<IDeepLinkHandler>` via DI and dispatches by host. Adding the silmarillion route is then just registering one more handler that internally parses its two-segment sub-path.

**Item/Recipe handlers stay in the shared layer.** They depend only on `IReferenceNavigator`, which is shell-registered as `NoOpReferenceNavigator` and overridden by Silmarillion via last-singleton-wins DI. Keeping these two handlers in `Mithril.Shared.Wpf` preserves the existing degradation path (Silmarillion uninstalled → URIs are accepted but no-op). The silmarillion handler lives in Silmarillion.Module since it's the module's own scheme.

**Card-shaped rows become row-shaped rows.** The card template chosen for #207 was modelled after Celebrimbor's RecipeCard, but Celebrimbor uses cards as inspect-popups; Silmarillion uses them for browsable catalogue navigation, which needs density. Two-line compact (24px icon + name on line 1, dim subtitle on line 2) was chosen over single-line+tail because the subtitle (equip slot / skill+level) is genuinely useful at scan time and the second line costs ~8px versus the four-line card it replaces.

**ItemDetailView's reading order is restructured by dropping the `Height="*"` slack-absorber.** The outer `Grid` with 23 explicit `RowDefinition`s becomes a `DockPanel` with the internal-name footer `Dock="Bottom"` and a `StackPanel` body. With the footer pinned by `DockPanel`, no body row needs to absorb slack, so the cross-link sections move from rows 19–21 to immediately after Description — the structural change *enables* the reorder. This mirrors the pattern already used by [RecipeDetailView.xaml](../../src/Silmarillion.Module/Views/RecipeDetailView.xaml), making the two detail views structurally consistent.

## Files to modify

### 1. `IDeepLinkHandler` — new interface

New file `src/Mithril.Shared.Wpf/Modules/IDeepLinkHandler.cs`:

```csharp
public interface IDeepLinkHandler
{
    /// <summary>The first path segment after mithril://. Must be lowercase ASCII.</summary>
    string Action { get; }

    /// <summary>
    /// Handle the remainder of the URI path (everything after the host segment,
    /// with the leading '/' stripped). Implementations own their payload grammar
    /// and any per-handler diagnostic messages. Return false for validation
    /// failure or missing dependency; true on successful dispatch.
    /// </summary>
    bool TryHandle(string subPath, IDiagnosticsSink? diag);
}
```

### 2. Extract per-action handlers

New files under `src/Mithril.Shared.Wpf/Modules/`:

- `ItemDeepLinkHandler.cs` — `Action = "item"`. Validates against `EntityPayloadPattern` (the existing regex `^[A-Za-z0-9_]{1,128}$`). Calls `_navigator.Open(EntityRef.Item(payload))`. Takes `IReferenceNavigator` via constructor.
- `RecipeDeepLinkHandler.cs` — same shape, `EntityRef.Recipe(payload)`.

The two regex constants `EntityPayloadPattern`, `ListPayloadPattern`, `PippinPayloadPattern`, `LegolasPayloadPattern` move with their owning handler. Keep them `private static readonly` on each handler class — they're not shared across handlers.

New files in each owning module project (`src/<Module>.Module/Navigation/<Module>DeepLinkHandler.cs`):

- `Celebrimbor.Module/Navigation/CraftListDeepLinkHandler.cs` — `Action = "list"`, calls `ICraftListImportTarget.ImportFromLinkPayload`. Project already references `Mithril.Shared.Wpf`.
- `Pippin.Module/Navigation/PippinDeepLinkHandler.cs` — `Action = "pippin"`.
- `Legolas.Module/Navigation/LegolasDeepLinkHandler.cs` — `Action = "legolas"`.
- `Elrond.Module/Navigation/ElrondDeepLinkHandler.cs` — `Action = "elrond"`.

Each module's `Register(IServiceCollection)` adds `services.AddSingleton<IDeepLinkHandler, FooDeepLinkHandler>()`. Item and Recipe handlers register from shell DI ([ServiceCollectionExtensions.cs](../../src/Mithril.Shared/DependencyInjection/ServiceCollectionExtensions.cs)) since they live in the shared layer.

### 3. `DeepLinkRouter` — rewrite as dispatcher

[DeepLinkRouter.cs](../../src/Mithril.Shared.Wpf/Modules/DeepLinkRouter.cs):

- Constructor takes `IEnumerable<IDeepLinkHandler> handlers` and `IDiagnosticsSink? diag`. Builds `Dictionary<string, IDeepLinkHandler>` keyed by `Action.ToLowerInvariant()`. Duplicate `Action` registrations throw at construction time (DI ordering bug — fail loud).
- `Handle(string uri)` becomes: validate well-formed URI, check scheme is `mithril`, look up handler by host, call `handler.TryHandle(parsed.AbsolutePath.TrimStart('/'), _diag)`. Unknown host → diag info + return false (existing behavior).
- File shrinks from ~170 lines to ~40.

### 4. `SilmarillionDeepLinkHandler` — new module-scoped route

New file `src/Silmarillion.Module/Navigation/SilmarillionDeepLinkHandler.cs`:

- `Action = "silmarillion"`. Takes `IReferenceNavigator` via constructor.
- `TryHandle` splits `subPath` on first `/` → `(kind, name)`. Validates `name` against `EntityPayloadPattern`. Unknown `kind` → diag info + return false. Dispatch:
  - `kind == "item"` → `_navigator.Open(EntityRef.Item(name))`
  - `kind == "recipe"` → `_navigator.Open(EntityRef.Recipe(name))`
- Register from [SilmarillionModule.Register](../../src/Silmarillion.Module/SilmarillionModule.cs).

### 5. Compact row templates

[src/Silmarillion.Module/Views/Resources.xaml](../../src/Silmarillion.Module/Views/Resources.xaml) — rewrite both DataTemplates. Border removed; row is a `DockPanel` with `IconImage` on the left and a `StackPanel` body.

```xml
<DataTemplate x:Key="ItemCardTemplate">
    <DockPanel Margin="0" LastChildFill="True">
        <wpf:IconImage DockPanel.Dock="Left" IconId="{Binding IconId}"
                       Width="24" Height="24" Margin="6,3,6,3" VerticalAlignment="Center"/>
        <StackPanel VerticalAlignment="Center" Margin="0,3,6,3">
            <TextBlock Text="{Binding Name}" FontWeight="SemiBold"
                       Foreground="#FFD4A847" TextTrimming="CharacterEllipsis"
                       FontSize="{DynamicResource AppFontSizeHint}"/>
            <TextBlock Text="{Binding EquipSlot, Converter={StaticResource CamelCaseSplit}}"
                       Foreground="#88FFFFFF"
                       FontSize="{DynamicResource AppFontSizeSmall}"
                       TextTrimming="CharacterEllipsis"
                       Visibility="{Binding EquipSlot, Converter={StaticResource NullOrEmptyToVis}}"/>
        </StackPanel>
    </DockPanel>
</DataTemplate>
```

The recipe template mirrors this shape, replacing the subtitle binding with `SkillDisplayName` + level (hidden when `SkillDisplayName` is empty via `NullOrEmptyToVis`).

Key keeps `*CardTemplate` despite no longer being cards — renaming would ripple into both Silmarillion tab views and isn't worth the diff. Names are wallpaper.

`TextTrimming="CharacterEllipsis"` on both lines preserves the 320px-wide listbox constraint without wrap-jumping row heights.

### 6. ItemDetailView restructure

[ItemDetailView.xaml](../../src/Mithril.Shared.Wpf/ItemDetailView.xaml) — rewrite the top-level structure:

```xml
<Border Padding="14,12">
    <DockPanel>
        <TextBlock DockPanel.Dock="Bottom" Text="{Binding InternalName}"
                   Foreground="#88FFFFFF"
                   FontFamily="{DynamicResource AppMonoFontFamily}"
                   FontSize="{DynamicResource AppFontSizeSmall}"
                   Margin="0,8,0,0" HorizontalAlignment="Right"/>
        <StackPanel>
            <!-- Header -->
            <!-- Description -->
            <!-- Sources (was row 19) -->
            <!-- Produced by (was row 20) -->
            <!-- Used in (was row 21) -->
            <!-- Skill requirements (was row 2) -->
            <!-- Effects (was row 3; Height="*" gone) -->
            <!-- Augmentation, WaxAugments, WaxItems, AugmentPools, TaughtRecipes,
                 EffectTags, ResearchProgress, XpGrants, WordsOfPower,
                 LearnedAbilities, ProducedItems, EquipBonuses,
                 CraftingEnhancements, RecipeCooldowns,
                 UnpreviewableExtractions (rest of optional sections,
                 same relative order as today) -->
        </StackPanel>
    </DockPanel>
</Border>
```

All sections become `StackPanel` children; their `Visibility` bindings already hide them when empty. The host `ContentControl` keeps its `MinHeight="{Binding ViewportHeight, ElementName=DetailScroller}"` binding (set in [ItemsTabView.xaml:78-85](../../src/Silmarillion.Module/Views/ItemsTabView.xaml#L78-L85)), so the DockPanel stretches to viewport and the footer pins to the bottom of the viewport for short content.

Cross-link section internal ordering preserved (Sources → Produced by → Used in), matching the issue's recommended layout.

### 7. Wiki — Deep-Linking page

[Mithril wiki](https://github.com/moumantai-gg/mithril/wiki) — update the Deep-Linking page (or equivalent reference) to:

- Document `mithril://silmarillion/item/<internalName>` and `mithril://silmarillion/recipe/<internalName>` as the **preferred** forms.
- Mark `mithril://item/<internalName>` / `mithril://recipe/<internalName>` as **legacy but still supported**.
- Update the example in [AboutSettingsView.xaml:146](../../src/Mithril.Shell/Views/AboutSettingsView.xaml#L146) to use the module-scoped form.

Wiki commit goes in [project-gorgon.wiki](../../../project-gorgon.wiki) (master branch — see memory `workspace_repo_map.md`).

## Testing

### Router refactor (commit 1)

[tests/Mithril.Shared.Tests/Modules/DeepLinkRouterTests.cs](../../tests/Mithril.Shared.Tests/Modules/DeepLinkRouterTests.cs) — existing test cases stay; refactor the test setup to register handlers via DI rather than passing import targets to the router constructor. Per-handler unit tests can live alongside each handler later — not blocking, since coverage is preserved end-to-end through the router tests.

### Silmarillion handler (commit 2)

New file `tests/Silmarillion.Tests/Navigation/SilmarillionDeepLinkHandlerTests.cs`:

- `TryHandle_ItemKind_OpensItem` — `silmarillion/item/CraftedLeatherBoots5` → navigator opens `EntityRef.Item("CraftedLeatherBoots5")`.
- `TryHandle_RecipeKind_OpensRecipe` — `silmarillion/recipe/SkinSheep` → navigator opens `EntityRef.Recipe("SkinSheep")`.
- `TryHandle_UnknownKind_ReturnsFalse` — `silmarillion/npc/Marna` → false, navigator not invoked, diag breadcrumb logged.
- `TryHandle_InvalidPayload_ReturnsFalse` — `silmarillion/item/<garbage with separators>` → false.
- `TryHandle_NoSecondSegment_ReturnsFalse` — `silmarillion/item` (no name) → false.
- `TryHandle_EmptySubPath_ReturnsFalse` — `silmarillion/` or `silmarillion` → false.

Add one router-level integration test (`Handle_SilmarillionRoute_DispatchesToHandler`) to confirm DI registration works end-to-end.

### Compact rows (commit 3)

Manual verification only — XAML template change with no behavioral path to unit-test. Launch `dotnet run --project src/Mithril.Shell`, open Silmarillion's Items tab, confirm:

- Rows are visually compact (24px icon, two-line text).
- Subtitle hides when empty (e.g. for items with no equip slot).
- Hover/selection accent still works.
- Scroll performance unchanged (virtualization still on).
- Both tabs work (Items + Recipes).

### ItemDetailView restructure (commit 4)

Manual verification only — XAML restructure with no behavioral change. Confirm:

- Sparse item (e.g. a lorebook or candle-wick material): cross-link sections (Sources / Produced by / Used in) appear immediately after description; internal-name footer pins to the bottom of the right pane.
- Effect-heavy item (e.g. a weapon or armor piece): all sections render in the new order; scrolling works; no visual regressions.
- Popup [ItemDetailWindow.xaml](../../src/Mithril.Shared.Wpf/ItemDetailWindow.xaml) (which hosts the same view) still renders correctly. The popup sizes to content so the footer-pin behavior is irrelevant there; verify the new section order looks reasonable.

## Out of scope

- **#235 (recipe Sources section + `IReferenceDataService.RecipeSources` plumbing)** — separate PR; needs new parser plumbing and is the largest of the four follow-ups. Will land independently.
- **Renaming `*CardTemplate` → `*RowTemplate`** in `Resources.xaml`. Names are wallpaper; rename would ripple into both tab views without changing behavior.
- **Removing legacy `mithril://item/` and `mithril://recipe/` schemes.** Issue calls for soft-deprecation (keep working, document the preferred form). No removal scheduled.
- **Per-handler unit tests for the extracted Item/Recipe/CraftList/Pippin/Legolas/Elrond handlers.** Coverage is preserved through the existing router tests; per-handler tests are a follow-up nicety, not a blocker.

## Commit sequence

1. **Router → handler-registry refactor** (`IDeepLinkHandler`, extract six handlers, rewrite router, re-wire tests, register each module's handler in its `Register(IServiceCollection)`). No new URI behavior; existing tests pass.
2. **Add Silmarillion deep-link handler** + tests + register in `SilmarillionModule.Register`. New URI behavior added; legacy URIs unchanged.
3. **Compact row templates** in `Resources.xaml`. Manual visual verification.
4. **ItemDetailView restructure**. Manual visual verification on both sparse and effect-heavy items.
5. **Wiki update** + `AboutSettingsView.xaml` example string.
