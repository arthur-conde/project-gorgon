# Auto-Calibration Gate Study — Verdict

**Date:** 2026-05-30
**Issue:** [mithril#897](https://github.com/moumantai-gg/mithril/issues/897)
**Spec:** [docs/superpowers/specs/2026-05-30-map-calibration-gate-study-design.md](superpowers/specs/2026-05-30-map-calibration-gate-study-design.md)
**Verdict:** **PROCEED** to the auto-calibration engine spec. H1 (discrete orientation) is decisive. H4 (cold automation) is **feasible and demonstrated end-to-end** on a landmark-dense area (Serbule: zero-prior, 0.93 px residual, 23 RANSAC inliers). Sparse / irregular-border areas (Eltibule, Kur) still need stronger detection — and a **texture-deviation front-end now looks like the lever** (see below). The remaining work is engineering, not a falsification of the model.

This records the gate-study result so the engine spec can cite it. The history below is kept deliberately: **two separate "it's infeasible" conclusions in this study turned out to be corrupted inputs, not real failures.** That pattern is the single most important carry-over.

## Headline lesson — verify your inputs first (it bit us twice)

1. **The DPI bug.** For most of the study, every screenshot run failed: garbage NCC peaks, zero RANSAC inliers, blank debug overlays. Root cause was the shared image loader (`ImageIo`, fixed in [PR #907](https://github.com/moumantai-gg/mithril/pull/907)): `Graphics.DrawImageUnscaled` honors a PNG's DPI metadata, so the author's 300-DPI cropped screenshots were drawn at ~1/3 size into the top-left corner of a 96-DPI buffer with the rest black. Every metric was computed against a mostly-black image. The tell was a debug image showing the map shrunk into a black corner. Fix: draw into an explicit pixel-sized destination rect, ignoring DPI.

2. **The wrong-filename false negative.** The texture-deviation probe (below) first reported mean local-NCC ~0.03 — "no shared structure, approach dead." Cause: it was pointed at `Map_Serbule.v4.png`, which doesn't exist; the real file is `Map_AreaSerbule.v4.png`. With the correct file, mean NCC jumps to **0.86** and the approach works. A positive control (screenshot vs itself → NCC 0.999) is what flagged that the near-zero cross-result had to be an input problem, not a real one.

**Carry into the engine: before trusting any detection metric, validate the loaded pixels — size, not-black, the file actually exists and is the one you meant, icons visible. Keep a positive control (self-NCC ≈ 1.0) in the test suite.**

## Sample

Six areas — the author's full reachable set — spanning outdoor (Serbule, Eltibule, KurMountains), cave (Cave1, MyconianCave), and indoor (Casino). Rotation/handedness from the user-refinement store (`refinements.json`, frame-invariant). Serbule additionally has a committed texture-frame baseline and a reproduced cold solve (see H4).

## Results (Half A — `measure`)

| area | rotationDeg | orientation | mirrorNorth | scale (tex) | predScaleX | scaleRatioX | insetMax | similarity residual |
|---|---|---|---|---|---|---|---|---|
| AreaCave1 | 0.0003° | 0° | false | — | — | — | — | 0.049 px |
| AreaCasino | −0.0001° | 0° | false | — | — | — | — | 0.006 px |
| AreaSerbule | 0.019° | 0° | false | 0.823 | 0.984 | 0.836 | **8.9%** | 0.303 px |
| AreaMyconianCave | 0.032° | 0° | false | — | — | — | — | 0.530 px |
| AreaEltibule | −179.996° | 180° | false | — | — | — | — | 0.336 px |
| AreaKurMountains | 179.999° | 180° | false | — | — | — | — | 0.335 px |

(`scale`/`inset` need a texture-frame solve per area; only Serbule has one committed, hence `—` for the rest. Residuals are from each area's own solve.)

## Hypotheses

### H1 — axis-aligned orientation ∈ {0, π} — **CONFIRMED**

All six areas snap to exactly {0°, 180°}: four within **0.04°** of 0, two within **0.004°** of π, with **no in-between angle**. This both falsifies the naive "rotation ≈ 0" (Eltibule and KurMountains are dead-on 180°) and confirms the discrete-orientation model. The engine's "enumerate the axis-aligned orientation states" bootstrap is justified by real data — and it must **not** assume θ = 0. Handedness (`mirrorNorth`) was constant in this sample; the orientation variation surfaced as rotation 0/π (an equivalent parameterization of the same discrete-4-state group).

**Subtlety the texture-deviation probe surfaced:** the 0/π split is in the **world→texture-pixel** mapping (where landmark icons are *placed*), not in the texture image itself. The base texture art aligns to the in-game screenshot at **0° for every area** (deviation NCC picks 0° for both Serbule and the 180°-calibrated Eltibule). So a detection front-end aligns texture↔screenshot at 0°; the discrete {0, π} only matters later, in the world→pixel solve.

### H2 — isotropy — **indirectly supported**

A 4-parameter *similarity* fits all six areas to **sub-pixel** residual (0.006–0.53 px). If the renderer needed an anisotropic (affine) model, a similarity could not fit that tightly. A direct affine-vs-similarity contest is only meaningful at kept ≥ 4 detected points per area; that becomes available once detection is robust enough on the sparse areas (currently only Serbule clears it).

### H3 — consistent border inset — **single data point; completion path identified**

Serbule's landmark bbox projects into the texture with an **8.9%** max-edge inset (scaleRatioX 0.836). Consistency across areas can't be assessed from n=1; the other five need a texture-frame solve, gated on the same detection robustness as H4.

### H4 — cold zero-prior correspondence — **DEMONSTRATED on Serbule; open on sparse areas**

**Serbule: cold auto-calibration confirmed.** With the DPI bug fixed, the proven [`tools/MapCalibrationFromScreenshot`](../tools/MapCalibrationFromScreenshot) (PR #854, RANSAC + type-aware assignment) recovers Serbule's committed baseline **from zero priors** — no stored calibration, no manual clicks:

```
dotnet run --project tools/MapCalibrationFromScreenshot -- \
  --screenshot "study/screenshots/AreaSerbule.png" --area AreaSerbule \
  --icons-dir "study/icons" --map-dir "study/textures" \
  --map-rect "0,0,881,920" --icon-render-size 16 \
  --icon-size landmark_npc=17x16 --detection-threshold 0.8 --dry-run
```

→ **scale 0.8226, rotation 0°, residual 0.93 px, 23 RANSAC inliers**, matching the manual baseline (0.30 px) well under the 12 px `CalibrationGoodResidualPx` gate.

Two enablers were decisive, both non-obvious:

1. **The DPI fix** — without it, nothing detects.
2. **Include NPCs at the right per-type size.** It is tempting to drop the noisy NPC type (`--exclude-type Npc`), but at `--icon-size landmark_npc=17x16` (PG renders that sprite at a 17×16 aspect, not square) the NPCs become the *bulk of the real anchors*: inliers jump **8 → 23**, and that density is what makes RANSAC robust.

**Eltibule + KurMountains (both 180°): not yet robust.** Both are sparser, with a rocky outer border whose texture matches the icon templates. The `--border-mask` (PR #908, edge flood-fill of non-vegetation/water) is necessary — on Eltibule it drops ~125 rim false positives and lets the solver reach the correct orientation — but not sufficient: too few interior landmark icons match strongly enough for stable RANSAC. This is the open detection-quality problem, and the texture-deviation front-end below is the most promising attack on it. (Pixel dims for `--map-rect "0,0,W,H"`: Eltibule 921×914, Kur 981×980.)

## Texture-deviation probe — **promising; likely the sparse-area detection front-end**

Idea: the icon-free CDN base texture (`Map_Area<X>.v4.png`) and the in-game map screenshot are the same artwork; align them by extent (via the map-rect) and compute a **sliding-window local NCC**. Local NCC is invariant to per-window linear brightness/contrast, so terrain matches through PG's restyle/tint and the rocky border largely cancels; an icon disrupts the local match → low NCC → "added content" candidate. Prototyped as a visualization in [`tools/MapTextureDeviationProbe`](../tools/MapTextureDeviationProbe) (throwaway).

**Verified results (after the wrong-filename fix above):**

| run | mean local-NCC | low-NCC pixels (icons) | border-band deviation | interior deviation |
|---|---|---|---|---|
| Serbule vs itself (positive control) | **0.999** | 0.0% | — | — |
| **Serbule** vs base texture | **0.86** | **2.8%** | 0.178 (low — border cancels) | 0.127 |
| **Eltibule** vs base texture | **0.79** | **13%** | 0.495 (rocky rim partly deviates) | 0.128 |

Terrain and (on Serbule) the border largely cancel; deviation is **localized**, and eyeballing the heatmaps/overlays confirms it: icons appear as discrete bright/low-NCC blobs against a dark matched-terrain field, with quiet terrain in between. This is exactly the wanted signal.

**Caveats (why it's a front-end, not the whole detector):**

- It flags **all** added/changed content, not only icons: the central keep/structure on Serbule, map labels, and fog read as deviation too. So it is a **candidate generator** — the engine must follow it with a **shape/size filter** (icon-sized compact blobs vs large smooth fog vs elongated structures) and then run icon-template NCC *only inside* the candidate regions. That both cuts false positives and slashes the search space on sparse areas.
- It needs the texture aligned to the screenshot (the same `--map-rect` the proven tool already uses), and the base texture file must actually exist for the area.

**This is the recommended sparse-area rescue and a strong candidate for the engine's detection stage:** terrain-subtract via local NCC → shape-filter the blobs → type-aware RANSAC template match within candidates.

## Verdict & implications for the engine spec

**PROCEED.** The renderer is regular: per-area global isotropic similarity with a **discrete axis-aligned orientation ∈ {0, π}** (H1 decisive, n=6), sub-pixel-fittable everywhere (H2 indirect), with a measured border inset on the one fully-solved area (H3 partial). Cold, near-zero-ref calibration is **demonstrated** on a landmark-dense area. Concrete carry-overs:

- **Validate inputs first.** File-exists, size, not-black, icons present; keep a self-NCC ≈ 1.0 positive control. Two false "infeasible" verdicts in this study came from bad inputs.
- **Orientation:** enumerate the discrete {0, π} state set for the world→pixel solve; do not assume θ = 0. Align the texture↔screenshot images at 0°.
- **Detection (the bottleneck on sparse areas):** texture-deviation local-NCC front-end → shape/size filter → type-aware template NCC within candidates. Plain whole-image template NCC is too weak on sparse interiors; terrain subtraction makes the icons pop.
- **Correspondence:** RANSAC + type-aware assignment (per-type, trimmed to landmark count) + a scale plausibility guard, not greedy nearest-neighbour. **Include NPCs**, with per-type sizing (`landmark_npc` is 17×16) — they are the anchors that make landmark-dense fits robust.
- **Border-mask irregular zones** (PR #908) as a complement; on Serbule the deviation front-end already cancels the border, on Eltibule the rocky rim partly survives and the mask helps.
- **Honesty ceiling:** the ±10% non-affine map warp ([PR #449](https://github.com/moumantai-gg/mithril/pull/449)) still applies — "approximate location" UX, not pixel-perfect rendezvous.

## Remaining work

1. **Build the texture-deviation → shape-filter → template stage** and re-test on Eltibule/Kur. The probe shows the terrain-subtraction step works; the missing piece is the blob shape/size filter that separates icons from structures/fog, then template NCC within candidates.
2. **Texture-frame solves for the other five areas** (completes H3 inset consistency + a real per-area H2 affine contest at kept ≥ 4). Run the proven `tools/MapCalibrationFromScreenshot` once sparse-area detection is robust, commit those `AreaCalibration`s to the baseline, re-run `measure`, and append the inset values here and on the wiki.

## Teardown

The throwaway study scaffolding (`tools/MapCalibrationStudy`, and `tools/MapTextureDeviationProbe` once its idea is folded into the engine) is deleted in its own PR per #897 Task 10 — separate from this verdict and from any further measurement runs, per the squash-merge-orphans rule.
