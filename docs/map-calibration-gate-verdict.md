# Auto-Calibration Gate Study — Verdict

**Date:** 2026-05-30
**Issue:** [mithril#897](https://github.com/moumantai-gg/mithril/issues/897)
**Spec:** [docs/superpowers/specs/2026-05-30-map-calibration-gate-study-design.md](superpowers/specs/2026-05-30-map-calibration-gate-study-design.md)
**Verdict:** **PROCEED** to the auto-calibration engine spec. The renderer is regular enough (H1 decisive); H2/H3 are partially confirmed with a clear, cheap path to complete; H4 (automation) is feasible but requires robust correspondence, not the naive prototype.

This records the result of the gate study so the engine spec can cite it. It also updates the wiki [Legolas-Calibration-Findings](https://github.com/moumantai-gg/mithril/wiki/Legolas-Calibration-Findings) (n=2 → n=6).

## Sample

Six areas — the author's full reachable set — spanning outdoor (Serbule, Eltibule, KurMountains), cave (Cave1, MyconianCave), and indoor (Casino). Rotation/handedness from the user-refinement store (`refinements.json`, frame-invariant); Serbule additionally has a committed texture-frame baseline solve.

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

All six areas snap to exactly {0°, 180°}: four within **0.04°** of 0, two within **0.004°** of π, with **no in-between angle**. This both falsifies the naive "rotation ≈ 0" (Eltibule and KurMountains are dead-on 180°) and confirms the discrete-orientation model. The engine's "enumerate the 4 axis-aligned orientation states" bootstrap is justified by real data — and the engine must **not** assume θ = 0. Handedness (`mirrorNorth`) was constant in this sample; the orientation variation surfaced as rotation 0/π (an equivalent parameterization of the same discrete-4-state group).

### H2 — isotropy — **indirectly supported**

The tool's honest H2 signal (affine-vs-similarity on independent detected pixels) lives only in `bootstrap`, which did not produce trustworthy fits on real data (see H4); `measure`-mode affine is `n/a` by construction. **Indirect support:** a 4-parameter *similarity* fits all six areas to **sub-pixel** residual (0.006–0.53 px) across outdoor/cave/indoor. If the renderer needed an anisotropic (affine) model, a similarity could not fit that tightly. Not a direct affine contest, but strong circumstantial evidence the renderer is isotropic. A direct H2 measurement is available via the completion path below.

### H3 — consistent border inset — **single data point; completion path identified**

Serbule's landmark bbox projects into the texture with an **8.9%** max-edge inset (scaleRatioX 0.836). Consistency across areas can't be assessed from n=1; the other five need a texture-frame solve. **This is the main piece of remaining evidence.**

### H4 — cold zero-prior correspondence — **feasible, but needs robust correspondence**

The study's `ColdBootstrap` (zero-prior: scale-from-bbox → enumerate 4 orientations → greedy nearest-neighbour correspondence → similarity solve → outlier guard, selected by global reprojection) **could not recover the transform on real screenshots.** Root causes, observed end-to-end on Serbule/Eltibule:

1. **Map localization** — PG's in-game map renders with restyling/fog/a UI frame, so whole-texture NCC can't locate it (peaks ~0.23 < 0.30). Worked around with a `--map-rect` override (the screenshot is full-extent, so the frame-cropped image maps 1:1 to the texture).
2. **Icon scale** — PG ships 256 px icon art but renders icons at ~16 px; needed a render-size sweep + `--icon-render-size` override.
3. **Detection noise** — at a usable threshold the icon templates produce many false positives on PG's textured terrain; `landmark_npc` (the bulk of real anchors on Serbule) didn't detect at all at its native aspect, while sparse types (medipillar/portal) produced mostly-false detections.
4. **Type-blind greedy correspondence** — `ColdBootstrap` matches *all* world points against *all* detections without type awareness, so it can't disambiguate ~30 NPCs from a noise cloud. Result: degenerate / wrong-orientation fits (`paired = false`), even after a scale-sanity guard stopped the worst collapses.

**This does not mean automation is infeasible — it means the naive prototype is insufficient.** The shipped [`tools/MapCalibrationFromScreenshot`](../tools/MapCalibrationFromScreenshot) (PR #854) already auto-detects and solves robustly using exactly the machinery the study prototype simplified away: **type-aware assignment** (match each icon type only to its own landmarks, trim to the real count), **RANSAC** (largest consistent inlier set, not greedy NN), a **scale plausibility check**, and per-icon `--icon-size` overrides for aspect quirks (the source comment flags `landmark_npc` on Serbule specifically). Its wiki-recorded result is sub-pixel on Serbule. So **H4 is feasible — the engine's correspondence stage must be RANSAC + type-aware, not greedy.** That is the concrete design lesson the gate produced.

## Verdict & implications for the engine spec

**PROCEED.** The renderer is regular: per-area global isotropic similarity with a **discrete axis-aligned orientation ∈ {0, π}** (H1 decisive, n=6), sub-pixel-fittable everywhere (H2 indirect), with a measured border inset on the one fully-solved area (H3 partial). Near-zero-ref calibration is viable **provided** the correspondence stage uses robust machinery. Concrete carry-overs for the engine spec:

- **Orientation:** enumerate the discrete 4-state set; do not assume θ = 0.
- **Correspondence:** RANSAC + type-aware assignment (per-type, trimmed to landmark count) + a scale plausibility guard. The naive greedy approach is documented here as insufficient.
- **Map localization & icon scale:** the engine needs the `MapRectLocator` fallback (frame-aware / user-assisted rect) and the render-size sweep + per-icon aspect override — none are optional on real screenshots.
- **Honesty ceiling:** the ±10% non-affine map warp ([PR #449](https://github.com/moumantai-gg/mithril/pull/449)) still applies — "approximate location" UX, not pixel-perfect rendezvous.

## Remaining work (to fully close H2/H3)

The cheap, robust way to get a texture-frame solve for the other five areas — completing H3 (inset consistency) and giving a direct H2 affine contest per area — is to run the **proven** `tools/MapCalibrationFromScreenshot` on the captured screenshots (it has the RANSAC/type-aware detection the study prototype lacks), commit those `AreaCalibration`s to the baseline, and re-run `measure`. This is preferable to manual harness clicking and to porting RANSAC into the throwaway study tool.

Once the five inset values are in, append them to the table here and on the wiki, and confirm the inset clusters (H3) before the engine's data-only-bootstrap scale estimate relies on it.

## Teardown

The throwaway `tools/MapCalibrationStudy` has served its purpose (it produced this verdict and the H4 design lesson). Delete it in its own PR per #897 Task 10 once the five inset values are gathered (it may be re-run once more for those, via the proven tool feeding its baseline).
