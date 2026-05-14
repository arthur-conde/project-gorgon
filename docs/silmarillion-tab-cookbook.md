# Silmarillion · Tab Cookbook

> **Companion doc:** [silmarillion-roadmap.md](silmarillion-roadmap.md) — *why* each tab exists. This doc covers *how* to build one.

How to add a new master-detail tab to Silmarillion, after the Items + Recipes v1 shipped (PR #236), the navigator was refactored to a kind-target registry (#239), and the tab-strip moved to MVVM `ItemsSource` binding via [`ModuleTab`](../src/Mithril.Shared.Wpf/ModuleTab.cs) (#272 / #233). Every Bucket B tab follows the same scaffold; this doc is the scaffold. Per-tab handoffs in [`docs/agent-plans/`](agent-plans/) own the entity-specific decisions.

## What's in scope vs. what your handoff still owns

**The cookbook covers** (don't repeat in handoffs):
- File layout, naming, folder structure
- DI registration shape and the Func<> cycle break
- Master-detail VM skeleton (lazy detail VM, FileUpdated subscription, selection preservation)
- `IReferenceKindTarget` adapter shape
- TabControl wiring + the `SilmarillionViewModel` constructor parameter
- Cross-link chip conventions and degradation pattern
- The standard test trio (tab VM, kind target, navigator integration)
- Verification ladder

**Your handoff still owns**:
- Which `IReferenceDataService` source dictionary to read
- The master-list row shape and filter facets
- Detail-pane sections — which fields surface, in what order
- Reverse-lookup plumbing on `IReferenceDataService` (if the detail pane needs new cross-links)
- Cross-link chip kinds (which `EntityKind` values to anchor to)
- Any bundled JSON / refresh ordering caveats for the entity's source file

## Scaffolding checklist

For tab `X` (e.g. `Npcs`, `Quests`, `Areas`):

1. **New files**
   - `src/Silmarillion.Module/Views/<X>TabView.xaml` (+ `.xaml.cs`)
   - `src/Silmarillion.Module/ViewModels/<X>TabViewModel.cs`
   - `src/Silmarillion.Module/ViewModels/<X>DetailViewModel.cs` *(or reuse an existing shared detail VM if the entity already has one — e.g. items use `Mithril.Shared.Wpf.ItemDetailViewModel`)*
   - `src/Silmarillion.Module/Navigation/<X>KindTarget.cs`
2. **`SilmarillionModule.Register`** ([src/Silmarillion.Module/SilmarillionModule.cs](../src/Silmarillion.Module/SilmarillionModule.cs)) — three `AddSingleton` lines, **in this order** so DI can resolve:
   ```csharp
   services.AddSingleton<XTabViewModel>();
   // ... existing AddSingleton<SilmarillionViewModel>() stays here ...
   services.AddSingleton<IReferenceKindTarget>(sp => new XKindTarget(
       sp.GetRequiredService<XTabViewModel>(),
       sp.GetService<IDiagnosticsSink>()));
   ```
3. **`SilmarillionViewModel`** ([src/Silmarillion.Module/ViewModels/SilmarillionViewModel.cs](../src/Silmarillion.Module/ViewModels/SilmarillionViewModel.cs)) — add a constructor parameter `XTabViewModel x`, a public `X { get; }` property, and **append a `new ModuleTab("<Label>", x)` entry to the `Tabs` collection** built in the constructor. Tab index is the array position. **No changes to `OnNavigated` or `OpenInWindow`** — the registry-driven dispatch already handles new kinds automatically. Confirm `EntityKind.<X>` exists in [src/Mithril.Shared/Reference/EntityRef.cs](../src/Mithril.Shared/Reference/EntityRef.cs); it should — all 11 entity kinds were enumerated in v1, alongside three synthetic deep-link variants (`RecipeIngredientKeyword` from #259, `ItemKeyword` from #270, `RecipeIngredientItem` from #273) — see *Synthetic kinds*, below.
4. **`SilmarillionView.xaml`** ([src/Silmarillion.Module/Views/SilmarillionView.xaml](../src/Silmarillion.Module/Views/SilmarillionView.xaml)) — **the `TabControl` itself doesn't change** (it binds `ItemsSource="{Binding Tabs}"` since the #272 / #233 refactor). Add a `<DataTemplate DataType="{x:Type vm:XTabViewModel}"><local:XTabView/></DataTemplate>` entry to `UserControl.Resources` so the TabControl's `ContentTemplate` can resolve the new VM type to its view. Header rendering and the gold IsSelected underline come from `ModuleTabHeaderTemplate` + `MithrilTabItemStyle` — already wired.
5. **Wire `IReferenceDataService.FileUpdated`** in the tab VM constructor — subscribe to the file name your data source reads (e.g. `"npcs"`, `"quests"`). Rebuild the master list on the UI thread; preserve selection by `InternalName`. **Don't skip this** — without it, a background CDN refresh leaves WPF's ListBox bound to a stale collection and selections set via the navigator silently fall out of the list.
6. **Expose `public static IReadOnlyList<ColumnSchema> SchemaSnapshot`** on the tab VM, reflected from your row type via `ColumnBindingHelper.BuildFromProperties(typeof(<Row>)).ToSchema()` — see [ItemsTabViewModel.cs:34](../src/Silmarillion.Module/ViewModels/ItemsTabViewModel.cs#L34) for the canonical shape. Bind it from XAML as `<wpf:MithrilQueryBox Schema="{x:Static vm:XTabViewModel.SchemaSnapshot}" .../>` (#264 / #262). Without this binding the completion popup silently never opens and column-name highlighting falls back to unknown-column red. The reflected surface must match what the row type exposes, since the same reflection drives the `QueryFilter` parser at attach time.

That's it. No edits to `SilmarillionReferenceNavigator`, no `V1TabbedKinds` (it was retired in #239), no `OnNavigated` switch.

## Pattern walkthrough

The Items tab is the cleanest reference. Read these in order:

- [ItemsTabViewModel.cs](../src/Silmarillion.Module/ViewModels/ItemsTabViewModel.cs) — `_allItems` list, lazy `DetailViewModel` build on `OnSelectedItemChanged`, `FileUpdated` re-bind preserving selection (lines 30-37 ctor, ~80-110 selection handler, ~140-170 cross-link builders).
- [ItemsTabView.xaml](../src/Silmarillion.Module/Views/ItemsTabView.xaml) — `MithrilQueryBox` top, virtualized `ListBox` left (~320px), `ScrollViewer` right with detail pane.
- [ItemsKindTarget.cs](../src/Silmarillion.Module/Navigation/ItemsKindTarget.cs) — the four-property adapter. Note the critical comment at lines 33-39: resolve `TrySelectByInternalName` against the tab VM's bound collection, **not** against `IReferenceDataService` directly. A background refresh swaps instances, and a refData lookup hands you a record WPF can't match by `Equals`.

The Recipes tab does the same dance with a row-projection wrapper (`RecipeListRow`) because raw `Recipe` records don't carry resolved skill display names. If your entity needs cross-cuts (resolved skill, joined area, computed icon fallback), follow that pattern instead of `Item`'s plain-record model.

## Cross-link chips

Use [`EntityChipVm`](../src/Mithril.Shared.Wpf/EntityChipVm.cs) for entity references. The chip carries `(DisplayName, IconId, EntityRef Reference, bool IsNavigable)`. Set `IsNavigable = _navigator.CanOpen(reference)` — this returns `true` iff a kind target is registered for `reference.Kind`. Chips to kinds without a tab degrade to plain text automatically; they flip to clickable the moment that kind's tab ships. **Don't gate on the kind manually** — let `CanOpen` decide.

Use [`ItemSourceChipVm`](../src/Mithril.Shared.Wpf/EntityChipVm.cs) for source-style rows where the anchor entity may not exist as a clickable target — its `EntityReference` is nullable for true non-entity sources (e.g. "monster drop", "barter table"). Don't introduce a parallel chip type per entity kind; reuse this one.

## Synthetic kinds — deep-linking to a tab with a pre-filled query

Not every `IReferenceKindTarget` selects an entity by name. The `Silmarillion.Module/Navigation/` folder ships three synthetic kind targets, all following the same shape — `Kind` is a synthetic `EntityKind` value, `InternalName` carries a payload (keyword tag, item internal name, etc.), and `TrySelectByInternalName` mutates a tab VM's `QueryText` instead of selecting a row:

- **[`RecipeIngredientKeywordKindTarget`](../src/Silmarillion.Module/Navigation/RecipeIngredientKeywordKindTarget.cs)** (#259) — keyword chip on item-detail's "Used as" → Recipes tab filtered to `IngredientKeywords CONTAINS "<keyword>"`.
- **[`ItemKeywordKindTarget`](../src/Silmarillion.Module/Navigation/ItemKeywordKindTarget.cs)** (#270) — keyword chip on recipe-detail → Items tab filtered to items whose Keywords contain the chip's tag.
- **[`RecipeIngredientItemKindTarget`](../src/Silmarillion.Module/Navigation/RecipeIngredientItemKindTarget.cs)** (#273) — overflow pill on item-detail's "Used in" → Recipes tab filtered to `Ingredients CONTAINS "<itemInternalName>"`. Used when chip cardinality exceeds `SilmarillionSettings.UsedInChipCap`.

All three set `TryOpenInWindow` → `false` (nothing to open in isolation) and `TabIndex` to the destination tab's index.

**Two lessons from this pattern's evolution:**

1. **When a chip's natural target is *"open the right tab filtered to this concept"* rather than *"select this specific row"*, model it as a synthetic `EntityKind` + a query-mutating kind target.** Don't fan out to a list of name-keyed chips when the cardinality could be high — Massive Tourmaline's "any Crystal" expansion produced ~547 chips and was unscannable until #259 collapsed it to a single keyword chip.
2. **When fan-out cardinality is unbounded but the *direct* refs are still useful, cap + overflow-pill.** [`ItemsTabViewModel.BuildConsumedByChips`](../src/Silmarillion.Module/ViewModels/ItemsTabViewModel.cs) returns the first `UsedInChipCap` (default 12) chips plus a `+{N-cap} more →` pill that uses a synthetic kind target to deep-link to the filtered tab. The pill carries the source entity's icon for visual continuity. Settings live in [`SilmarillionSettings`](../src/Silmarillion.Module/SilmarillionSettings.cs); follow that file as the template if your tab needs persistent settings (`SchemaVersion` stamped from day one, `INotifyPropertyChanged`, source-gen STJ context, debounced autosave via `AddMithrilSettings<T>`).

**Tooling for collection-CONTAINS filtering:** wrap the tag/value in a small record implementing `IQueryStringValue` (e.g. [`IngredientKeywordValue.cs`](../src/Silmarillion.Module/ViewModels/IngredientKeywordValue.cs), [`IngredientItemValue.cs`](../src/Silmarillion.Module/ViewModels/IngredientItemValue.cs)), expose the field on your master-list row as `IReadOnlyList<TValue>`, and the `MithrilQueryBox` parser handles `CONTAINS` for free (#261). Make sure the row type's reflected schema (step 6 above) includes the collection field — without it, autocomplete won't suggest it.

## DI cycle break — why the Func<> wrappers exist

[SilmarillionModule.cs:36-39](../src/Silmarillion.Module/SilmarillionModule.cs#L36-L39):

```csharp
services.AddSingleton<IReferenceNavigator>(sp => new SilmarillionReferenceNavigator(
    () => sp.GetServices<IReferenceKindTarget>(),
    () => sp.GetService<IModuleActivator>(),
    sp.GetService<IDiagnosticsSink>()));
```

Kind targets depend on tab VMs (via constructor). Tab VMs depend on `IReferenceNavigator` (for `CanOpen` calls inside chip builders). The navigator depends on kind targets (for `CanOpen` to mean anything). The `Func<>` wrapper defers `GetServices` until the navigator's first `CanOpen` call — by which point the container is fully built. **Keep the Func<> shape when modifying registration.** Eager resolution will cycle.

Tests should use the eager `SilmarillionReferenceNavigator(IEnumerable<IReferenceKindTarget>)` constructor with stubs; production code is the lazy path.

## Test scaffolding

Three test files per tab, mirroring the v1 trio:

1. `tests/Silmarillion.Tests/ViewModels/<X>TabViewModelTests.cs` — list construction, sort order, skill / area resolution, `FileUpdated` re-bind preserves selection, detail-VM build cross-link projection.
2. `tests/Silmarillion.Tests/Navigation/<X>KindTargetTests.cs` — `Kind` / `TabIndex` properties, `TrySelectByInternalName` (hit and miss), `TryOpenInWindow` (with and without current detail).
3. `tests/Silmarillion.Tests/Navigation/SilmarillionReferenceNavigatorTests.cs` and `SilmarillionViewModelTests.cs` — **extend existing tests**, don't create per-tab navigator tests. Verify the duplicate-registration guard still trips with the new kind in the mix.

Use the `NavFactory.WithKinds(...)` helper from [RecipesTabViewModelTests.cs](../tests/Silmarillion.Tests/ViewModels/RecipesTabViewModelTests.cs) to construct stub navigators with a specific kind subset registered — this is how you write `IsNavigable=true|false` assertions deterministically. Extend `StubReferenceData` with new source dictionaries as needed; consumer fakes in other modules use the interface default for any property they don't care about, so adding source data doesn't ripple across the test suite.

## Verification ladder

Every Bucket B PR should pass these in order:

1. `dotnet build Mithril.slnx` — warnings-as-errors clean.
2. `dotnet test tests/Silmarillion.Tests` — the three new test files + existing tests pass.
3. `dotnet test Mithril.slnx` — no regressions, especially in consumers of `IReferenceDataService` (Celebrimbor, Bilbo, Elrond, Arwen).
4. `dotnet run --project src/Mithril.Shell` — manual checks:
   - Open Silmarillion → new tab appears in the strip with the gold IsSelected underline (confirms `MithrilTabItemStyle` is being applied — only happens when the tab comes through `ItemsSource`, not direct `TabControl.Items.Add`).
   - Pick an entity → detail pane populates.
   - Type a column name in the query box → completion popup opens, known names highlight in column-gold (confirms `SchemaSnapshot` is wired per step 6).
   - Cross-link chips render (navigable to tabbed kinds, plain text otherwise).
   - Click a chip that points to your new kind from an *existing* tab → tab switches and entity selects.
   - Open-in-window button on the header works for the new kind.
   - Background refresh check: leave the tab open, force a CDN refresh, confirm the list rebuilds without losing the current selection.

## Three things that go wrong

1. **The tab VM is registered but the kind target isn't.** The tab renders but cross-link chips to its kind stay plain-text, and the navigator's `CanOpen` returns false for the new kind. Symptom: clicking a chip silently does nothing. Cause: missed the second `AddSingleton<IReferenceKindTarget>` line. Fix: register the target.
2. **The tab VM lookup happens against `IReferenceDataService` instead of the bound `All<X>` collection.** Symptom: post-CDN-refresh navigation appears to succeed but the detail pane goes blank. Cause: WPF's ListBox can't match the new refData instance to its `ItemsSource`. Fix: resolve in `TrySelectByInternalName` against the tab VM's bound collection, per the `ItemsKindTarget` comment.
3. **`MithrilQueryBox.Schema` isn't bound.** Symptom: typing in the query box never opens the completion popup, and known column names render as unknown-column red. Cause: missed the `Schema="{x:Static vm:XTabViewModel.SchemaSnapshot}"` binding (or never exposed `SchemaSnapshot`). The query *parser* still works for hand-typed expressions, so this fails silently in tests — only manual smoke catches it. Fix: per step 6 above.

## When the cookbook doesn't apply

- **Single-pane tabs** (no master-detail). Not in scope for any Bucket B entry; lorebooks are still master-detail with a long-form body in the detail pane. Revisit this doc if a future tab needs a different shape.
- **Calculator-shaped surfaces** (e.g. a hypothetical Powers / TSys tab). Per [silmarillion-roadmap.md](silmarillion-roadmap.md) these are out of scope for Silmarillion; their natural home is Celebrimbor.
- **Cross-module dependencies** (e.g. NPCs cross-links from Arwen's favor service). Pure read access to other modules' DI-exposed services is fine; if a new tab needs to *push* data back, that's a design question outside the cookbook — file an issue first.
