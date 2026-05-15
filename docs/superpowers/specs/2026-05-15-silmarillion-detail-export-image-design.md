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
- A camera button added to the title bar of all 10 detail windows:
  - 9 Silmarillion-local: `AbilityDetailWindow`, `QuestDetailWindow`, `NpcDetailWindow`,
    `EffectDetailWindow`, `AreaDetailWindow`, `LorebookDetailWindow`,
    `PlayerTitleDetailWindow`, `StorageVaultDetailWindow`, `RecipeDetailWindow`.
  - 1 shared: `Mithril.Shared.Wpf/ItemDetailWindow`.

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
    /// behind the visual and renders at `scale`× for crisp text.
    public static BitmapSource RenderToBitmap(
        FrameworkElement target, double scale, Color background);
}
```

Behaviour of `RenderToBitmap`:

- Read `target.ActualWidth` / `ActualHeight`. If either is non-positive, fall back to a
  `Measure(infinite)`/`Arrange` pass so a not-yet-arranged element still renders (defensive;
  the live case is always already arranged).
- Build a `DrawingVisual`; in its `DrawingContext` draw a filled `background` rectangle
  then the `target` via a `VisualBrush`, scaled by `scale`. This yields crisp text at
  higher-than-screen resolution and guarantees a non-transparent surface so the pasted
  image isn't see-through.
- `RenderTargetBitmap` at `ceil(width*scale) × ceil(height*scale)`, DPI `96*scale`,
  `Pbgra32`. `Render(drawingVisual)`, `Freeze()`, return.

`CopyToClipboard`:

- `scale = 2.0`, `background = #FF1A1A1A` (the detail-card surface colour, matching the
  window Border so the export looks like the on-screen card).
- `Clipboard.SetDataObject(new DataObject(DataFormats.Bitmap, bmp), copy: true)`.
  `copy: true` persists the bitmap past Mithril's process lifetime — established
  Pippin/Legolas pattern; lets the user copy, close Mithril, and still paste in Discord.
- Whole thing in try/catch (clipboard access transiently throws). Returns the bool.

No off-screen STA worker thread (the Pippin renderer needs that only because it rebuilds
a control tree from scratch; here the visual is already live, laid out, and on the UI
thread).

### 2. Title-bar button

Each `*DetailWindow.xaml` already has a title-bar `DockPanel` with a `DockPanel.Dock="Right"`
Close button. Add, immediately before the Close button:

```xml
<Button DockPanel.Dock="Right" Width="24" Height="24" Padding="0"
        Background="Transparent" BorderThickness="0" Cursor="Hand"
        ToolTip="Copy as image" Click="ExportImageButton_Click"
        x:Name="ExportImageButton">
    <icon:PackIconLucide Kind="Camera" Width="14" Height="14" Foreground="#88FFFFFF"/>
</Button>
```

The inner detail view element gets `x:Name="DetailBody"`.

### 3. Wiring (code-behind)

Each window's `.xaml.cs` gets a one-line handler delegating to a shared static so the
copy/feedback logic is written once:

```csharp
private void ExportImageButton_Click(object sender, RoutedEventArgs e)
    => DetailExportFeedback.Run(VisualImageExporter.CopyToClipboard(DetailBody),
                                ExportImageButton);
```

`DetailExportFeedback` (new, `Mithril.Shared.Wpf`) is a tiny static that, given the
success bool and the button, swaps the button's `PackIconLucide.Kind` to `Check`
(success) or `AlertTriangle` (failure) for ~1.2s via a `DispatcherTimer`, then restores
`Camera`. No shared toast infrastructure exists, so the button itself is the affordance.

## Data flow

```
user clicks Camera button (title bar, not captured)
  → ExportImageButton_Click
  → VisualImageExporter.CopyToClipboard(DetailBody)
      → RenderToBitmap(DetailBody, 2.0, #FF1A1A1A)
      → Clipboard.SetDataObject(Bitmap, copy:true)
  → DetailExportFeedback.Run(success, button)  // 1.2s icon swap
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
