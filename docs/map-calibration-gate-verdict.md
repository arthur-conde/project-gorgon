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
| AreaEltibule | −179.996° | 180° | false | 0.763 | 0.970 | 0.787 | **11.6%** | 0.650 px |
| AreaKurMountains | 179.999° | 180° | false | 0.569 | 0.721 | 0.788 | **11.3%** | 0.732 px |

(`scale`/`inset` need a texture-frame solve per area. Serbule, Eltibule and KurMountains now have committed texture-frame baselines; MyconianCave still `—`. Eltibule/Kur were solved **cold** via the deviation-blob → type → RANSAC pipeline — see H4. Residuals: Serbule/Eltibule/Kur are the texture-frame solve RMS; the others are from each area's overlay solve.)

## Hypotheses

### H1 — axis-aligned orientation ∈ {0, π} — **CONFIRMED**

All six areas snap to exactly {0°, 180°}: four within **0.04°** of 0, two within **0.004°** of π, with **no in-between angle**. This both falsifies the naive "rotation ≈ 0" (Eltibule and KurMountains are dead-on 180°) and confirms the discrete-orientation model. The engine's "enumerate the axis-aligned orientation states" bootstrap is justified by real data — and it must **not** assume θ = 0. Handedness (`mirrorNorth`) was constant in this sample; the orientation variation surfaced as rotation 0/π (an equivalent parameterization of the same discrete-4-state group).

**Subtlety the texture-deviation probe surfaced:** the 0/π split is in the **world→texture-pixel** mapping (where landmark icons are *placed*), not in the texture image itself. The base texture art aligns to the in-game screenshot at **0° for every area** (deviation NCC picks 0° for both Serbule and the 180°-calibrated Eltibule). So a detection front-end aligns texture↔screenshot at 0°; the discrete {0, π} only matters later, in the world→pixel solve.

### H2 — isotropy — **indirectly supported**

A 4-parameter *similarity* fits all six areas to **sub-pixel** residual (0.006–0.53 px). If the renderer needed an anisotropic (affine) model, a similarity could not fit that tightly. A direct affine-vs-similarity contest is only meaningful at kept ≥ 4 detected points per area; that becomes available once detection is robust enough on the sparse areas (currently only Serbule clears it).

### H3 — consistent border inset — **supported (n=3); inset clusters in a ~9–12% band**

Three areas now have committed texture-frame solves, so the landmark-bbox-into-texture inset is measurable across them:

| area | scaleRatioX | max-edge inset |
|---|---|---|
| AreaSerbule (0°, dense) | 0.836 | 8.9% |
| AreaEltibule (180°, sparse) | 0.787 | 11.6% |
| AreaKurMountains (180°, sparse) | 0.788 | 11.3% |

The inset is **not a single constant but a tight band (8.9–11.6%)**, and the two independently-solved sparse 180° areas land **near-identical** (11.3 / 11.6%, scaleRatioX 0.787 / 0.788 — within 0.3 pp / 0.001). That consistency is the H3 signal: a cold scale estimate of `texture_dim / world_span × scaleRatioX` with `scaleRatioX ≈ 0.79–0.84` (≈ 9–12% inset prior) is viable as a bootstrap, to be refined by the correspondence solve. The inset is computed from **all** landmarks+NPCs' world coords projected through each committed baseline (`InsetMetrics.Compute`), so it reflects the true landmark extent, not the detected subset.

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

**Eltibule + KurMountains (both 180°): cold solve DEMONSTRATED via the blob front-end (update 2026-05-30).** Whole-image template NCC + RANSAC stays stuck at 3 (often degenerate, near-coincident) inliers on both areas regardless of threshold or `--border-mask` — the named refs are either in an unseparable town cluster or lost under the rocky-rim / terrain false-positive flood (64 medipillar + 64 portal "detections" per template, mostly rim). A 0.7 threshold doesn't help: it lets noise form a wrong-but-self-consistent fit at the wrong orientation (−49.6° on Eltibule).

What *did* work is the pipeline this verdict recommended but hadn't wired end-to-end: **texture-deviation local-NCC → shape/size blob filter → type-aware template NCC *within* each icon blob → RANSAC.** Typing only the ~14–24 blob candidates (instead of the whole noisy screenshot) collapses the false-positive pool, and RANSAC over those clean typed detections recovers both areas cold — **no prior calibration, no manual clicks**:

| area | scale | rotation | origin | refs | residual | refs on-screen |
|---|---|---|---|---|---|---|
| AreaEltibule | 0.763 | 179.98° (π) | (2146.2, −202.5) | 5 | **0.65 px** | 38/38 |
| AreaKurMountains | 0.569 | 180.00° (π) | (2188.8, −141.5) | 8 | **0.73 px** | 32/32 |

Both land on the discrete {0, π} class (matching `refinements.json`), sub-pixel, every ref projecting on-screen. So **H4 is now demonstrated on sparse areas too** — the lever is the deviation-blob detector, precisely as predicted. Tooling: `MapTextureDeviationProbe --blobs … --icons-dir …` emits a typed-detections CSV; `MapCalibrationFromScreenshot --detections-csv` solves from it.

**`--border-mask` (probe side) is load-bearing — measured, both areas:**

| area | mask | icon blobs | RANSAC inliers | residual | rotation | outcome |
|---|---|---|---|---|---|---|
| Eltibule | on | 14 | 5 | 0.65 px | π | ✅ correct |
| Eltibule | off | 21 | 3 | 1.77 px | −0.814 rad | ❌ wrong orientation |
| Kur | on | 24 | 8 | 0.73 px | π | ✅ correct |
| Kur | off | 29 | 3 | 1.23 px | 0.110 rad | ❌ wrong orientation |

Without the mask the rocky rim contributes ~7 extra blobs that **type as icons** (rim rock matches the pin templates above the 0.55 floor); those rim false positives let RANSAC settle on a wrong-but-self-consistent **3-inlier** fit (3 points / 4 DOF fits almost any similarity at low residual) at the wrong orientation. The mask drops them and RANSAC locks onto the correct π fit at 5–8 inliers. The degeneracy guards (100 px span, refit-residual tiebreak) do **not** save the unmasked run on either area. So the mask is necessary for convergence here — *and yet* it simultaneously **over-masks Eltibule's interior**: its edge-connected non-veg/water flood bleeds through the brown interior (new `--mask-debug` viz: 171 dropped / 36 kept), eating legitimate interior icons. Net positive (removing rim FPs matters more than the interior icons lost), but blunt — the engine wants a tighter rim classifier (bounded flood depth or rock-colour) that drops the rim without eating the interior. (Pixel dims for `--map-rect "0,0,W,H"`: Eltibule 921×914, Kur 981×980. Textures 2048×2033 / 2048×2048.)

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
- **Accuracy ceiling is detection + zoom, not the renderer.** The renderer is a clean per-area isotropic similarity with **no** intra-area warp; it fits to sub-pixel once zoom is normalised. ([PR #449](https://github.com/moumantai-gg/mithril/pull/449) is **resolved** — the old "±10% non-affine warp" band was *operational* (live Survey pipeline: player-relative pins, un-readable in-game zoom, hand-correction), not a renderer non-linearity. Affine/homography/poly and piecewise/TPS/RBF were all eliminated; do not reintroduce warp language.) So engine accuracy is bounded by auto-detection precision and zoom handling, not by any map warp.

## Detector re-scoring against the new baselines (2026-05-30)

With Eltibule + Kur baselines committed, the #911 blob shape-filter detector (`--blobs --ground-truth`, gt-tol 20 px) yields real recall/precision instead of #911's eyeballed candidate counts:

| area | icon candidates | recall (separable) | precision | notes |
|---|---|---|---|---|
| AreaEltibule | 14 | 11/38 = **29%** (0 under structure) | 12/14 = **86%** | low recall: many of the 38 GT refs aren't rendered as detectable icons (unshown NPCs) + the over-masked lower interior |
| AreaKurMountains | 24 | 16/24 = **67%** (8 of 32 project onto a rejected structure/fog blob) | 13/24 = **54%** | snow terrain deviates more → noisier candidate set, lower precision |

**Honest caveat:** each baseline was *derived* from this same blob detector (typed → RANSAC over a 5–8 inlier subset), so the inlier refs trivially hit — recall over the remaining ~30/24 refs and precision over the non-inlier candidates are the independent part. The baselines' validity is corroborated independently: sub-pixel residual, exact-π orientation matching `refinements.json`, scaleRatioX consistent with Serbule, and named inliers that are real area entities (Travel to Ilmari Desert, Creepy Door, Meditation Pillars). Precision (esp. Eltibule's 86%) is the more meaningful axis: when the detector flags a candidate it is usually a real ref. Both are lower bounds — GT = *all* landmarks+NPCs, not all of which render.

## Remaining work

1. ~~**Build the texture-deviation → shape-filter → template stage** and re-test on Eltibule/Kur.~~ **Done.** Blob shape/size filter + type-aware template NCC within candidates is wired (`MapTextureDeviationProbe --blobs --icons-dir`) and cold-solves both sparse areas (see H4). The `--border-mask` over-masking on Eltibule (interior bleed-through) is a known refinement for the engine — flood depth or rock-colour classification rather than "edge-connected non-veg/water".
2. **Texture-frame solves for the remaining areas** (MyconianCave, Cave1, Casino — completes H3 across the full reachable set + a real per-area H2 affine contest at kept ≥ 4). Same `--detections-csv` pipeline; commit to the baseline, re-run `measure`, append insets here and on the wiki.

## Teardown

The throwaway study scaffolding (`tools/MapCalibrationStudy`, and `tools/MapTextureDeviationProbe` once its idea is folded into the engine) is deleted in its own PR per #897 Task 10 — separate from this verdict and from any further measurement runs, per the squash-merge-orphans rule.
