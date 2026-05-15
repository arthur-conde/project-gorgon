# Silmarillion detail-view "Copy as image" — Design

**Tracked in:** _no issue yet_
**Date:** 2026-05-15
**Status:** Approved, building

## Problem

Silmarillion's entity detail popups (Ability, Quest, Npc, Effect, Area, Lorebook,
PlayerTitle, StorageVault, Recipe, plus the shared Item detail) are rich, self-contained
cards users want to share into Discord / the wiki. Today there is no way to get one out
of the app except an OS screenshot, which captures window chrome and requires manual
cropping. We want a one-click "copy this card as a PNG to the clipboard," sized to
exactly the card content.

## Scope

In scope:

- A reusable visual→clipboard image exporter in `Mithril.Shared.Wpf`.
- A reusable `DetailExportHost` wrapper that overlays the camera button, wrapped around
  the content of all 10 detail **views** (so the affordance shows in both the inline
  master-detail pane and the popup windows):
  - 9 Silmarillion-local: `AbilityDetailView`, `QuestDetailView`, `NpcDetailView`,
    `EffectDetailView`, `AreaDetailView`, `LorebookDetailView`,
    `PlayerTitleDetailView`, `StorageVaultDetailView`, `RecipeDetailView`.
  - 1 shared: `Mithril.Shared.Wpf/ItemDetailView`.

Out of scope (explicitly):

