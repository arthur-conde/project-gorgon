# WPF gotchas (Mithril)

A catalogue of WPF/XAML traps that have bitten Mithril more than once or that
build green + tests green and only surface at runtime. **Read this before
editing any `*.xaml` or writing a new view.**

Each section names the symptom, why WPF behaves that way, and the fix used in
this codebase. PR refs are the worked examples — open them if the abstract
description isn't clicking.

---

## Click capture & hit-testing

### Opacity = 0 disables hit-testing

WPF elements with `Opacity == 0` stop receiving mouse events even when
`IsHitTestVisible="True"`. There's no "logically present but visually invisible
and still clickable" mode.

**Fix:** for fade-out / auto-hide animations on a surface that still needs to
receive clicks, clamp the minimum opacity to a small non-zero value — `0.01`
(1%) is visually indistinguishable from 0 but keeps the surface live. Use
`Visibility="Collapsed"` if you genuinely want it gone.

Bit: Legolas overlay auto-hide (couldn't click to bring back a faded overlay).

### Layered window: a `Background="Transparent"` region with no painted backdrop lets clicks fall through

In a WPF `AllowsTransparency="True"` (layered) window, the OS only routes
clicks to pixels with **non-zero composited alpha**. A region painted
`Background="Transparent"` (ARGB `#00…`) with nothing else drawn behind it
has alpha 0 in the final composition, so the OS routes the click to whatever
is underneath — your `MouseLeftButtonDown` never fires.

This is distinct from the `Opacity=0` rule above: that's an element's opacity;
this is the OS-level alpha of a layered-window pixel.

**Fix:** paint a `~1%`-alpha backdrop on any click-capture surface. `#03000000`
is visually imperceptible over a game map but routes clicks. Precedent:
`MapOverlayView` paints a full-window `<Rectangle Fill="#80101010"
IsHitTestVisible="False"/>`. Any new transparent click-capture overlay must do
the same.

Bit: PR #449 (calibration overlay) — clicks fell through to the game, no
marker placed.

---

## Layout positioning

### Grid centers a fixed-size child; pinned markers need a zero-size Canvas

A `Grid` containing a fixed-size dot **and** a wider label auto-sizes the
cell to the label, and a fixed-size child with no H/V alignment is **centred
in the cell**. So a "dot + label" Grid placed at `Canvas.Left/Top = (X,Y)`
with `Margin="-7,-7"` (intended to center a top-left-anchored dot) renders the
dot ~half-the-label-width away from (X,Y). Symptom: "pin drops far from where
I clicked" + "label sits on top of the point".

**Fix:** for a marker whose hotspot must be exactly the placement pixel, make
the `ItemTemplate` root a **zero-size `<Canvas>`**: the container's
`Canvas.Left/Top` pins its (0,0) to the pixel, and every part (crosshair arms,
ring, label) is positioned with **explicit `Canvas.Left/Top`** (including
negative values), so geometry is independent of label width. Leave a centre
gap + thin ring so the calibrated pixel stays visible. Zero
`Padding/BorderThickness/Margin` on the host `ItemsControl` so no theme
padding shifts the panel Canvas origin.

Bit: PR #449.

### Horizontal `StackPanel` children default to `VerticalAlignment=Stretch`; small glyphs read low

A stretched `Button`/`TextBlock` in a horizontal StackPanel draws its text
from the **top of the line box**, so a sibling separator with
`VerticalAlignment=Center` lands near the text **baseline** and reads as a
period rather than a centered middot. The glyph isn't the problem — alignment
is. (Some fonts also place `U+00B7` low intra-em; the Mithril mono font does.)

**Fix:** set `VerticalAlignment="Center"` on *all* row items (the text-bearing
elements too, not just the separator). Prefer a shape (`Ellipse`) over a font
glyph for separators — geometry is font-independent.

Bit: PR #339 (Lorebook footer `·` looked like `.`).

---

## Bindings

### `<Run>.Text` is TwoWay by default — crashes against read-only sources

`<Run Text="{Binding X.Count}">` throws at first render:

> `System.InvalidOperationException: A TwoWay or OneWayToSource binding cannot work on the read-only property 'Count' of type 'List<…>'.`

`Run.Text` is unusual: `BindsTwoWayByDefault = true`. Most other `Text`-named
properties (`TextBlock.Text`, `Button.Content`) default to OneWay, so this
only bites inside `<Run>` (and `<Hyperlink>`).

**Fix:** add `Mode=OneWay` explicitly on every `<Run Text="{Binding …}">`
unless you genuinely want write-back (rare).

```xml
<Run Text="{Binding ConsumedByRecipes.Count, StringFormat=' ({0})', Mode=OneWay}" />
```

Build won't catch this. Tests won't catch this (no XAML instantiation). Only
running the shell will.

### `ComboBox` + `DisplayMemberPath` + `SelectedValue`: selection box may render `ToString()`

When a `ComboBox` is configured with both `DisplayMemberPath` and
`SelectedValue/SelectedValuePath`, the dropdown items render correctly but
the **selection box** (the always-visible header) may fall back to
`Item.ToString()` — so an `AlarmChannel { Id = "default", Name = "Default" }`
shows as `"Samwise.Alarms.AlarmChannel"`. Timing-dependent on when
`SelectedValue` resolves vs. when `ItemsSource` is bound.

**Fix:** use an explicit `ItemTemplate` instead of `DisplayMemberPath`. The
template applies to both dropdown items and the selection box.

```xml
<ComboBox ItemsSource="..."
          SelectedValuePath="Id"
          SelectedValue="{Binding Foo.Id}">
    <ComboBox.ItemTemplate>
        <DataTemplate>
            <TextBlock Text="{Binding Name}"/>
        </DataTemplate>
    </ComboBox.ItemTemplate>
</ComboBox>
```

Bit: PR #215 (Samwise alarm channels) — caught visually, not by any test.
Worth eyeballing every new ComboBox + SelectedValue combo.

### `List<T>` won't refresh a bound `ItemsControl`; use `ObservableCollection<T>`

`List<T>` doesn't implement `INotifyCollectionChanged`. A bound `ItemsControl`
re-evaluates the binding when the *property* fires `PropertyChanged`, but
in-place `Add`/`Remove` doesn't change the property reference, so WPF sees
the same list and skips regeneration. Per-item INPC bubbling makes it look
reactive — but only item-property edits work; Add/Remove are silent.

**Fix:** use `ObservableCollection<T>` for any bound property the UI will
mutate, even a "stable settings list". Also subscribe to `CollectionChanged`
from the parent settings object so per-item `PropertyChanged` handlers get
wired on added items (otherwise edits to newly-added items are silently lost
on restart):

```csharp
private ObservableCollection<T> _items = new();
public ObservableCollection<T> Items { get => _items; set { /* … */ } }

// In ctor / PostLoadInit:
_items.CollectionChanged += OnItemsCollectionChanged;

private void OnItemsCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e) {
    if (e.NewItems is not null)
        foreach (T item in e.NewItems) item.PropertyChanged += OnItemChanged;
    if (e.OldItems is not null)
        foreach (T item in e.OldItems) item.PropertyChanged -= OnItemChanged;
}
```

If the property is persisted, register `ObservableCollection<T>` with the
source-generated JSON context (STJ source-gen handles it natively).

Bit: PR #215 — two compounding bugs (invisible added channels + lost edits
on restart).

