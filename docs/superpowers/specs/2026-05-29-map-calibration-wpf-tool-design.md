# Map Calibration WPF Workspace — Design

**Tracked in:** _no issue yet_ (filed alongside this commit; supersedes [#857](https://github.com/moumantai-gg/mithril/issues/857))
**Date:** 2026-05-29
**Status:** Approved, building

## Problem

[PR #854](https://github.com/moumantai-gg/mithril/pull/854) shipped a screenshot-based
CLI calibration tool (`tools/MapCalibrationFromScreenshot/`) that solved AreaSerbule
to 0.30 px residual. In practice it has worked on exactly that one of the ~35 PG
areas — Eltibule failed because NCC template-matching against icon sprites is
drowned out by rocky-border texture noise, and similar failure modes are expected
across most areas.

[#857](https://github.com/moumantai-gg/mithril/issues/857) proposed a click-calibrate
CLI sub-phase (green-pixel detection on PG's "icon selected" highlight) as the cheap
fix. That likely works — empirically the user has confirmed the green highlight
renders on both the open map and minimap — but the friction around the rest of the
CLI loop dominates, not just ref capture:

- `--map-rect` is measured in Gimp per area (~30 s + context switch).
- NCC parameters (`--icon-render-size`, `--detection-threshold`, `--exclude-type`)
  are tuned blind: edit args, re-run, open the `--debug-image` PNG, eyeball,
  retry.
- Each screenshot goes through Alt+PrtSc → Paint → Save As → note path → switch
  to terminal (~60–90 s of clerical work per area).
- The `--projection-overlay` PNG that shows residual-vs-ground-truth is a static
  artifact you reopen after each run.

Over 34 areas, the clerical friction (not the per-area CLI math) is what determines
whether the backlog gets cleared in one sitting or postponed indefinitely. The
green-pixel modality also doesn't reach every conceivable ref — landmarks that
don't have an in-game pin to highlight, painted-only map features, and refs the
user wants to anchor by visually clicking the source map.

A WPF workspace with the CLI's primitives wired up to live overlays — paste a
screenshot, draw the map-rect with the mouse, watch detections + projections
update in real time, click landmarks on the source map directly, commit to the
baseline JSON without leaving the window — collapses every loop above into one
interactive surface.

## Scope

**In scope (Phase 1, the MVP):**

- A new WPF tool project: `tools/MapCalibrationWpf/`.
- A new shared lib `tools/Mithril.MapCalibration.Tools.Common/` carved out of
  the merged CLI, containing the primitives both tools use:
  `BaselineFile`, `IconTemplateExtractor`, `MapTextureExtractor`, `MapRectLocator`,
  `NccTemplateMatch`, `LandmarksReader`, `NpcsReader`, `PlayerLogScanner`,
  `SteamInstall`, `ImageIo`.
- The merged CLI (`tools/MapCalibrationFromScreenshot/`) is refactored to
  `ProjectReference` the new common lib and stripped of the moved files.
  Its CLI surface, recipe, and behaviour are unchanged.
- Phase 1 WPF surface — area picker, screenshot paste (`Ctrl+V`), mouse-drawn
  map-rect, source-map canvas with overlaid markers, landmark/NPC list,
  source-map-click ref-capture modality, live solver readout
  (`Scale`, `RotationDegrees`, `MirrorNorth`, `ResidualPixels`, `ReferenceCount`),
  per-ref residual in the ref-table, projection overlay on the source map,
  "Commit anchor" button that upserts into
  `src/Mithril.MapCalibration/BundledData/map-calibration-baseline.json`.

**Out of scope for Phase 1** (deferred to follow-up phases — see [Phasing](#phasing)):

- NCC live overlay + parameter sliders (Phase 2).
- Screenshot-bbox ref-capture modality + green-pixel auto-detect (Phase 3).
- Native PG-window capture via `User32.PrintWindow` (Phase 4).
- Auto-bootstrap from world-bounds (3a) and slider-tune fitting (3b) — both
  drop in as additional capture modalities on the same workspace once the
  Phase 1 substrate exists.

**Explicitly not built (ever, in this tool):**

- End-user calibration UX. Legolas's per-user click-to-refine path already
  serves end-users; this tool ships dev-only.
- Refactoring `Mithril.MapCalibration` (the lib in `src/`). The solver,
  `AreaCalibration`, and bundled JSON schema stay exactly as-is.
- Productionising into `Mithril.Shell`. No runtime hotkey to "calibrate this
  area from the current screenshot" — that was already out of scope on #852
  and remains so.
- Per-user write target (`%LocalAppData%/Mithril/map-calibration-user.json`).
  This tool writes only the bundled baseline JSON in-repo.

## Decisions (from brainstorming)

| Question | Decision |
|---|---|
| Where does the tool live? | New `tools/MapCalibrationWpf/` project. ProjectReferences `Mithril.MapCalibration` and the new `tools/Mithril.MapCalibration.Tools.Common`. |
| Does it replace the merged CLI? | No. CLI stays for headless / batch / self-test. Both tools share primitives. |
| Audience? | Dev-only. Arthur (and any future contributor populating the bundled baseline). |
| Output target? | `src/Mithril.MapCalibration/BundledData/map-calibration-baseline.json` (the same file the CLI already writes). |
| Relationship to #857 / #858? | **Supersedes #857** — WPF does the click-calibrate job with a better integrated loop, close #857 with a pointer here. **#858 (swap-detection) stays open** — it benefits the headless CLI on the few areas where NCC works at all. |
| Phase 1 ref-capture modalities? | Source-map click only. User picks a landmark from a filtered list, clicks on the source PNG to anchor it; texture pixel = click position directly. Screenshot-bbox modality lands in Phase 3. |
| Does the solver run incrementally? | Yes — every time the ref-set mutates, re-solve via `LandmarkCalibrationSolver.Solve`, push the result + residual to the readout, redraw the projection overlay on the source-map canvas. |
| What happens when an area has no source PNG? | Phase 1 fails the workspace open with a clear message ("no extracted map texture for AreaX — run the CLI's `--phase extract-map` first"). Bundled assets are gated by what the CLI's existing extractor produces; not the WPF tool's job to re-derive. |
| Dirty-state on area switch? | Confirm-discard dialog if there are uncommitted refs. |
| Schema migrations? | None. `AreaCalibration` (the persisted record) is unchanged; the tool only constructs solver-produced instances at `SchemaVersion = 1`. |

## Architecture

### Project structure

```
tools/
├─ Mithril.MapCalibration.Tools.Common/           ← NEW
│    BaselineFile.cs
│    IconTemplateExtractor.cs
│    MapTextureExtractor.cs
│    MapRectLocator.cs
│    NccTemplateMatch.cs
│    LandmarksReader.cs
│    NpcsReader.cs
│    PlayerLogScanner.cs
│    SteamInstall.cs
│    ImageIo.cs
│    Mithril.MapCalibration.Tools.Common.csproj   ← Microsoft.NET.Sdk, netstandard2.0 or net10.0-windows
│
├─ MapCalibrationFromScreenshot/                  ← TRIMMED (10 files moved out)
│    Program.cs
│    CliArgs.cs
│    Pipeline.cs
│    ScreenshotCalibrator.cs
│    SelfTest.cs
│    README.md
│    MapCalibrationFromScreenshot.csproj          ← + ProjectReference to Common
│
└─ MapCalibrationWpf/                             ← NEW
     App.xaml / App.xaml.cs
     MainWindow.xaml / MainWindow.xaml.cs
     ViewModels/
       MainViewModel.cs
       AreaWorkspaceViewModel.cs
       RefViewModel.cs
       LandmarkPickerViewModel.cs
     Views/
       SourceMapCanvas.xaml          ← shows source PNG + overlays markers
       ScreenshotPad.xaml            ← (Phase 1: paste + map-rect draw; Phase 2+: NCC overlay)
       RefTable.xaml                 ← list of refs with per-ref residual
       SolverReadout.xaml            ← scale / rotation / mirror / residual / count
     Services/
       AreaCatalog.cs                ← lists areas with extracted source PNGs
       ScreenshotPasteService.cs     ← clipboard → BitmapSource
       WorkspaceCommitService.cs     ← solver → AreaCalibration → BaselineFile upsert
     MapCalibrationWpf.csproj        ← Microsoft.NET.Sdk, net10.0-windows, UseWPF=true
                                       + ProjectReference to Common + src/Mithril.MapCalibration
```

`Mithril.MapCalibration` (the existing lib in `src/`) is untouched. The WPF tool
and the CLI both `ProjectReference` it for `LandmarkCalibrationSolver`,
`AreaCalibration`, `PixelPoint`, `WorldCoord`, `CalibrationSource`.

### Why a separate common lib (not source-shared)

The CLI's primitives are stable code today; they want to stay sharable without
being re-compiled differently per consumer. A real assembly with one `csproj`
keeps build cost bounded, lets both tools `ProjectReference` it cleanly,
and matches the existing pattern (e.g., `tools/Mithril.LogSanitizer` is its own
csproj). Source-linking via `<Compile Include="..\..\X.cs" />` would couple the
two tool projects to each other's directory layout and is rejected.

### Phase 1 component anatomy

The MVP window is a 3-pane WPF layout:

```
┌─────────────────────────────────────────────────────────────────────┐
│  [Area: AreaEltibule ▼]                            [Commit anchor]  │
├──────────────────┬───────────────────────────┬──────────────────────┤
│ Landmarks/NPCs   │ Source map                │ Refs (5)             │
│ ┌─search ─────┐  │                           │ ┌──────────────────┐ │
│ │ kleav     │  │   [source PNG, fills pane] │ │ Kleave   2.1 px  │ │
│ └─────────────┘  │   ● Kleave  (proj)        │ │ Sus Cow  1.8 px  │ │
│ [Kleave    NPC]  │   ● Sus Cow (proj)        │ │ Med.Pillar 0.9px │ │
│ [Sus Cow   NPC]  │   ✕ Sus Cow (ref click)   │ │ Strange Gtw 1.4  │ │
│ [Med.Pillar LMK] │                           │ │ Teleport Crc 1.1 │ │
│ [Strange Gateway]│                           │ └──────────────────┘ │
│ ...              │                           │ Solver readout       │
│                  │                           │ Scale  0.823         │
│                  │                           │ Rot    0.0°          │
│                  │                           │ Mirror false         │
│                  │                           │ Residual 1.51 px     │
└──────────────────┴───────────────────────────┴──────────────────────┘
```

**Left pane — `LandmarkPickerView`.** A search-as-you-type `TextBox` filters a
`ListBox` over the union of `landmarks.json` + `npcs.json` entries for the
selected area. NPCs are pulled from `NpcsReader` (already in the CLI's primitives
moving to Common); landmarks from `LandmarksReader`. Each row carries the
world-coord (`WorldX`, `WorldZ`), the display name, and an enum tag
(`LandmarkKind` ∈ {Npc, Landmark}). Selection sets the
`AreaWorkspaceViewModel.PendingRef` — a half-built `RefViewModel` waiting for a
click on the source map.

**Center pane — `SourceMapCanvas`.** An `Image` element bound to the area's
source PNG (loaded via `MapTextureExtractor` if needed; cached PNG path on
first run), wrapped in a `Canvas` that hosts overlay markers in a layered
`ItemsControl`. Two overlay layers:

- **Refs layer** — for each committed `Ref`, draw a red ✕ at its
  `texturePixel`. Hover shows landmark name + per-ref residual.
- **Projections layer** — for every landmark/NPC in the area (not just the
  refs), draw a small filled circle at the current calibration's predicted
  pixel position. Colour-encode by residual class: green ≤ 3 px, yellow
  ≤ 12 px (the `CalibrationGoodResidualPx` threshold), red > 12 px.
  Updates reactively when the solver re-runs.

A left-click on empty canvas with a `PendingRef` set materialises a `Ref` at
that pixel (texture pixel = click pixel, no transform), clears the
`PendingRef`, and triggers re-solve. A click on an existing ref selects it.
Selected ref is removable via `Delete` or a context menu.

**Right pane — `RefTable` + `SolverReadout`.** A `DataGrid` over
`ObservableCollection<RefViewModel>` showing name, world-coord, texture-pixel,
per-ref residual (computed as `projected - actual` distance). A footer
`SolverReadout` shows the current `AreaCalibration` properties + the
overall RMS residual + reference count, plus a coloured badge keyed on the
12 px threshold.

**Header — area picker + commit button.** `AreaWorkspaceViewModel` loads on
area change; commit button is enabled only when (a) the solver has converged
(≥2 non-degenerate refs) AND (b) the calibration differs from whatever's
currently in the baseline JSON for that area.

### Data flow

```
Area selected
  └─> AreaCatalog.LoadArea(name)
        ├─> LandmarksReader.ReadForArea(name)
        ├─> NpcsReader.ReadForArea(name)
        ├─> MapTextureExtractor.EnsurePng(name)       (cached after first run)
        └─> BaselineFile.LoadAnchor(name)              (may be null)
  └─> AreaWorkspaceViewModel populated, projection layer rendered against
      either the loaded anchor or "no projection yet"

User picks landmark from list
  └─> PendingRef = { name, worldX, worldZ, kind, pendingTexturePixel: null }

User clicks source-map canvas
  └─> Ref = PendingRef.Complete(texturePixel = click)
        ├─> Refs.Add(Ref)
        ├─> Re-solve via LandmarkCalibrationSolver.Solve(Refs)
        ├─> Per-ref residuals recomputed
        └─> Projection layer redraws against the new AreaCalibration

User clicks "Commit anchor"
  └─> WorkspaceCommitService.Commit(area, calibration)
        └─> BaselineFile.UpsertAnchor(area, calibration with
              { Source = BundledBaseline, CalibrationZoom = 1.0,
                SchemaVersion = 1 })
        └─> Status-bar success; dirty-state cleared
```

### Error handling

| Failure | Behaviour |
|---|---|
| No source PNG for area (extractor never run on this machine) | Workspace fails to open with a message: "No extracted map texture for AreaX. Run `dotnet run --project tools/MapCalibrationFromScreenshot -c Release -- --phase extract-map --area AreaX`." Pointer to merged CLI keeps the responsibility split clean. |
| `landmarks.json` / `npcs.json` not on disk | Same gating — pointer to the CLI's extractor recipe. |
| `LandmarkCalibrationSolver.Solve` returns `null` (<2 non-degenerate refs) | Solver readout shows "Insufficient refs (need ≥2 spread points)"; commit button stays disabled; projection layer is hidden. |
| Residual > 12 px after all available refs added | Red badge on readout, refs flagged red-by-residual in the table. User decides whether to commit (the JSON carries the residual; consumers already render "approximate" affordances over the threshold). |
| User switches area with uncommitted refs | Confirm-discard dialog. Default: cancel. |
| `BaselineFile.UpsertAnchor` IO failure (file locked, disk full) | Surface as a non-fatal status-bar error; refs and solver state preserved; user can retry. |
| Clicking the source map with no `PendingRef` selected | No-op (or a subtle "select a landmark from the list first" toast). Never silently materialise a ref against the wrong landmark. |

### Testing

The factored common lib is unit-testable today — `NccTemplateMatch`,
`BaselineFile`, `LandmarksReader`, `NpcsReader`, etc., are pure functions over
files / byte arrays. Existing CLI tests (if any) migrate to a new
`tests/Mithril.MapCalibration.Tools.Common.Tests` project alongside the lib.

The WPF tool itself is dev-only and not test-gated. Verification path:

1. Build green (`dotnet build tools/MapCalibrationWpf/MapCalibrationWpf.csproj`).
2. Launch (`dotnet run --project tools/MapCalibrationWpf`).
3. Open AreaSerbule, place the 5 canonical refs (Kleave, Sus Cow, etc.),
   confirm residual is in the same ballpark as the merged CLI's verified
   0.30 px outcome. ("Same ballpark" because manual clicks have ~1 px noise
   the CLI's sub-pixel NCC peak doesn't.)
4. Open AreaEltibule, do the same, confirm the workspace produces a working
   calibration where the merged CLI failed. *This is the validation that
   motivated the project.*
5. Spot-check that the committed `map-calibration-baseline.json` round-trips
   through `BundledBaselineLoader` (the existing consumer used by the shipped
   shell).

No automated UI testing infra. The window is small (~3 panes, ~20 controls
total) and dev-only.

## Phasing

The MVP is genuinely useful alone. Subsequent phases extend the workspace's
surface without changing the factoring.

### Phase 1 — Workspace MVP

Above. Source-map-click ref-capture, live solver, commit to baseline JSON.
Unblocks the ~34-area backlog for every area where source-map clicks suffice.
**Estimated ~600–800 LOC of WPF + ~10 files moved into the new common lib.**

### Phase 2 — NCC live overlay

Port the merged CLI's NCC pipeline into a `NccOverlayService` that renders
detection rectangles on the `ScreenshotPad` (the pasted-screenshot canvas
gains a center-pane role). Live sliders for `--icon-render-size`,
`--detection-threshold`, `--exclude-type`. Kills the blind-tuning loop.

### Phase 3 — Screenshot-bbox ref-capture modality

Second capture modality: paste a screenshot of a single
green-highlighted icon, draw the screenshot's map-rect (mouse-drag bbox),
click the green-highlighted icon. The tool projects the click through the
bbox-affine to a texture pixel and materialises a ref. Texture-pixel math:

```
textureX = (clickX - mapRect.left) * texW / mapRect.W
textureY = (clickY - mapRect.top)  * texH / mapRect.H
```

Load-bearing assumption (same as #854 today): the screenshot was taken at
`CalibrationZoom = 1.0`. The tool surfaces this as an explicit confirm dialog
the first time per session.

Optional sub-feature: auto-detect the green-pixel centroid inside the
user-drawn bbox as a "suggested click" — the same green-pixel logic #857
proposed for the CLI lives in the common lib and the WPF UI consumes it.

### Phase 4 — Native PG-window capture

`User32.PrintWindow` button: "Capture PG" snaps the live game window directly
to the workspace's screenshot pad without Alt+PrtSc / clipboard round-trip.
~50–100 LOC. Removes the last screenshot-mechanics friction step.

### Phase 5 (future, not committed) — 3a auto-bootstrap + 3b slider-tune

Both surface as additional capture modalities on the same workspace:

- **3a auto-bootstrap** — a "Bootstrap from landmarks" button that synthesises
  refs from the world-bounds union of all area landmarks. The handedness
  problem (no tiebreaker with N=1 calibrated area today) blocks this from
  being a "ship to baseline" path; v1 of 3a only previews the bootstrapped
  calibration without writing it, so the user can sanity-check it against the
  source map by eye and choose to commit, flip handedness, or discard.
- **3b slider-tune** — origin/scale/rotation sliders + a handedness toggle that
  drag the projection overlay live until it visually sits on painted features.
  Tool then captures the slider values as an `AreaCalibration` and the user
  commits. Useful for areas where painted features are clearer than icons.

Both phases depend on Phase 1's solver-readout + projection-overlay being in
place; they are pure surface extensions over the same data model.

## Open questions deferred to later phases

- **Phase 2 NCC tuning UX**: do the sliders write back into a per-area
  `NccProfile` cached on disk, or are they fully transient per session? Defer
  until the live-overlay has been tried on a few real areas.
- **Phase 3 zoom-not-1.0 detection**: can we detect from EXIF / window-size
  metadata that a screenshot was taken at non-canonical zoom and refuse it
  instead of relying on a confirm dialog? Defer.
- **Phase 4 multi-monitor PG window**: if PG is on a non-primary display,
  does `PrintWindow` cooperate? Defer.
- **Phase 5 3a handedness**: is there ever a tiebreaker without per-area
  human input? Probably not. Defer.

## Non-goals (worth restating)

- Not refactoring `Mithril.MapCalibration` (the lib in `src/`). Solver,
  `AreaCalibration`, baseline JSON schema, projector all stay as-is. The
  WPF tool *consumes* this lib; it does not redesign it.
- Not changing the merged CLI's behaviour, recipe, or CLI surface. The CLI
  gains a `ProjectReference` to the new common lib and loses 10 source
  files; its `Program.cs`, `CliArgs.cs`, `Pipeline.cs`, `ScreenshotCalibrator.cs`,
  `SelfTest.cs`, and verified Serbule recipe are untouched.
- Not shipping anything into `Mithril.Shell`. WPF tool stays under `tools/`
  and is invoked via `dotnet run`. Not added to any installer.
- Not implementing any end-user UX. Legolas's per-user click-to-refine path
  already owns that surface.

## Relationship to other work

- Supersedes [#857](https://github.com/moumantai-gg/mithril/issues/857). Phase 3
  reaches the same outcome with better UX; #857 should close with a pointer
  here when this tool's Phase 3 lands.
- Coexists with [#858](https://github.com/moumantai-gg/mithril/issues/858).
  #858 benefits the headless CLI's NCC pipeline on areas where NCC works at
  all; that's an independent code path from this tool.
- Reuses the calibration ceiling reality from
  [legolas_calibration_findings](https://github.com/moumantai-gg/mithril/wiki/Legolas-Calibration-Findings):
  k≈1, ~±10% non-affine ceiling, no zoom auto-detection. Phase 1 surfaces the
  residual to the user and lets them decide; the tool does not pretend the
  ceiling away.
- Bundled JSON consumer chain is `BundledBaselineLoader` →
  `MapCalibrationService` → `Mithril.Reference.Models.*` consumers (Legolas
  primarily). No consumer changes needed.

---

— drafted by Claude (Opus 4.7), posted by @arthur-conde
