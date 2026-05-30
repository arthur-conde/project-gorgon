# Auto-Calibration Gate Study — Design

**Tracked in:** [mithril#897](https://github.com/moumantai-gg/mithril/issues/897)
**Date:** 2026-05-30
**Status:** Approved, building
**Relationship:** Prerequisite gate for a later **auto-calibration "detect + correspond + solve" engine** spec. The engine spec is intentionally **not yet written** — its feasibility (data-only bootstrap, near-zero-ref calibration) rests on a hypothesis validated on only two areas. This study tests that hypothesis on a small, deliberately-varied sample and records a go/no-go verdict that the engine spec will cite.

## 1 — Why this is a gate, not the engine

The wiki finding [Legolas-Calibration-Findings](https://github.com/moumantai-gg/mithril/wiki/Legolas-Calibration-Findings) claims PG's map renderer places icons via a per-area **global isotropic similarity** (scale + rotation + origin + handedness), validated sub-pixel on **two** areas (Eltibule, Casino), with rotation ≈ 0 in both (maps appear north-up; only handedness varies). A blind correspondence-by-projection landed at 0.3 px. If that regularity holds *across areas*, then a brand-new area can be calibrated from **near-zero human input**: compute `scale ≈ texture_dim / world_span`, pick handedness, refine on a few auto-detected icons.

That "if" is load-bearing and under-sampled. Building the full detect→correspond→solve engine as a product on an n=2 hypothesis is premature: if even one common area-class rotates, or the border inset wanders between areas, the data-only bootstrap's core assumptions break and the engine needs a different shape. **So we measure first.** The deliverable here is *numbers + a recorded verdict + one throwaway prototype*, not shipping infrastructure.

This study reuses the substrate already shipped — it adds no product code:

- `src/Mithril.MapCalibration/LandmarkCalibrationSolver.cs` — closed-form 2D-similarity LSQ that already **self-corrects handedness** by solving under both north-axis reflections and keeping the lower-residual fit. Don't reimplement; call it.
- `src/Mithril.MapCalibration/AreaCalibration.cs` — the solved transform (`Scale`, `RotationRadians`, `OriginX/Y`, `MirrorNorth`, `ResidualPixels`).
- `tools/Mithril.MapCalibration.Tools.Common/` — `NccTemplateMatch` (sub-pixel, zoom-invariant icon match), `IconTemplateExtractor` (real pivot-corrected sprite templates), `MapTextureExtractor` (per-area base texture + dims), `LandmarksReader`/`NpcsReader` (world coords), `MapRectLocator`.
- `tools/Mithril.MapCalibration.Harness/` + `tools/MapCalibrationWpf/` — the manual-click calibration surface ([2026-05-30-map-calibration-harness-design.md](2026-05-30-map-calibration-harness-design.md), PRs #884/#890), used to produce the Half-A ground-truth solves.

**Ownership note (charters).** Calibration *production* is shared infra in `Mithril.MapCalibration`, **not** Legolas (see [module-charters.md](../../module-charters.md) — the per-area calibration data + transform + store + session lifted out of Legolas, owner-confirmed 2026-05-28/29). This study is shared-infra-adjacent measurement; it lives in `tools/`, references the shared lib, and touches no module.

## 2 — The hypothesis under test (stated precisely)

For every sampled area, the world→texture-pixel placement of landmark icons is a **single global isotropic similarity**:

```
texturePx = origin + scale · R(θ) · H · worldXZ
```

where `H` is an axis-aligned handedness/reflection (per-area, discrete) and:

- **H1 (axis-aligned orientation):** `θ` is one of a small **discrete** set — empirically `{0, π}` — not an arbitrary angle. (Refined from the wiki's "rotation≈0": the author's local store already shows 4/6 areas at ≈0° but **Eltibule and KurMountains at ≈±180°**, within 0.004° of π — see §3 Data source 0. A π rotation is a discrete flip, not drift; combined with handedness it is exactly the "4 axis-aligned states" the cold bootstrap enumerates. The engine must **not** assume `θ=0`; it must enumerate the discrete orientation set.)
- **H2 (isotropy):** a single uniform `scale` suffices; a full affine fit yields no meaningful residual improvement over the similarity.
- **H3 (inset-consistency):** the landmark world-bbox projects into a sub-rectangle of the texture whose margin, **expressed as a fraction of texture dimension, is ~constant across areas** — i.e. there's a fixed authoring border. This is what makes `scale` computable cold from `texture_dim / world_span`.
- **H4 (cold-correspondence):** a zero-prior solver (scale-from-bbox → try handedness → best icon-fit) recovers the *same* world↔icon correspondence as a careful manual solve, to sub-gate residual.

The discrete orientation state (`H` together with `θ∈{0,π}`) is **expected to vary** between areas — that is not a failure; the solver chooses it per-area from geometry. Note that "rotation 0/π with fixed handedness" and "rotation 0 with varying handedness" are two parameterizations of the same discrete-4-state group (rot-π ≡ reflect-both-axes); the bootstrap enumerates the set regardless of which label the solver reports.

## 3 — Corpus & capture protocol

### Data source 0 — existing persisted calibrations (free, n=6, rotation+handedness only)

The author's local store `%LocalAppData%/Mithril/MapCalibration/refinements.json` already holds **6 solved calibrations** (Serbule, Eltibule, KurMountains, Cave1, Casino, MyconianCave) spanning outdoor / cave / indoor maps, all sub-pixel residual (0.006–0.53 px). These are an **immediate, zero-capture sample** for two sub-hypotheses:

| Area | rotationRad | ≈deg | mirrorNorth | calibrationZoom | residualPx |
|---|---|---|---|---|---|
| AreaCave1 | 5.9e-06 | 0.0003° | false | 1.0 | 0.049 |
| AreaCasino | −1.6e-06 | −0.0001° | false | 0.8 | 0.006 |
| AreaSerbule | 3.3e-04 | 0.019° | false | 0.42 | 0.388 |
| AreaMyconianCave | 5.6e-04 | 0.032° | false | 0.434 | 0.530 |
| AreaEltibule | −3.14153 | −179.996° | false | 1.0 | 0.336 |
| AreaKurMountains | 3.14158 | +179.999° | false | 1.0 | 0.335 |

- **H1 (orientation):** the rotations are sharply **bimodal — {0, π}**, never an in-between angle. This both falsifies the naive "rotation≈0" and confirms the discrete-orientation model (§2 H1).
- **H2 (isotropy):** sub-pixel similarity residual across 6 varied maps is strong support that one uniform scale suffices.

**Frame caveat — this is why source 0 is not sufficient on its own.** These were solved against the **live Legolas overlay at varying `CalibrationZoom`** (0.42 / 0.8 / 1.0 / 0.434…), i.e. world→overlay-pixel, *not* world→texture-pixel. Rotation and handedness are invariant to scale and translation, so they transfer directly. But `scale`, `origin`, and therefore the **inset (H3)** are in overlay-pixel space and are **not** comparable to `texture_dim / world_span` — note the wild `scale` spread (0.24–2.26) is mostly baked-in zoom, not a renderer property. **H3 and H4 still require the texture-frame screenshots below.**

The study's `measure` mode reads source 0 directly to tabulate the rotation/handedness column across all 6 areas for free, then augments with the captured set for the scale/inset/cold-correspondence columns.

### Data source 1 — captured screenshots (for H3 + H4)

**Input:** one full-map screenshot per sampled area.

**Protocol (recorded so the sample can grow):**
1. Enter the area; open the in-game map.
2. **Zoom all the way out** so the entire texture is visible and pan = 0. (This is the `MapRectLocator` / `CalibrationZoom = 1.0` assumption; a panned/zoomed sub-window defeats the v1 map-rect math.)
3. Screenshot at native resolution; name `study/<AreaKey>.png`.

**Sample = the 6 source-0 areas.** These are the maps the author can actually reach, so they are the **whole reachable population**, not a subset of a larger possible sample — and they already span the variety that matters (outdoor: Serbule, Eltibule, KurMountains; cave: Cave1, MyconianCave; indoor: Casino; and crucially both orientation classes, 0° and 180°). Capture a zoomed-out texture-frame screenshot for **as many of the 6 as practical**; H3/H4 need only enough to confirm the inset clusters and that blind correspondence works on at least one area of *each* orientation class (so include at least one of Eltibule/KurMountains, since the π-orientation is the one that most stresses the bootstrap's handedness enumeration). `AreaSerbule` additionally has a committed 0.30 px texture-frame baseline in `map-calibration-baseline.json` as a free anchor.

**Honesty clause (carried into the verdict):** n=6 is **directional evidence, not proof** — but it is the practical ceiling of reachable maps, not a convenience cut, so widening the sample isn't an available lever. The verdict is "proceed / stop / investigate," not a statistical guarantee; the protocol stays repeatable so the table can be appended if more areas become reachable later (e.g. via the future opt-in passive-capture path).

## 4 — Half A: geometric-consistency measurement

For each area:

1. **Ground-truth solve.** Load `study/<AreaKey>.png` in the existing WPF harness; place well-spread refs by manual click against the screenshot's real icons; solve → an `AreaCalibration` (the manual path is tractable at this n and gives a trustworthy reference transform that does *not* depend on the engine we're gating).
2. **Measure** (the console tool's `measure` mode, reading the solved transform + `landmarks.json`/`npcs.json` + texture dims):

| Metric | Definition | Hypothesis |
|---|---|---|
| `rotationDeg` | `AreaCalibration.RotationRadians` in degrees | H1: snaps to `{0°, 180°}`, no in-between |
| `mirrorNorth` | `AreaCalibration.MirrorNorth` | recorded; part of the discrete orientation state |
| `scale` | `AreaCalibration.Scale` (px/world-unit) | — |
| `predictedScale` | `texture_dim / world_span` over the landmark bbox (report X and Z separately) | — |
| `scaleRatio` | `scale / predictedScale` | constant across areas ⇒ inset is constant |
| `insetFrac` | margin between the projected world-bbox and the texture edges, as a fraction of texture dim (per edge) | H3: spread `< ~3%` across areas |
| `affineResidual` vs `similarityResidual` | RMS px of a full affine fit vs the similarity, on the *same* refs | H2: affine win is negligible |

The affine fit (H2) is a study-only computation in the tool (6-param least squares); it exists solely to confirm isotropy empirically rather than assume it. It is **not** added to the shared solver.

## 5 — Half B: cold-correspondence prototype (the decisive test)

A **throwaway** prototype (the console tool's `bootstrap` mode) that proves a *zero-prior* solver corresponds correctly. Per area, using **only** `landmarks.json`/`npcs.json` + the extracted texture dims + the screenshot (no stored calibration, no manual clicks):

1. **Scale-from-bbox.** `scale₀ ≈ texture_dim / world_span` (no inset correction yet — deliberately rough).
2. **Enumerate handedness.** Try the 4 axis-aligned reflection states (±X × ±Z) under the rotation≈0 assumption. For each, project the landmark world-bbox onto the texture corners to get a candidate orientation.
3. **Detect.** NCC the real icon sprites (`IconTemplateExtractor`, pivot-corrected via `centre + (w·(pivot.x−0.5), h·(0.5−pivot.y))`) against the screenshot → detected icon pixels (sub-pixel). Convert screenshot→texture via the harness `CalibrationContext` transform.
4. **Score & pick orientation.** For each candidate orientation, greedily correspond predicted-landmark → nearest-detected-icon and score total match quality; keep the best-scoring handedness.
5. **Refine.** Feed the corresponded `(world ↔ detected texturePx)` pairs to `LandmarkCalibrationSolver.Solve` (it re-confirms handedness from geometry). The handful of detected icons pins down the true inset → refined transform. Apply the **one-axis-outlier guard**: a ref whose residual is large in a single axis only is a detection/transcription error, not a real offset — drop it and re-solve (this study is where that guard meets real clipped/overlapping icons, e.g. the south-edge clipped portal noted in the wiki).
6. **Compare to Half-A ground truth.** Record: did blind correspondence assign the same landmark↔icon pairing? What's the final residual? How far is the refined transform from the manual one (per-parameter)?

The prototype is **informational** — it informs the real engine's algorithm but is not it, and is deleted with the rest of the tool once the verdict lands.

## 6 — The verdict (pass thresholds)

**Status going in:** source 0 (existing n=6) already lends strong support to **H1** and **H2** before any capture. The gate's real remaining risk is **H3 + H4**, which need the texture-frame screenshots.

The hypothesis **holds** (engine spec proceeds with the data-only-bootstrap shape) if, across the sample:

- **H1** — every area's `θ` snaps to a discrete axis-aligned value (`{0, π}`); each is within `~0.1°` of a set member, with no area landing on an in-between angle. (Source 0 already shows clean bimodality; the capture set should not introduce a third cluster.)
- **H2** — affine residual improves on similarity residual by `< ~0.5 px` RMS (no meaningful anisotropy).
- **H3** — `insetFrac` spread across areas `< ~3%` of texture dim; equivalently `scaleRatio` clusters tightly. *(Texture-frame only — source 0 cannot answer this.)*
- **H4** — Half-B blind correspondence matches ground-truth pairing, with refined residual `< 2 px` (well under the 12 px `CalibrationGoodResidualPx` gate) on areas with ≥3 clean detected icons.

The discrete orientation state varying across areas is **expected and passing** — what must hold is that it stays in the small enumerable set.

The hypothesis **fails / needs investigation** if any area rotates materially, the affine clearly beats the similarity, the inset wanders, or blind correspondence mis-pairs. The verdict records the **offending area and which sub-hypothesis broke**, so the engine spec can scope around it (e.g. "dungeons rotate — bootstrap must search rotation, not assume 0").

Thresholds are starting values calibrated against Serbule (`rotation = 7e-5 rad ≈ 0.004°`, residual 0.30 px); the study may report a metric just outside a threshold with a judgement call rather than a mechanical fail.

## 7 — Tooling, deliverables, where it lives & issues

### Tooling

A single **throwaway** console tool, `tools/MapCalibrationStudy/` (isolated, referencing `Mithril.MapCalibration` + `Mithril.MapCalibration.Tools.Common` + `Mithril.MapCalibration.Harness`), two modes:

- `measure` — Half A: read per-area ground-truth `AreaCalibration` + landmarks/NPCs + texture dims → emit the consistency table (CSV + a markdown summary).
- `bootstrap` — Half B: cold solve from zero priors → emit the per-area comparison (pairing match, refined residual, per-parameter delta vs ground truth).

It is committed for reproducibility and **deleted once the verdict is recorded** (a clean delete; per the squash-merge-orphans rule, the add and the delete are separate PRs so neither is gc-eliminated mid-history). Half-A ground-truth solves are produced with the existing WPF harness — no new product code.

### Deliverables & where they live (per [CLAUDE.md "Where does new content go?"](../../../CLAUDE.md))

- **This spec** → `docs/superpowers/specs/` (here). Scratch-tier; the canonical plan is the issue body.
- **The recorded verdict + the per-area table** → the wiki **[Legolas-Calibration-Findings](https://github.com/moumantai-gg/mithril/wiki/Legolas-Calibration-Findings)** page (canonical, stable, already cited by `AreaCalibration` and the charters) — extended from n=2 to the full sample, with the go/no-go conclusion.
- **The gate decision + rationale** → a short `docs/` design-notebook entry (co-evolves with the engine work) capturing *what the verdict implies for the engine spec*. A "Verification owed" marker is removed once the wiki numbers land.
- **The engine spec stays unwritten** until the verdict is recorded.

### Issues

1. **Gate-study issue** (this spec folded into the body, self-contained per the spawned-session-handoff rule): build `tools/MapCalibrationStudy`, capture the corpus, run `measure` + `bootstrap`, record the verdict to the wiki + notebook. On a **pass**, its closing comment is the trigger to write the engine spec; on a **fail**, it records the broken sub-hypothesis.
2. _(Not filed now)_ the **engine spec** — written only after issue 1's verdict, citing it.

## 8 — Relationship to other work

- **Gates** the future auto-calibration engine (detect→correspond→solve→outlier-guard, two consumers: offline baseline builder + live overlay self-cal retiring `CalibrationZoom`, plus an opt-in passive/crowdsourced capture path). None of those are specced until this verdict lands.
- **Builds on** the harness ([2026-05-30-map-calibration-harness-design.md](2026-05-30-map-calibration-harness-design.md), #884/#890) and the common-lib detectors; the cold-bootstrap prototype is effectively a future `ICalibrationMethod` rehearsed in throwaway form.
- **Shared infra, not a module** — lives in `tools/`, references `Mithril.MapCalibration`; per the charters, calibration production is shared infra owned by no module (lifted out of Legolas).
- **Inherits the ±10% non-affine ceiling** (`legolas_calibration_findings`, [PR #449](https://github.com/moumantai-gg/mithril/pull/449)): even if H1–H4 pass, the residual map warp means "approximate location" UX is the honest ceiling. The study's per-area residuals quantify it for the sample. *(Correction 2026-05-30: this premise was **disproven** by the study itself — the ±10% band was operational (live Survey pipeline), not a renderer warp; H2 confirmed sub-pixel similarity fits with no anisotropy. The honest ceiling is detection precision + zoom handling. See `docs/map-calibration-gate-verdict.md`.)*

---

— drafted by Claude (Opus 4.8), posted by @arthur-conde
