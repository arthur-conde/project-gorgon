# MapCalibrationStudy — operator runbook

> **Throwaway gate-study tool** ([mithril#897](https://github.com/moumantai-gg/mithril/issues/897)). It measures whether PG's per-area map renderer is regular enough (rotation ∈ {0, π}, isotropy, consistent inset, cold-correspondence) to justify a near-zero-ref auto-calibration engine. It produces **numbers + a go/no-go verdict**, then gets deleted (Task 10). Design rationale: [`docs/superpowers/specs/2026-05-30-map-calibration-gate-study-design.md`](../../docs/superpowers/specs/2026-05-30-map-calibration-gate-study-design.md).

This README is the **Task-9 runbook**: how to collect the inputs and run the two modes.

---

## TL;DR

1. **Capture** one zoomed-out screenshot per area → `study/screenshots/<AreaKey>.png`.
2. **Stage** the extracted base textures (`study/textures/`) and icon templates (`study/icons/`).
3. **Solve** texture-frame ground truth per area in the WPF harness (for `measure`/Half A).
4. **Run** `measure` (Half A) and `bootstrap` (Half B).
5. **Record** the verdict against the H1–H4 thresholds → wiki + `docs/` notebook.

The 6 reachable areas: `AreaSerbule`, `AreaEltibule`, `AreaKurMountains`, `AreaCave1`, `AreaCasino`, `AreaMyconianCave`.

---

## 1. Capture the screenshots  ← the part most people ask about

One screenshot per area. **Get this right or `bootstrap` can't locate the map.**

1. Enter the area in Project Gorgon and open the in-game map (default `M`).
2. **Zoom the map all the way out** so the **entire** map texture is visible at once, and leave it **centered (pan = 0)**.
   - *Why this matters:* the tool's `MapRectLocator` matches the full extracted texture against your screenshot under the v1 assumption that the whole texture fits in view at `CalibrationZoom = 1.0`. A panned or partially-zoomed view is only a sub-window of the texture and defeats the match — you'll get a "could not locate the map rect" error.
3. Screenshot at **native resolution** (no DPI scaling, no downscaling). The landmark / NPC icons must be clearly visible — those are exactly what `bootstrap` detects.
4. Save as `study/screenshots/<AreaKey>.png`, using the **exact area key** (`bootstrap` opens `<--screenshots>/<AreaKey>.png`):
   - `AreaSerbule.png`, `AreaEltibule.png`, `AreaKurMountains.png`, `AreaCave1.png`, `AreaCasino.png`, `AreaMyconianCave.png`.
5. **Include at least one of `AreaEltibule` / `AreaKurMountains`** — these render at the π (180°) orientation and stress the cold bootstrap's handedness enumeration hardest. Your existing calibrations already show 4 areas at 0° and these two at ±180°, so a π-area is the important coverage.

Tips for clean detection:
- Avoid screenshots where icons overlap the map border or each other (the outlier guard tolerates some, but fewer clean icons = weaker fit). The south-edge clipped-portal case in the wiki is the kind of thing to re-shoot if possible.
- A higher-resolution screenshot gives the sub-pixel NCC more to work with.

## 2. Stage textures + icons

Both are extracted from your local PG install (the existing tools already do this — no new work):

- **Base textures** → `study/textures/Map_<AreaKey>.v4.png`. The tool reads cached PNGs via `MapTextureExtractor.EnsureExtractedOrCached` (no live decode). Produce them by running the existing extraction in `tools/MapCalibrationFromScreenshot` / the WPF harness once per area against your install, and copy/point at the resulting `Map_<Area>.v4.png` files.
- **Icon templates** → `study/icons/` (an `index.json` + per-icon PNGs from `IconTemplateExtractor`). The WPF harness produces these on first run; reuse that folder.

You'll also need the reference data the readers consume:
- `landmarks.json` and `npcs.json` (the same files Mithril ships / fetches — any local copy works; pass their paths).

## 3. Ground-truth solves (for `measure` / Half A only)

`measure` reports rotation/handedness from your existing `refinements.json` (free), but its **scale / inset / affine** columns need a **texture-frame** solve per area. Produce those in the WPF harness (`tools/MapCalibrationWpf`): load the area's screenshot, click landmarks, Commit — that writes a texture-frame `AreaCalibration` into `src/Mithril.MapCalibration/BundledData/map-calibration-baseline.json` (or your `--baseline` copy).

> `bootstrap` (Half B) needs **no** manual solve — that's the whole point (zero priors). It only needs the screenshot + texture + icons.

## 4. Run

From the repo root (build/run with **Mithril closed** — the build hook guards stale DLLs; see [#901](https://github.com/moumantai-gg/mithril/issues/901)):

```powershell
# Half A — geometric consistency (rotation/handedness free from refinements; scale/inset/affine from baseline solves)
dotnet run --project tools/MapCalibrationStudy -- measure `
  --refinements "$env:LOCALAPPDATA\Mithril\MapCalibration\refinements.json" `
  --baseline src/Mithril.MapCalibration/BundledData/map-calibration-baseline.json `
  --landmarks <path>\landmarks.json --npcs <path>\npcs.json `
  --textures study/textures `
  --areas AreaSerbule,AreaEltibule,AreaKurMountains,AreaCave1,AreaCasino,AreaMyconianCave `
  --out study/out

# Half B — cold correspondence (zero priors; needs the screenshots)
dotnet run --project tools/MapCalibrationStudy -- bootstrap `
  --screenshots study/screenshots --textures study/textures --icons study/icons `
  --landmarks <path>\landmarks.json --npcs <path>\npcs.json `
  --areas <areas-you-captured> `
  --out study/out
```

Each mode prints a markdown table and writes `study/out/<mode>.{csv,md}`.

## 5. Read the results → verdict

Evaluate against the §6 thresholds in the spec:

| Hypothesis | Column(s) | Pass |
|---|---|---|
| **H1** orientation | `rotationDeg`, `orientationDeg` | every area snaps to {0°, 180°}, none in between |
| **H2** isotropy | `affResid` vs `simResid` (**bootstrap only**) | affine doesn't meaningfully beat similarity |
| **H3** inset | `insetFracMax`, `scaleRatioX` | inset fraction clusters across areas (< ~3%) |
| **H4** cold-correspondence | `bootstrapCorresponded`, `paired`, refined residual | blind pairing matches ground truth, residual < ~2px |

**Two caveats to honour when writing the verdict (from review):**
- **`measure`-mode affine is `n/a` by construction** — it has no independent pixels, so it reprojects through the solved similarity and would read ~0 trivially. The real H2 signal is the **bootstrap** affine column only.
- The bootstrap affine is only meaningful at **≥ 4 kept inliers** (a 6-DOF affine over exactly 3 points is exactly-determined → ~0 regardless of isotropy). Note the kept count when reading it.
- A genuinely **point-symmetric** area is a real 0°/180° ambiguity the cold bootstrap can't resolve (math, not a bug). The reachable corpus isn't symmetric, but flag any area that looks close.

Record the per-area table + the go/no-go conclusion to the wiki [Legolas-Calibration-Findings](https://github.com/moumantai-gg/mithril/wiki/Legolas-Calibration-Findings) page and a short `docs/` notebook. A **pass** is the trigger to write the auto-calibration engine spec; a **fail** records the broken sub-hypothesis + offending area.

## 6. Teardown

Once the verdict is recorded, delete this tool in its own PR (Task 10) — it's intentionally disposable.