- Save-to-file / Save-As dialog.
- Fixed social-card layouts (that is Pippin/Legolas's separate "share card" feature).
- Refactoring the 10 windows onto a shared chrome `ControlTemplate` / base class.
- Any new settings.

## Decisions (from brainstorming)

| Question | Decision |
|---|---|
| What does the button do? | Copy PNG to the clipboard. No dialog. |
| What region is captured? | The inner detail `UserControl` body only — **not** the window chrome / title bar. Each detail view already renders its own icon + name header and internal-name footer. |
| Which views? | All 10 (9 Silmarillion-local + shared `ItemDetailView`). |

## Architecture

### 1. `VisualImageExporter` (new — `src/Mithril.Shared.Wpf/VisualImageExporter.cs`)

Static helper. Public surface:

```csharp
public static class VisualImageExporter
{
    /// Renders `target` to a PNG-quality bitmap and places it on the clipboard.
    /// Returns true on success; false if rendering or the clipboard failed.
    public static bool CopyToClipboard(FrameworkElement target);

    /// The render half, factored out for clarity/reuse. Backfills `background`
    /// behind the visual, inset by `padding` px on every side, at `scale`×.
    public static BitmapSource RenderToBitmap(
        FrameworkElement target, double scale, Color background, double padding = 0);
}
```

Behaviour of `RenderToBitmap`:

- Read `target.ActualWidth` / `ActualHeight`. If either is non-positive, fall back to a
  `Measure(infinite)`/`Arrange` pass so a not-yet-arranged element still renders (defensive;
  the live case is always already arranged).
- Capture the element with `RenderTargetBitmap.Render(target)` directly (1:1 in its own
  coordinate space, whitespace included, **no stretch**). A `VisualBrush` is deliberately
  *not* used: its `Stretch=Fill` maps the visual's content bounding box onto the
  destination, distorting sparse cards (little content in a tall pane). Direct render is
  the standard "export this element" path (cf. `PippinShareCardRenderer`).
- If `padding > 0`, compose that bitmap onto a `background`-filled canvas inset by
  `padding`, via a `DrawingVisual` (`DrawRectangle` backfill + `DrawImage` at the same
  `96*scale` DPI so pixels stay 1:1 — no second resample). Final `RenderTargetBitmap` at
  `ceil(canvas*scale)`, DPI `96*scale`, `Pbgra32`, `Freeze()`, return. `padding == 0`
  returns the element bitmap directly.

`CopyToClipboard`:

- `scale = 2.0`, `background = #FF1A1A1A` (the detail-card surface colour, matching the
  window Border so the export looks like the on-screen card), `padding = 16` px of
  backfill-coloured breathing room on every side so the card isn't flush to the edges.
- `Clipboard.SetDataObject(new DataObject(DataFormats.Bitmap, bmp), copy: true)`.
  `copy: true` persists the bitmap past Mithril's process lifetime — established
  Pippin/Legolas pattern; lets the user copy, close Mithril, and still paste in Discord.
- Whole thing in try/catch (clipboard access transiently throws). Returns the bool.

No off-screen STA worker thread (the Pippin renderer needs that only because it rebuilds
a control tree from scratch; here the visual is already live, laid out, and on the UI
thread).

### Design revision — host-wrapper, not title-bar (2026-05-15)

The first implementation (PR #338) put the camera button in each `*DetailWindow`
**title bar**. That was wrong: the Silmarillion tabs render the detail view **inline as
a master-detail pane** (`<ContentControl Content="{Binding DetailViewModel}">` in each
`*TabView`), not via the popup window. The `*DetailWindow` popups only open from
cross-link chips / deep links. So in the everyday flow the title bar — and the button —
never appears. The affordance has to live on the **detail view itself**, which is shared
by both the inline pane and the popups.

### 2. `DetailExportHost` (new — `src/Mithril.Shared.Wpf/DetailExportHost.cs`)

A `sealed class DetailExportHost : ContentControl` (templated control; mirrors the
existing `ViewModeToggle` pattern — `DefaultStyleKeyProperty.OverrideMetadata`, default
style in `Mithril.Shared.Wpf/Resources.xaml`). Its `ControlTemplate` is a `Grid` with:

- `<ContentPresenter x:Name="PART_ExportContent"/>` — the wrapped detail card.
- `<Button x:Name="PART_ExportButton">` — a top-right overlaid camera button
  (`PackIconLucide Kind="Camera"`, semi-opaque, brightens on hover, `Panel.ZIndex=1`).

`OnApplyTemplate` wires the button's `Click` to snapshot **`PART_ExportContent` only**.
Because the button is a *sibling* of the content presenter in the template (not a child),
it is intrinsically excluded from the capture — no hide/restore dance, no title-bar
chrome, works identically in the inline pane and the popup.

```csharp
private void OnExportClick(object sender, RoutedEventArgs e)
{
    if (GetTemplateChild("PART_ExportContent") is not FrameworkElement content
        || _exportButton is null) return;
    var ok = VisualImageExporter.CopyToClipboard(content);
    DetailExportFeedback.Run(ok, _exportButton);
}
```

`DetailExportFeedback` (new, `Mithril.Shared.Wpf`) swaps the button's
`PackIconLucide.Kind` to `Check` (success) or `TriangleAlert` (failure) for ~1.2 s via a
`DispatcherTimer`, then restores `Camera`. No shared toast infra exists, so the button
itself is the affordance.

### 3. Wiring (XAML only — no code-behind)

Each of the 10 `*DetailView.xaml` wraps its existing single root element in the host,
between `</UserControl.Resources>` and `</UserControl>`:

```xml
</UserControl.Resources>

<c:DetailExportHost>
    <Border Padding="14,12"> … existing card … </Border>
</c:DetailExportHost>
</UserControl>
```

(`c:` is each view's existing `Mithril.Shared.Wpf` xmlns alias; `PlayerTitleDetailView`
uses `wpf:`.) The 10 `*DetailWindow` files are **unchanged** (reverted to `main`) — the
wrapped view carries the affordance into the popup automatically. `DataContext` and
`RelativeSource AncestorType={x:Type local:XDetailView}` bindings still resolve: the
`UserControl` remains a visual ancestor of the wrapped content through the host template.

## Data flow

```
user clicks the overlaid camera button (sibling of content, not captured)
  → DetailExportHost.OnExportClick
  → VisualImageExporter.CopyToClipboard(PART_ExportContent)
      → RenderToBitmap(content, 2.0, #FF1A1A1A)
      → Clipboard.SetDataObject(Bitmap, copy:true)
  → DetailExportFeedback.Run(success, PART_ExportButton)  // 1.2s icon swap
```

## Error handling

- Clipboard / render exception → `CopyToClipboard` returns false → button shows the
  alert icon for 1.2s. App never crashes; user can retry.
- Zero-size target → defensive measure/arrange fallback; if still zero, returns false.

## Testing

This codebase deliberately does **not** unit-test `RenderTargetBitmap` paths — see
`PippinShareViewModelTests`: "the actual bitmap render needs a WPF Dispatcher and is
verified manually per the plan." `VisualImageExporter` is almost entirely render +
clipboard with no meaningful pure-data surface, so we follow that established convention
rather than introduce a fragile STA bitmap test.

Verification is a manual shell smoke test, recorded in the PR: launch Mithril, open one
detail card of each of the 10 types, click the camera button, confirm (a) the button
shows the success check, (b) pasting into an external app (e.g. Discord/Paint) yields a
correctly-cropped, non-transparent image of the card body with no window chrome.

The `scale → output dimension` arithmetic and the zero-size guard are simple enough that
the manual paste check exercises them end-to-end; no separate test project is added.

## Risks / notes

- DPI: rendering at 2× fixed scale (not the live monitor DPI) keeps the test deterministic
  and is sharp enough for Discord/wiki use. Revisit only if users report blur.
- `DataFormats.Bitmap` (vs a PNG stream) matches the existing in-repo share code and
  pastes correctly into Discord/Slack/browsers per the Pippin/Legolas precedent.