### Cross-thread mutation of a bound `ObservableCollection` corrupts the bound view's filter

WPF's `ICollectionView` throws on cross-thread `SourceCollection` mutations
("This type of CollectionView does not support changes to its SourceCollection
from a thread different from the Dispatcher thread"). The throw is **swallowed
by the background-task pump**, leaving the collection half-mutated and the
bound view's filter pipeline corrupted.

**Symptom signature** (diagnostic):
- Items render fine on first launch
- After some delay (first background CDN refresh) or after the user types in a
  query box, items disappear
- Clearing the query doesn't restore them — the filter is "sticky"
- Persists across one app restart, then resets on the second (timing-dependent)

**Where to look first:** `%LocalAppData%\Mithril\Shell\logs\mithril-yyyymmdd_NNN.json`.
Serilog captures the swallowed exception as a Warning with
`"Category":"Reference"` or similar. **Always check the shell log before
deep-diving into a Mithril UI state bug** — `tail -100` + grep "Warning" or
"failed" is enough.

**Multicast subscriber knock-on (PR #191/#192):** when the throw happens
inside a multicast event handler, the throw aborts the multicast invocation
**before later subscribers run**. The bug then surfaces as a *different
feature* going stale: in #191, `DashboardAggregator` (first subscriber) threw,
so `TimerSourceBinder` (second subscriber) never ran, and the Quests tab kept
showing stale "done X ago" rows even though Dashboard was the offender. If a
UI element silently stops updating in response to known-good events, check
whether *any other subscriber* of the same multicast event is throwing.

**Fix:** capture `Application.Current.Dispatcher` in the ctor and dispatch
the rebuild. See `src/Pippin.Module/ViewModels/GourmandViewModel.cs` for the
pattern. Events that are NOT safe by default in this codebase:
`IReferenceDataService.FileUpdated`, `FoodCatalog.CatalogChanged`,
`GourmandStateMachine.StateChanged`, any `IPlayerLogStream` / `IChatLogStream`
consumer output, and most `BackgroundService` outputs. Treat all log-stream
and CDN-refresh derived events as background by default; treat
`IActiveCharacterService.ActiveCharacterChanged` and
`CharacterExportsChanged` the same rather than gambling on the source
thread.

Bit: Pippin (May 2026), then PR #191/#192.

---

## Visibility & null leaks

### `ContentControl.ContentTemplate` instantiates even when `Content` is null

`<ContentControl Content="{Binding DetailVm}">` with an inline
`<ContentControl.ContentTemplate>` applies the template **even when Content is
null**. The instantiated tree binds against a `null` DataContext; Visibility
bindings like `Visibility="{Binding X.Count, Converter=PositiveIntToVis}"`
fail silently and Visibility defaults to `Visible` — so section labels,
chrome `TextBlock`s, etc. leak through the master-detail empty state.

**Fix:** use a `DataType`-keyed `DataTemplate` in `ContentControl.Resources`
instead of inline `ContentTemplate`. WPF only applies a `DataType` template
when `Content`'s runtime type matches — null Content gets no template at all,
so nothing renders.

```xml
<ContentControl Content="{Binding DetailVm}">
    <ContentControl.Resources>
        <DataTemplate DataType="{x:Type vm:DetailViewModel}">
            <views:DetailView/>
        </DataTemplate>
    </ContentControl.Resources>
</ContentControl>
```

Alternative: bind `Visibility` on the `ContentControl` (or its parent
`ScrollViewer`) through a null-aware converter on the VM property.

Bit: Silmarillion tab detail pane (PR for #207) — chrome of `ItemDetailView`
leaked through the "Browse items" empty state.

### `NullOrEmptyToVisibilityConverter` is **string-only** — the element silently disappears for any object binding

`Mithril.Shared.Wpf.NullOrEmptyToVisibilityConverter` resolves to
`string.IsNullOrEmpty(value as string) ? Collapsed : Visible`. For any
non-string value the `as string` cast yields `null`, so the converter returns
`Collapsed` **regardless of whether the value is null or not** — the element
silently disappears.

**Use the right converter for the binding type:**

| Binding type | Converter |
|---|---|
| `string` / `string?` | `NullOrEmptyToVis` |
| Object refs (`EntityChipVm?`, POCOs, records) | `NullToVis` (`value is null ? Collapsed : Visible`) |
| `int?` / count | `PositiveIntToVis` |

Audit grep for any tab:

```
Chip[A-Za-z]*, Converter=\{StaticResource NullOrEmptyToVis\}
```

— cross-check each hit against the VM's declared type. Hit on `EntityChipVm`
or any record/POCO → switch to `NullToVis`. Hit on `string?` → leave alone.
**Naming the property `<X>Chip` doesn't make the converter recognise it** —
e.g. `RepeatabilityChip`/`SkillRequirementChip` are correctly string-typed
(they bind `TextBlock.Text`) and keep `NullOrEmptyToVis`.

Related: ContentControl.ContentTemplate null leak (above) — adjacent quirk
where Visibility bindings default to Visible against a null DataContext.

Bit twice: PR #298 (Effects tab, 3 sites) and PR #302 (`AbilityDetailView`
×5, `QuestDetailView` ×2).

---

## Containers & styles

### `ItemContainerStyle` only reliably applies to **generated** containers

`TabControl.ItemContainerStyle` is only reliably applied to TabItems WPF
**generates** from `ItemsSource`. TabItems added directly to `TabControl.Items`
(whether via XAML `<TabControl><TabItem/></TabControl>` or code-behind
`Tabs.Items.Add(new TabItem(...))`) bypass `PrepareContainerForItemOverride`
and ignore `ItemContainerStyle`. Implicit `Style TargetType="TabItem"` lookup
behaves inconsistently for the same reason — works for some XAML-direct cases,
fails for others depending on margin / load order.

**Fix:** for any new tabbed module, bind `TabControl.ItemsSource` to a
collection of `Mithril.Shared.Wpf.ModuleTab` and use
`ItemContainerStyle="{StaticResource MithrilTabItemStyle}"` +
`ItemTemplate="{StaticResource ModuleTabHeaderTemplate}"`. Established
pattern: Samwise / Smaug / Palantir / Arwen / Gandalf / Silmarillion / Bilbo.
When two tabs share an underlying VM but render different views, wrap each in
a marker VM type (see Arwen's `ObservationsEditorViewModel` or Bilbo's
`InventoryTabViewModel` / `CraftableRecipesTabViewModel`) so `DataTemplate`-
by-type can distinguish them.

Bit: #233 — multi-round debug spiral until the wildcard-on-direct-items
behaviour was identified.

### A `Window`'s own `Style="{StaticResource X}"` can't resolve `X` from its own `Window.Resources`

The `Style` attribute on `<Window>` parses **before** `<Window.Resources>`,
so a `StaticResource` lookup against the window's own merged resource
dictionary fails.

**Fix:** keep behavioural Window attrs inline (`WindowStyle=None`,
`AllowsTransparency`, `SizeToContent`) and only share **child-element** styles
via `StaticResource` — those resolve fine because child elements parse after
`Window.Resources`. Alternative: `DynamicResource` for a true window-level
style (deferred lookup), but child-element extraction is the lower-risk path.

Shared chrome keys: `MithrilChromeWindow{Frame,TitleBar,CloseButton,TitleText}Style`
in `Mithril.Shared.Wpf/Resources.xaml`. Conformance call:
`docs/silmarillion-window-chrome.md` / #446.

### `x:Class` XAML with no hand-authored `*.xaml.cs` → silent blank control

A WPF `UserControl`/`Window` whose `x:Class` XAML ships **without a
hand-authored `*.xaml.cs`** (the constructor that calls `InitializeComponent()`)
is a silent runtime failure. The XAML compiler still emits the partial class
+ `InitializeComponent()` in `obj/**/*.g.cs`, so **the build succeeds and
every unit test stays green** — but the implicit default constructor never
calls `InitializeComponent()`, so the control instantiates **completely
empty**: blank pane, header/host present, no exception, nothing in
`crash.log`. `-Clean` doesn't help — missing source, not stale binaries.

**Diagnosis cue:** "tab/pane totally blank, no error, tests pass, rebuild
doesn't help" → check the view has its `.xaml.cs`, don't keep probing the
VM. Test the layer the symptom is in, not just the layer below.

**Guard:** `tests/Silmarillion.Tests/Views/TabViewCodeBehindGuardTests.cs`
asserts every Silmarillion `x:Class` XAML has a `.xaml.cs` calling
`InitializeComponent()`. Cheapest layer that catches this (xunit never
instantiates XAML). Extend the guard to new tab regions as you add them.

Bit: `LorebooksTabView` / `StorageVaultsTabView` shipped to `main` rendering
blank tabs; VM/data was correct the whole time. Fixed in PR #337 (closes
#335). Was also a #331 splice site — see
[[post_merge_reverify_nonnegotiable]].

---

## Virtualization & image export

### Use the **sbaeumlisberger `VirtualizingWrapPanel` NuGet package**, not the in-repo `MithrilVirtualizingWrapPanel`

For new WPF surfaces that need a virtualizing wrap panel, use the
`VirtualizingWrapPanel` package (sbaeumlisberger, MIT, supports
`net10.0-windows`). It handles variable-width items, has solid recycling,
and is actively maintained.

The in-repo `Mithril.Shared.Wpf.MithrilVirtualizingWrapPanel` is **sunset**.
Fixed-cell only (`ItemWidth × ItemHeight` uniform), would need substantial
work for variable-width items (chip flows, ingredient lists).

**How to apply:**
- Reach for `WpfToolkit.Controls.VirtualizingWrapPanel` in new XAML.
- When touching a file that uses `MithrilVirtualizingWrapPanel`, migrate it
  as part of the change rather than perpetuating the old impl. Don't migrate
  proactively — only when in the file for other reasons.
- Once all callers are migrated, delete
  `src/Mithril.Shared.Wpf/MithrilVirtualizingWrapPanel.cs`.
- Existing usage to migrate (as of 2026-05-13):
  `src/Arwen.Module/Views/ObservationsEditorTab.xaml`.

### Exporting a live WPF element to a bitmap — three rakes

From PR #338 (Silmarillion detail "Copy as image"); each cost a rework cycle.

1. **Affordances on Silmarillion detail views must live on the *view*, not
   the *DetailWindow* chrome.** The tabs render the detail inline as a
   master-detail pane (`<ContentControl Content="{Binding DetailViewModel}">`
   in each `*TabView`, via `DataTemplate` → `*DetailView`). The
   `*DetailWindow` popups only open from cross-link chips / deep links. A
   button added to the window title bar never appears in the everyday flow.
   Solution pattern: a shared wrapper control
   (`Mithril.Shared.Wpf.DetailExportHost : ContentControl`) that each
   `*DetailView` wraps its root in — appears in both the inline pane and the
   popups. Templated-control precedent: `ViewModeToggle`
   (`DefaultStyleKeyProperty.OverrideMetadata` + Style/ControlTemplate in
   `Mithril.Shared.Wpf/Resources.xaml`).

2. **Never snapshot via `VisualBrush` with default `Stretch=Fill`.** It maps
   the visual's *content bounding box* onto the destination rect — sparse
   cards (little content in a tall pane) get stretched/distorted. Use
   `RenderTargetBitmap.Render(target)` directly (1:1, same path as
   `PippinShareCardRenderer`).

3. **For a *tight* image when the live element is arranged larger (stretched
   by a pane), crop to `VisualTreeHelper.GetDescendantBounds(target)`** —
   the union of actually-drawn geometry — rather than re-arranging the live
   element (disturbs the UI) or shrink-wrapping the in-app host (width
   jiggles per selection — user disliked that). Cropping also drops the
   card's own empty `Border` padding so a single exporter padding doesn't
   read as "doubled".

`RenderTargetBitmap` paths are NOT unit-tested in this repo (need a WPF
Dispatcher) — verified manually per `PippinShareViewModelTests`.

Design notebook:
`docs/superpowers/specs/2026-05-15-silmarillion-detail-export-image-design.md`.
