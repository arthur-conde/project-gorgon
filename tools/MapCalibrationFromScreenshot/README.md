# MapCalibrationFromScreenshot

Offline tool for [mithril#852](https://github.com/moumantai-gg/mithril/issues/852) — derive a per-area `AreaCalibration` from a single in-game map screenshot.

**Achieves sub-pixel residual** (0.30 px on AreaSerbule, parity with manual click-calibration via Legolas) when invoked with the recipe in [Real usage](#real-usage) below.

## What it does

Pipeline:

1. **Extract icon templates** from `WindowsPlayer_Data/sharedassets0.assets` (landmark pins, with per-icon `Sprite.m_Pivot` sidecars).
2. **Extract the area's map texture** from `StreamingAssets/aa/StandaloneWindows64/maps_assets_*.bundle`.
3. **Locate the map rect** inside the screenshot (manual `--map-rect` recommended over the NCC auto-detect — eyeball the visible map's bbox in any image viewer, takes 30 seconds in Gimp).
4. **Detect icons** in the screenshot via single-scale NCC with sub-pixel parabolic peak refinement.
5. **Apply pivot offset** to recover each icon's world-anchor pixel.
6. **RANSAC assignment** over the cross-product of (detection, ref) pairs, with spatial-diversity + refit-residual tiebreaker to avoid degenerate local optima.
7. **Iterative refinement** (LO-RANSAC pattern): drop the worst-residual inlier if it's > 2× the median, re-solve, repeat. Catches mis-pairings between world-close refs (e.g. NPCs in a town square where multiple stand within a few world units of each other).
8. **Solve** via [`LandmarkCalibrationSolver`](../../src/Mithril.MapCalibration/LandmarkCalibrationSolver.cs).
9. **Persist** the result into `src/Mithril.MapCalibration/BundledData/map-calibration-baseline.json`.

This tool is **offline**. The PG game does **not** need to be running — only the screenshot file needs to have been captured from a live PG session. The tool reads PG's on-disk install via Steam-AppID detection but never touches the running game process (consistent with PG's anti-injector stance — see memory `pg_anti_injector_stance`).

## Build & run

Built outside `Mithril.slnx` to keep AssetsTools.NET out of central package management:

```bash
dotnet build tools/MapCalibrationFromScreenshot/MapCalibrationFromScreenshot.csproj -c Release
```

Self-test (no PG / no `classdata.tpk` needed — synthesises a fake screenshot end-to-end):

```bash
dotnet run --project tools/MapCalibrationFromScreenshot -c Release -- --phase self-test
```

## Real usage

**One-time prerequisites:**

1. Download `classdata.tpk` (~290 KB, covers all Unity versions UABEA supports including 6000.x). Lives in the UABEA repo, NOT in AssetsTools.NET's release attachments:
   ```
   https://github.com/nesrak1/UABEA/raw/master/ReleaseFiles/classdata.tpk
   ```
   Place it next to the tool binary or pass `--tpk <path>`.

2. Pre-extract icon templates (cached; re-run only after a PG patch):
   ```powershell
   dotnet run --project tools/MapCalibrationFromScreenshot -c Release -- --phase extract-icons
   ```

3. For each area, capture **one** in-game map screenshot (zoom all the way out, then `M` + screenshot tool). Save anywhere convenient.

4. For each area, measure the **visible map's bbox** in your screenshot using Gimp (or any image viewer with a measure tool). Note the top-left pixel `(x, y)` and the bbox `(width, height)`. The map area in PG has straight edges — easy to follow with Gimp's measure tool.

**Per-area run (verified recipe):**

```powershell
dotnet run --project tools/MapCalibrationFromScreenshot -c Release -- `
    --screenshot ".\AreaSerbule.png" `
    --area AreaSerbule `
    --map-rect "9,13,934,976" `
    --icon-render-size 16 `
    --detection-threshold 0.9 `
    --projection-overlay ".\verify.png"
```

The recipe (`--icon-render-size 16 --detection-threshold 0.9`) was empirically tuned on AreaSerbule and produces sub-pixel residuals. The high threshold (0.9) keeps only the cleanest NCC matches; iterative RANSAC refinement handles the few inevitable mis-pairings.

Add `--dry-run` to preview without writing to the baseline JSON. Open `verify.png` afterwards — green rects mark the RANSAC inliers actually used in the solve; they should visually land on real rendered icons.

## Screenshot-capture guidance

- **Open the in-game map UI** (default `M` key) before screenshotting.
- **Zoom all the way out** so the full area map fits in view. Pin icons stay constant pixel size at any zoom — [zooming out doesn't make icons smaller](https://github.com/moumantai-gg/mithril/issues/852#issuecomment-4569871060), it just maximises their visual separation. The `--map-rect` measurement assumes the visible map = the entire texture (no pan).
- Tight clusters of same-type refs (e.g. several NPCs standing in a town square within ~15 world units of each other) can cause RANSAC's per-ref dedup to mis-pair detections — the iterative refinement (step 7) drops outliers automatically, so this normally fixes itself, but you may end up with 2-3 fewer inliers than you'd expect from the screenshot.

## Tuning knobs

| Flag | When to use |
|---|---|
| `--icon-render-size <px>` | Default empirical sweet-spot is 16. Smaller produces less-discriminative templates; larger produces noise that overwhelms RANSAC. Override only if a particular area has different UI scaling. |
| `--detection-threshold <0..1>` | Default 0.5. Push to 0.7-0.9 when there are too many false-positive NCC matches; lower when recall is poor on a sparse area. |
| `--icon-size <name>=<W>x<H>` | Force a specific template to exact pixel dimensions, bypassing the aspect-preserving resize. Use when a sprite renders at an aspect ratio the source asset doesn't match (verified for `landmark_npc` on Serbule — source 236×256, PG renders 17×16). |
| `--exclude-type <Type>` | Drop a landmark Type from the RANSAC pool entirely. Useful when a template fundamentally doesn't match PG's actual sprite. |
| `--map-rect <x,y,w,h>` | Bypass auto-detect with an exact bbox measured in Gimp. Recommended for all real runs. |
| `--detections-csv <path>` | Load the detection pool from a CSV (`screenshotX,screenshotY,type,iconName,score`) instead of running whole-image template NCC. Pairs with the deviation probe's blob-typed detections (type-aware template NCC within icon blobs) — the lever that cold-solves sparse areas (Eltibule, Kur) where whole-image NCC starves correspondence. See `tools/MapTextureDeviationProbe`. |
| `--seed <rot,scale,ox,oy,mirror>` | Bypass RANSAC; run a guided ICP from a known-orientation seed (the frame-invariant {0,π} rotation). For sparse areas with a transferable orientation prior. |
| `--debug [--outdir <dir>]` | Dump every intermediate-stage visualization (`<area>_detections.png`, `<area>_mask.png`, `<area>_projection.png`) in one switch. `--mask-debug <path>` is the border-mask diagnostic alone (masked rim tinted red, detections green=kept / red=dropped). |
| `--projection-overlay <path>` | Render the inlier set on top of the screenshot for visual verification. Yellow crosses for every projected ref, green outlines for the RANSAC inliers. |
| `--debug-image <path>` | Render every NCC detection that cleared threshold (cyan rects + red crosses for pivot-corrected anchors). |

## Achieved accuracy (AreaSerbule, verified 2026-05-29)

Recovered calibration vs ground truth derived from manually-measured icon bboxes:

| | Truth | Recovered | Off by |
|---|---|---|---|
| scale | 0.823 px/unit | 0.8226 | 0.04% |
| rotation | ~0 rad | 0.000 | exact |
| originX | -161 | -159.7 | 1.3 px |
| originY | 2273 | 2271.7 | 1.3 px |
| residualPixels | — | **0.30** | — |

8 inliers used (after iterative refinement dropped 2 mis-paired NPCs from the central courtyard cluster). Per-inlier residuals 0.07-0.58 px. Well under the 12 px `CalibrationGoodResidualPx` shipping bar; at parity with manual click-calibration via Legolas.

## Out of scope (v1)

- **Shell integration.** This is a standalone tool. Once it has populated the baseline JSON, the result rides into Mithril via the existing [`BundledBaselineLoader`](../../src/Mithril.MapCalibration/Internal/BundledBaselineLoader.cs).
- **Zoomed-in screenshots** (where only a pan-window of the texture is visible). The `--map-rect` "screenshot contains the entire texture" assumption breaks; the user must zoom all the way out before screenshotting.
- **Image-alignment refinement** (compositing icons onto the texture and minimising the diff against the screenshot — see #852 thread for the proposal). Would push below the non-affine ceiling but adds substantial implementation; current sub-pixel result is good enough for v1.
- **Swap-detection for mis-paired refs** (e.g. Selphie ↔ Tadion in Serbule). Iterative refinement currently drops one of the two; a smarter pass could swap their assignments and keep both. Low priority — losing 1-2 inliers from a 10+ set doesn't affect accuracy.
- **OCR of map artwork.** Foreclosed by [#848](https://github.com/moumantai-gg/mithril/issues/848).

## Architecture quick-tour

| File | What |
|---|---|
| [`Program.cs`](Program.cs) | Entry point, error surface |
| [`CliArgs.cs`](CliArgs.cs) | Argument parser + usage |
| [`Pipeline.cs`](Pipeline.cs) | Phase orchestration |
| [`SteamInstall.cs`](SteamInstall.cs) | Steam-AppID-based PG install locator (lifted from `MapAssetSpike`) |
| [`IconTemplateExtractor.cs`](IconTemplateExtractor.cs) | `sharedassets0.assets` → per-icon PNG + `index.json` (with pivots) |
| [`MapTextureExtractor.cs`](MapTextureExtractor.cs) | Per-area bundle → map PNG (vertical-flipped to match in-game render orientation) |
| [`ImageIo.cs`](ImageIo.cs) | PNG ↔ `GrayImage`, bilinear resize, drawing primitives |
| [`NccTemplateMatch.cs`](NccTemplateMatch.cs) | Hand-rolled normalised cross-correlation (mask-aware, sub-pixel parabolic refinement, `Parallel.For` outer loop) |
| [`MapRectLocator.cs`](MapRectLocator.cs) | NCC scale-ladder → map-in-screenshot bbox (auto-detect; `--map-rect` override recommended) |
| [`LandmarksReader.cs`](LandmarksReader.cs) | Slim `landmarks.json` reader |
| [`NpcsReader.cs`](NpcsReader.cs) | Slim `npcs.json` reader (positions only) |
| [`PlayerLogScanner.cs`](PlayerLogScanner.cs) | Most-recent `[Status]` line for the area |
| [`ScreenshotCalibrator.cs`](ScreenshotCalibrator.cs) | Glue: locate → detect → pivot-correct → RANSAC → iterative refine → solve |
| [`BaselineFile.cs`](BaselineFile.cs) | Write the result into `map-calibration-baseline.json` |
| [`SelfTest.cs`](SelfTest.cs) | Synthetic end-to-end harness (no PG / no tpk) |
