# Auto-Calibration Gate Study — Verdict

**Date:** 2026-05-30
**Issue:** [mithril#897](https://github.com/moumantai-gg/mithril/issues/897)
**Spec:** [docs/superpowers/specs/2026-05-30-map-calibration-gate-study-design.md](superpowers/specs/2026-05-30-map-calibration-gate-study-design.md)
**Verdict:** **PROCEED** to the auto-calibration engine spec. H1 (discrete orientation) is decisive. H4 (cold automation) is **feasible and demonstrated end-to-end** on a landmark-dense area (Serbule: zero-prior, 0.93 px residual, 23 RANSAC inliers). Sparse / irregular-border areas need additional detection work (a built border-mask plus a stronger interior detector), but that is an *engineering* gap, not a falsification of the model.

This records the result of the gate study so the engine spec can cite it. It supersedes the earlier draft of this file, which was written **before the DPI bug below was found** and wrongly concluded that the naive cold prototype could not recover any transform. That conclusion was an artifact of corrupted inputs, not of the renderer. It also updates the wiki [Legolas-Calibration-Findings](https://github.com/moumantai-gg/mithril/wiki/Legolas-Calibration-Findings) (n=2 → n=6).

## Headline lesson — verify your inputs first

For most of the study, **every screenshot run failed** and it looked like cold automation was infeasible: NCC peaks were garbage, RANSAC found no inliers, debug overlays showed nothing matching. The root cause was not the algorithm — it was a **DPI bug** in the shared image loader (`ImageIo.ReadBgra`, fixed in [PR #907](https://github.com/moumantai-gg/mithril/pull/907)):

> `Graphics.DrawImageUnscaled` honors a PNG's DPI metadata. The author's screenshots were cropped at 300 DPI, so the loader drew them at ~1/3 size into the top-left corner of a 96-DPI-sized buffer and left the rest black. Every downstream NCC match, debug image, and inlier count was computed against a mostly-black image.

The tell was a **debug image showing the map shrunk into a black corner** — looking at the actual pixels the algorithm saw is what cracked a problem that had masqueraded as "automation is fundamentally hard" for the whole session. The fix is to draw into an explicit pixel-sized destination rectangle, ignoring DPI metadata. **Carry this into the engine: validate that the loaded screenshot/texture pixels are what you expect (size, non-black, icons visible) before trusting any detection metric.**

## Sample

Six areas — the author's full reachable set — spanning outdoor (Serbule, Eltibule, KurMountains), cave (Cave1, MyconianCave), and indoor (Casino). Rotation/handedness from the user-refinement store (`refinements.json`, frame-invariant). Serbule additionally has a committed texture-frame baseline and a **reproduced cold solve** (see H4).

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

All six areas snap to exactly {0°, 180°}: four within **0.04°** of 0, two within **0.004°** of π, with **no in-between angle**. This both falsifies the naive "rotation ≈ 0" (Eltibule and KurMountains are dead-on 180°) and confirms the discrete-orientation model. The engine's "enumerate the axis-aligned orientation states" bootstrap is justified by real data — and the engine must **not** assume θ = 0. Handedness (`mirrorNorth`) was constant in this sample; the orientation variation surfaced as rotation 0/π (an equivalent parameterization of the same discrete-4-state group).

### H2 — isotropy — **indirectly supported**

A 4-parameter *similarity* fits all six areas to **sub-pixel** residual (0.006–0.53 px) across outdoor/cave/indoor. If the renderer needed an anisotropic (affine) model, a similarity could not fit that tightly. A direct affine-vs-similarity contest (H2 in the strict sense) is only meaningful at kept ≥ 4 detected points per area; it is available per-area once detection is robust enough to produce that many clean inliers (currently only Serbule clears it — see H4 and "Remaining work").

### H3 — consistent border inset — **single data point; completion path identified**

Serbule's landmark bbox projects into the texture with an **8.9%** max-edge inset (scaleRatioX 0.836). Consistency across areas can't be assessed from n=1; the other five need a texture-frame solve, which is gated on the same detection robustness as H4. **This is the main remaining piece of geometric evidence.**

### H4 — cold zero-prior correspondence — **DEMONSTRATED on Serbule; open on sparse areas**

**Serbule: cold auto-calibration confirmed.** With the DPI bug fixed, the proven [`tools/MapCalibrationFromScreenshot`](../tools/MapCalibrationFromScreenshot) (PR #854, RANSAC + type-aware assignment) recovers Serbule's exact committed baseline **from zero priors** — no stored calibration, no manual clicks:

```
dotnet run --project tools/MapCalibrationFromScreenshot -- \
  --screenshot "study/screenshots/AreaSerbule.png" --area AreaSerbule \
  --icons-dir "study/icons" --map-dir "study/textures" \
  --map-rect "0,0,881,920" --icon-render-size 16 \
  --icon-size landmark_npc=17x16 --detection-threshold 0.8 --dry-run
```

→ **scale 0.8226, rotation 0°, residual 0.93 px, 23 RANSAC inliers.** This matches the manual baseline (scale 0.823, 0.30 px) well under the 12 px `CalibrationGoodResidualPx` gate.

Two enablers were decisive, and both are non-obvious:

1. **The DPI fix** (above) — without it, nothing detects.
2. **Include NPCs at the right per-type size.** The tool's own docs *recommended* `--exclude-type Npc` for Serbule. **That advice is wrong** — it came from DPI-corrupted runs where the NPC cloud was pure noise. With the DPI fix and `--icon-size landmark_npc=17x16` (PG renders the `landmark_npc` sprite at a 17×16 aspect, not square), the NPCs become the *bulk of the real anchors*: inliers jump **8 → 23**, and that density is what makes RANSAC robust. The README has been corrected accordingly.

**Eltibule + KurMountains (both 180°): not yet robust.** Both are sparser and have a rocky outer border whose texture matches the icon templates. The new `--border-mask` (PR #908, edge flood-fill of non-vegetation/water) is necessary — on Eltibule it drops **125 rim false positives** and lets the solver reach the correct ~180° orientation — but **not sufficient**: only ~3 interior landmark icons match strongly enough for stable RANSAC. The interior detection is too weak. This is the open problem, and it is a detection-quality problem, not a renderer-regularity problem. (Pixel dims for `--map-rect "0,0,W,H"`: Eltibule 921×914, Kur 981×980.)

**So H4 is feasible — demonstrated where landmarks are dense, with a clear (built + open) path for sparse areas.** The model is sound; the remaining work is making the detector strong enough on sparse interiors. Task 2 of the follow-up (texture-deviation detection) targets exactly this.

## Verdict & implications for the engine spec

**PROCEED.** The renderer is regular: per-area global isotropic similarity with a **discrete axis-aligned orientation ∈ {0, π}** (H1 decisive, n=6), sub-pixel-fittable everywhere (H2 indirect), with a measured border inset on the one fully-solved area (H3 partial). Cold, near-zero-ref calibration is **demonstrated** on a landmark-dense area and is the engine's target shape. Concrete carry-overs:

- **Validate inputs first.** Check loaded pixels (size, not-black, icons present) before trusting any detection metric. The DPI bug cost this study most of a session by looking like an algorithmic failure.
- **Orientation:** enumerate the discrete {0, π} state set; do not assume θ = 0.
- **Correspondence:** RANSAC + type-aware assignment (per-type, trimmed to landmark count), **not** greedy nearest-neighbour.
- **Include NPCs**, with per-type sizing (`landmark_npc` is 17×16, not square). On landmark-dense areas they are the anchors that make the fit robust, not noise to exclude.
- **Border-mask irregular zones.** Rocky/water rims produce icon-template false positives; mask them via the edge flood-fill before correspondence.
- **Detection is the bottleneck on sparse areas.** A stronger interior detector (e.g. texture-deviation / local-NCC against the icon-free base texture) is the next lever; pure template NCC is too weak on sparse interiors.
- **Honesty ceiling:** the ±10% non-affine map warp ([PR #449](https://github.com/moumantai-gg/mithril/pull/449)) still applies — "approximate location" UX, not pixel-perfect rendezvous.

## Remaining work (to fully close H2/H3 and rescue sparse areas)

1. **Stronger interior detection** (gates H3/H4 on sparse areas). Prototype texture-deviation detection: align the icon-free base texture to the screenshot via the map-rect and flag where the screenshot deviates (sliding-window local NCC, not absolute diff). The rocky border vanishes for free (identical in both → high local NCC → excluded); added icons disrupt the local match → low NCC → candidates. If promising, this becomes the engine's detection front-end and may rescue Eltibule/Kur.
2. **Texture-frame solves for the other five areas** (completes H3 inset consistency + gives a real per-area H2 affine contest at kept ≥ 4). Run the proven `tools/MapCalibrationFromScreenshot` once detection is robust enough, commit those `AreaCalibration`s to the baseline, re-run `measure`, and append the inset values here and on the wiki.

## Teardown

The throwaway `tools/MapCalibrationStudy` has served its purpose (it produced the consistency table and surfaced the DPI bug). Delete it in its own PR per #897 Task 10 — separate from this verdict and from any further measurement runs, per the squash-merge-orphans rule.
