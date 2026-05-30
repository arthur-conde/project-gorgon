# MapTextureDeviationProbe

**Throwaway R&D probe** for the auto-calibration gate study ([mithril#897](https://github.com/moumantai-gg/mithril/issues/897), task 2). Delete once its idea is folded into the engine's detection stage.

## The idea

The icon-free CDN base texture (`Map_Area<X>.v4.png`) and the in-game map screenshot (which has icons + fog drawn on it) are the same artwork. Align them by extent and compute a **sliding-window local NCC**. Local NCC is invariant to per-window linear brightness/contrast, so terrain matches through PG's restyle/tint (and the rocky border largely cancels), while an icon disrupts the local match → low NCC → "added content" candidate.

## The result: **promising**

| run | mean local-NCC | low-NCC pixels | border-band dev | interior dev |
|---|---|---|---|---|
| Serbule vs itself (positive control) | 0.999 | 0.0% | — | — |
| Serbule vs base texture | 0.86 | 2.8% | 0.178 | 0.127 |
| Eltibule vs base texture | 0.79 | 13% | 0.495 | 0.128 |

Terrain (and on Serbule the border) cancels; deviation is localized — eyeballing the overlay shows icons as discrete low-NCC blobs against matched terrain. It flags **all** added content (icons + structures + fog/labels), so it's a **candidate generator**: follow it with a shape/size filter, then run template NCC only inside the candidate regions.

> **Cautionary note:** this probe first reported ~0.03 NCC ("dead") — because it was pointed at `Map_Serbule.v4.png`, which doesn't exist; the real file is `Map_AreaSerbule.v4.png`. The self-NCC = 0.999 positive control is what exposed the bad input. Always confirm the texture file exists and is the one you meant.

See `docs/map-calibration-gate-verdict.md` § "Texture-deviation probe".

## Shape/size filter (`--blobs`) — mithril#897 remaining-work item 1

The deviation map flags **all** added content. `--blobs` adds the downstream stage that turns it into a clean icon-candidate set:

1. **Threshold** the deviation map (`dev >= 1 − low-ncc`) → binary foreground.
2. Optional `--border-mask` (drops the rocky rim — the edge-connected non-veg/water flood-fill from `BorderMask`) and `--close N` (morphological close to bridge fragmented icon pixels).
3. **8-connected component labelling** → blobs.
4. **Per-blob features**: area, bbox + aspect ratio, solidity (area / bbox area), centroid, mean/peak deviation.
5. **Classify** each blob `icon` / `fog` / `structure`. Icons render ~16 px → compact, high-solidity, high-peak blobs roughly 12–30 px across (window smearing widens them). Fog-of-war = large, soft, low-gradient. Structures (the Serbule keep, labels) = large, elongated or high-contrast. **Size is the primary separator**; solidity + aspect + peak-deviation reject labels, fragmented noise, and soft fog.
6. Writes `<stem>_blobs.png`: **green = icon candidate, blue = fog, red = structure**, bounding boxes drawn, screenshot underneath. With `--ground-truth`, yellow crosses mark every landmark/NPC projected through the committed baseline.

### Verified results (window 11, low-ncc 0.5, defaults)

| area | icon candidates | fog | structure | notes |
|---|---|---|---|---|
| **Serbule** (clean, has baseline) | **21** | 0 | 6 | central keep → 1 structure blob; precision 15/21 (71%) land on a landmark/NPC |
| **Eltibule** (`--border-mask`) | **14** | 0 | 1 | border-mask clears **96%** of fg (rocky rim); lakes match → not flagged |
| **KurMountains** (`--border-mask`) | **24** | 0 | 6 | snow terrain deviates more → border-mask clears only 71%, noisier |

**Serbule ground-truth (the one area with a committed baseline):** 46 refs project on-screen; **28 project onto the central keep** (Serbule town packs NPCs onto the keep — structurally unseparable as individual icon blobs). On the **18 separable refs, recall is 17/18 = 94%**. Precision 71% is a lower bound — several "false-positive" candidates are real map icons (signs, dungeon entrances) absent from `landmarks.json`/`npcs.json`.

**Headline:** Eltibule yields **14 icon candidates** where the proven calibrator's whole-image template NCC gets only ~3 stable RANSAC inliers today. The shape filter cleanly separates icons (green) from the keep/structures (red) and rejects the rocky rim + fog/water.

## Usage

```
dotnet run --project tools/MapTextureDeviationProbe -- \
  --screenshot <png> --texture <png> --out-dir <dir> \
  [--window 11] [--low-ncc 0.5] [--orientation auto|0|180] [--register] \
  [--blobs [--border-mask] [--close 1] \
   [--min-area 12] [--max-icon-area 900] [--min-solidity 0.35] \
   [--max-aspect 2.5] [--min-peak 0.7] \
   [--ground-truth --area AreaSerbule --landmarks <json> --npcs <json> \
    --baseline <json> [--gt-tol 20]]]
```

- Writes `<stem>_deviation.png` (heatmap) and `<stem>_overlay.png` (screenshot with low-NCC pixels tinted red) for eyeballing.
- `--blobs` adds the shape/size filter stage above and writes `<stem>_blobs.png`.
- `--ground-truth` (Serbule only — the one committed baseline) projects landmarks+NPCs and prints recall/precision against the icon candidates.
- `--register` runs a coarse rotation × scale × offset sweep and prints the best achievable mean local-NCC (the diagnostic that distinguishes "misregistered" from "different artwork").
- Prints a self-NCC-style sanity line (screenshot mean luma) and a positive control if you pass the screenshot as its own `--texture`.

### Example (Serbule with ground-truth)

```
dotnet run --project tools/MapTextureDeviationProbe -- \
  --screenshot study/screenshots/AreaSerbule.png \
  --texture   study/textures/Map_AreaSerbule.v4.png \
  --out-dir <tempdir> --blobs --ground-truth --area AreaSerbule \
  --landmarks src/Mithril.Shared/Reference/BundledData/landmarks.json \
  --npcs      src/Mithril.Shared/Reference/BundledData/npcs.json \
  --baseline  src/Mithril.MapCalibration/BundledData/map-calibration-baseline.json
```
