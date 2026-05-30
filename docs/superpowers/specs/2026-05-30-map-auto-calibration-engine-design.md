# Map Auto-Calibration Engine — Hands-Free Live Self-Calibration — Design

**Date:** 2026-05-30
**Status:** Draft (architecture converged; feasibility of the detection stage pending — see Open Risks)
**Tracked in:** to be filed as a GitHub issue once the gate verdict (PR #909) merges; this spec is the brainstorming artifact the issue body will fold in.
**Gated by:** [mithril#897](https://github.com/moumantai-gg/mithril/issues/897) gate study — verdict **PROCEED** ([docs/map-calibration-gate-verdict.md](../../map-calibration-gate-verdict.md), PR #909). This engine is the "live self-cal" consumer that the gate study anticipated.

## 1. Goal

Make per-area map calibration **hands-free**: the user never click-calibrates in Legolas or Gwaihir again. Instead, while playing, Mithril captures the in-game map, detects icons, solves the world→pixel transform automatically, and persists an `AreaCalibration` — "calibrate by playing."

The output is the **same artifact** the manual path produces today (`AreaCalibration`, persisted via `IMapCalibrationService` / the user-refinement store), so every existing consumer (Legolas overlay, Gwaihir) works unchanged. This engine is a new *producer* of that artifact, not a new transform model.

## 2. Non-goals (v1)

- **Zero-touch map discovery** (enumerate the game window, find the map with no overlay framing). That's the "(b)" path — a later version; v1 reuses the overlay rect the user already frames.
- **Live in-game-zoom tracking.** v1 captures at whatever zoom the user is at and records it (`AreaCalibration.CalibrationZoom`); zoom drift is handled by *re-capture* (cheap), not by tracking the live zoom continuously. Retiring `CalibrationZoom` entirely is future work.
- **Zoomed-in / panned map captures.** v1 assumes the in-game map is zoomed all the way out (visible map ≈ full texture extent), matching today's manual workflow.
- **Fullscreen-exclusive capture.** v1 targets windowed / borderless-windowed PG.
- **Sub-±10% accuracy.** PG's non-affine map warp ([PR #449](https://github.com/moumantai-gg/mithril/pull/449)) caps this at "approximate location" UX regardless.

## 3. Why this is now possible

The gate study established the load-bearing facts:

- **H1:** per-area orientation is discrete ∈ {0, π}; the renderer is a per-area global isotropic similarity (sub-pixel fittable).
- **H4:** cold, zero-prior calibration is demonstrated on a landmark-dense area (Serbule: 0.93 px, 23 RANSAC inliers) with the proven `tools/MapCalibrationFromScreenshot` (RANSAC + type-aware assignment).
- The base CDN texture and the in-game map are the **same artwork** (local-NCC 0.86), which both powers the texture-deviation detector and means the map is **findable inside a looser capture** by texture registration.

The remaining gap is detection robustness on **sparse** zones, which is the one open risk below.

## 4. Pipeline (end to end)

1. **Frame once.** The user positions the Legolas overlay over the in-game map panel (already a hard requirement — there is no live player position, so the overlay must sit on the map to guide). Its position + size persist.
2. **Trigger.** Player enters a zone, opens the map, and fires calibration (v1: hotkey/button gated to game focus; endgame: passive auto-attempt). The current area is known **from the log** (Arda/GameState), which selects the base texture + landmark/NPC reference set.
3. **Capture.** Blank the overlay for one frame, capture the (window-anchored) overlay rect from the OS framebuffer, restore the overlay.
4. **Locate.** Texture-registration (multi-scale NCC of the base texture against the captured frame) finds the map's true sub-rect inside the framed region — absorbing eyeball slop and per-zone letterboxing.
5. **Detect.** `ICalibrationDetector` produces icon candidates (v1 target: texture-deviation local-NCC → shape/size filter; fallback: direct template NCC).
6. **Solve.** Type-aware RANSAC over (candidate, reference) pairs, enumerate the discrete {0, π} orientation, feed to `LandmarkCalibrationSolver.Solve` → `AreaCalibration`.
7. **Validate & persist.** Gate on residual + inlier count; persist via `IMapCalibrationService` with `CalibrationSource.AutoCapture` only if confident, else stay uncalibrated and surface status. Overlay begins guiding.

## 5. Components & interfaces

Each is independently testable; the only one whose internals are unsettled is the detector.

| Seam | Responsibility | v1 implementation |
|---|---|---|
| `IGameWindowLocator` | Resolve the PG window handle + client rect (and focus state). | Reuse the existing game-process detection behind `ForegroundFocusGate` / `ControlPanel.GameProcessName` (the same setting that gates OS hotkeys). |
| `IMapCaptureRegionProvider` | Given the persisted overlay framing + live game-window rect, produce the desktop pixel rect to capture. | Overlay framing stored **relative to the game-window client rect** (fractions), re-derived to desktop coords at capture time. |
| `IScreenCapture` | Capture a desktop rect to a bitmap. | `BitBlt`/`Graphics.CopyFromScreen` for windowed PG (see §6 for the under-overlay handling). `Windows.Graphics.Capture` is the per-window upgrade path. |
| `IMapRegionRefiner` | Locate the map's true sub-rect inside the captured frame. | Texture-registration (multi-scale NCC vs the base texture). |
| `ICalibrationDetector` | Captured map → icon candidates (typed where possible). | **Open** (§8): texture-deviation → shape-filter. Fallback: template NCC. |
| (existing) RANSAC + `LandmarkCalibrationSolver` | Candidates + references → `AreaCalibration`. | Reuse `tools/MapCalibrationFromScreenshot`'s correspondence machinery, lifted into a shared service. |
| `ICalibrationConfidenceGate` | Accept/reject a solve. | Residual ≤ existing good-residual threshold (≈12 px) AND inlier floor. |
| (existing) `IMapCalibrationService` / user-refinement store | Persist `AreaCalibration`. | Reuse; add `CalibrationSource.AutoCapture`. |

**Where it lives:** the engine is shared infra in `Mithril.MapCalibration` (calibration *production* is owned by no module per the charters — lifted out of Legolas). The *capture* + *trigger* glue may live in a thin shell/overlay-adjacent service since it touches the overlay window and game-window detection. No module owns it.

## 6. Capturing under the overlay (the input-validation trap)

The overlay sits **on top** of the map, so naively capturing the desktop rect captures *our own overlay drawn over the map*, not the clean map. This is exactly the class of "verify your inputs" bug that cost the gate study two false negatives (DPI bug; wrong filename). Two handlings:

- **v1 — hide for one frame:** set the overlay fully transparent (or nudge it offscreen), capture, restore. Calibration is a one-shot action, so a momentary blank is acceptable.
- **upgrade — per-window capture:** `Windows.Graphics.Capture` on the game window returns the game surface excluding our overlay; no flicker, but pulls in part of the (b) work.

Either way the input is validated before use (non-black, expected size, a self-NCC ≈ 1.0 positive control in tests), and the confidence gate (§9) catches a poisoned frame rather than persisting a wrong transform.

## 7. Region source & window anchoring

The overlay framing is the capture region. Store it **relative to the game-window client rect** (fractional offset + size), not raw desktop coords, and re-derive the desktop rect from the live window rect at capture time. Consequences:

- Move or resize PG → the capture region follows (the map panel is fixed *relative to the game window*). The drift problem of raw desktop coords mostly dissolves.
- **One framing covers every area** — PG's map is a fixed UI panel; only the rendered map *within* it letterboxes per zone, which §4 step 4 (refine) absorbs.
- The only residual re-frame trigger is an in-game resolution / UI-scale change, which the confidence gate catches anyway.

## 8. Detection stage (the open risk, isolated behind an interface)

`ICalibrationDetector` is the one component whose internals are unproven on **sparse** zones. The v1 target, from the gate study's remaining-work item 1:

1. **Texture-deviation local-NCC** (`tools/MapTextureDeviationProbe`): align base texture to the captured map, sliding-window local NCC; terrain + border cancel (Serbule low-NCC 2.8%, Eltibule 13%), added content (icons + structures + fog) survives as low-NCC blobs.
2. **Shape/size filter** (in progress, the spawned task — `--blobs`): connected-components the deviation map; classify each blob icon / fog / structure by area + solidity + aspect + peak-deviation; keep icon-sized compact high-deviation blobs.
3. **Template NCC within candidates only** — type-aware, slashing the search space and false positives on sparse interiors.

**Fallback** if the deviation front-end underperforms on a zone: direct template NCC over the whole map-rect (the proven path; robust on dense areas, weak on sparse), plus `--border-mask` (PR #908). The interface lets the engine ship on dense areas while the sparse-area detector matures.

**Load-bearing dependency:** whether step 2/3 yields enough clean inliers for stable RANSAC on Eltibule/Kur (today ~3) is what the spawned shape-filter task is measuring. The spec's feasibility claim for sparse zones is pending that result.

## 9. Self-validation gate

Auto-cal must **never silently persist a wrong transform** (the sparse/foggy zones are exactly where it would be tempted). Persist only if:

- residual ≤ the existing good-residual threshold (≈12 px, reuse the constant — do not hard-code a duplicate), AND
- inlier count ≥ a floor (tunable; ≥ the solver's minimum-references with margin).

On failure: keep the area uncalibrated, emit diagnostics, and surface a non-blocking "couldn't auto-calibrate — adjust framing / not zoomed out / sparse zone" status. The manual click path remains as the explicit fallback.

## 10. Trigger & UX

- **v1: explicit** — a "Calibrate this area" hotkey/button, gated to game focus via the existing gate. User frames the overlay (once), presses it per new area. Predictable; never fires mid-pan.
- **Endgame: passive** — when a map opens in an uncalibrated area and the window is focused, auto-attempt a capture+solve in the background and silently upgrade on success. ("It just stays calibrated.")
- Re-calibration is the same action; a confidence-gate failure prompts a re-frame.

## 11. Error handling

| Condition | Behaviour |
|---|---|
| Game window not found / not focused | No capture; status "PG not detected". |
| Map not open / not zoomed out | Refine/registration fails low-confidence → reject, status. |
| Capture failed (occlusion, minimized) | Reject, status; never feed a partial frame to the solver. |
| Low-confidence solve (sparse, fog, drift) | Do not persist; keep prior calibration; status. |
| Captured our own overlay | Prevented by §6; if it slips through, the gate rejects it. |

## 12. Testing

- **Per-seam units:** window-anchor coordinate math (window move/resize → correct desktop rect), region refiner on the study screenshots, confidence gate thresholds.
- **Replay fixtures:** the gate-study screenshots (`study/screenshots/*` — local, gitignored) run end-to-end detect→solve and assert the recovered `AreaCalibration` matches the committed Serbule baseline within tolerance.
- **Positive control:** self-NCC ≈ 1.0 (screenshot vs itself) in the detector tests — the guard that caught the wrong-filename false negative.
- **Negative controls:** black frame, wrong-area texture, overlay-not-hidden frame → all must be rejected by the gate, none persisted.
- **No live game required** (consistent with `MapCalibrationFromScreenshot`'s offline self-test).

## 13. Anti-cheat

Screen capture reads the **OS framebuffer** (`BitBlt` / `Windows.Graphics.Capture`) — the same data path as the user's manual screenshots. It is **not** process injection, memory reading, or DirectX hooking, so it does not cross PG's anti-injector (ACTk) line, which targets hooks (already foreclosed). One-line confirmation belongs in the implementation PR; this is the green path.

## 14. Dependencies & sequencing

1. **PR #909** (gate verdict) merges — this spec cites it.
2. **Spawned shape-filter task** reports — settles §8 detection feasibility on sparse zones. If it succeeds, the deviation front-end is the v1 detector; if not, v1 ships dense-area-only behind the interface with sparse-area detection as fast-follow.
3. File the **engine GitHub issue** (folding in this spec) and write the implementation plan.

## 15. Open risks

- **Sparse-area detection** (§8) — the one unproven link; isolated behind `ICalibrationDetector` so the rest can proceed.
- **Capture-under-overlay flicker** (§6) — hide-for-one-frame may be visible; WGC is the mitigation.
- **In-game resolution/UI-scale change** invalidates the window-anchored framing — caught by the confidence gate, requires a re-frame.
- **±10% non-affine warp** — accuracy ceiling; "approximate location" UX, not pixel-perfect.

## 16. Out of scope / future

- (b) Zero-touch map discovery (capture whole game window, locate map with no framing).
- Live in-game-zoom tracking (full `CalibrationZoom` retirement).
- Community sync of auto-produced calibrations (`CalibrationSource.CommunitySync` already exists as a channel).
- Passive/crowdsourced capture path.
