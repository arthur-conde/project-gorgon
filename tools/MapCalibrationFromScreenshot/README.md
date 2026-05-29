# MapCalibrationFromScreenshot

Offline tool for [mithril#852](https://github.com/moumantai-gg/mithril/issues/852) — derive a per-area `AreaCalibration` from a single in-game map screenshot.

## What it does

Pipeline:

1. **Extract icon templates** from `WindowsPlayer_Data/sharedassets0.assets` (landmark pins + player-pin variants), with per-icon `Sprite.m_Pivot` sidecars.
2. **Extract the area's map texture** from `StreamingAssets/aa/StandaloneWindows64/maps_assets_*.bundle`.
3. **Locate the map rect** inside the screenshot via NCC scale-ladder.
4. **Detect icons** in the screenshot via single-scale NCC (icons are zoom-invariant per [issue #852 comment](https://github.com/moumantai-gg/mithril/issues/852#issuecomment-4569871060) — no pyramid needed).
5. **Apply pivot offset** to recover each icon's world-anchor pixel (teardrop pins anchor at bottom tip, not centre — see comment for why this is load-bearing).
6. **Assign + solve** via [`LandmarkCalibrationSolver`](../../src/Mithril.MapCalibration/LandmarkCalibrationSolver.cs).
7. **Persist** the result into `src/Mithril.MapCalibration/BundledData/map-calibration-baseline.json`.

This tool is **offline**. The PG game does **not** need to be running — only the screenshot file needs to have been captured from a live PG session earlier. The tool reads PG's on-disk install via Steam-AppID detection but never touches the running game process (consistent with PG's anti-injector stance — see memory `pg_anti_injector_stance`).

## Build & run

Built outside `Mithril.slnx` to keep AssetsTools.NET out of central package management:

```bash
dotnet build tools/MapCalibrationFromScreenshot/MapCalibrationFromScreenshot.csproj -c Release
```

Self-test (no PG / no `classdata.tpk` needed — synthesises a fake screenshot end-to-end):

```bash
dotnet run --project tools/MapCalibrationFromScreenshot -c Release -- --phase self-test
```

Real usage (single area):

```bash
# One-time prerequisite: download classdata.tpk (~290 KB, covers all Unity versions
# the UABEA build supports — including 6000.x). It lives in the UABEA repo, NOT in
# AssetsTools.NET's release attachments:
#   https://github.com/nesrak1/UABEA/raw/master/ReleaseFiles/classdata.tpk
# Place it next to the tool binary or pass --tpk <path>.

# Pre-extract icon templates (cached; re-run only after a PG patch):
dotnet run --project tools/MapCalibrationFromScreenshot -c Release -- --phase extract-icons

# Solve for one area:
dotnet run --project tools/MapCalibrationFromScreenshot -c Release -- \
    --screenshot "C:\Users\me\Pictures\pg-map-serbule.png" \
    --area AreaSerbule \
    --player-coord -120.5,340.2 \
    --zoom 1.0
```

Add `--dry-run` to preview the change without writing to the baseline JSON.

## Screenshot-capture guidance

- **Open the in-game map UI** (default `M` key) before screenshotting.
- **Zoom all the way out** so the full area map fits in view. The auto-locator assumes the visible map = the entire texture, no pan (v1 limitation). Pin icons stay constant pixel size at any zoom — [zooming out doesn't make icons smaller](https://github.com/moumantai-gg/mithril/issues/852#issuecomment-4569871060), but it does maximise their visual separation.
- For dense areas (Serbule, Eltibule's portal cluster) icons may overlap at max zoom-out. If the assignment looks wrong by eye, zoom in to a sub-region where landmarks separate cleanly, take a second screenshot, and re-run — same calibration math, different visible window. (Multi-screenshot fusion isn't in v1; one good screenshot per area is the goal.)
- Read `--player-coord` off Mithril's HUD or the most recent `[Status]` line in `Player.log` — or pass `--player-log <path>` and let the tool extract it.

## Out of scope (v1)

- **Shell integration.** This is a standalone tool. Once it has populated the baseline JSON, the result rides into Mithril via the existing [`BundledBaselineLoader`](../../src/Mithril.MapCalibration/Internal/BundledBaselineLoader.cs).
- **Zoomed-in screenshots** (where only a pan-window of the texture is visible). The locator's "screenshot contains the entire texture" assumption breaks; the user must zoom out before screenshotting.
- **OCR of map artwork.** Foreclosed by [#848](https://github.com/moumantai-gg/mithril/issues/848).
- **Pushing below the ~10% non-affine residual ceiling** that PG's hand-painted maps impose. See memory `legolas_calibration_findings`.

## Architecture quick-tour

| File | What |
|---|---|
| [`Program.cs`](Program.cs) | Entry point, error surface |
| [`CliArgs.cs`](CliArgs.cs) | Argument parser + usage |
| [`Pipeline.cs`](Pipeline.cs) | Phase orchestration |
| [`SteamInstall.cs`](SteamInstall.cs) | Steam-AppID-based PG install locator (lifted from `MapAssetSpike`) |
| [`IconTemplateExtractor.cs`](IconTemplateExtractor.cs) | `sharedassets0.assets` → per-icon PNG + `index.json` (with pivots) |
| [`MapTextureExtractor.cs`](MapTextureExtractor.cs) | Per-area bundle → map PNG |
| [`ImageIo.cs`](ImageIo.cs) | PNG ↔ `GrayImage` |
| [`NccTemplateMatch.cs`](NccTemplateMatch.cs) | Hand-rolled normalised cross-correlation (mask-aware) |
| [`MapRectLocator.cs`](MapRectLocator.cs) | NCC scale-ladder → map-in-screenshot bbox |
| [`LandmarksReader.cs`](LandmarksReader.cs) | Slim `landmarks.json` reader |
| [`PlayerLogScanner.cs`](PlayerLogScanner.cs) | Most-recent `[Status]` line for the area |
| [`ScreenshotCalibrator.cs`](ScreenshotCalibrator.cs) | Glue: locate → detect → pivot-correct → assign → solve |
| [`BaselineFile.cs`](BaselineFile.cs) | Write the result into `map-calibration-baseline.json` |
| [`SelfTest.cs`](SelfTest.cs) | Synthetic end-to-end harness (no PG / no tpk) |
