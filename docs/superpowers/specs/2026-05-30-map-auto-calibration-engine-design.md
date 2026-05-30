# Map Auto-Calibration Engine — Hands-Free Live Self-Calibration — Design

**Date:** 2026-05-30
**Status:** Draft (architecture converged; detection+solve **demonstrated end-to-end** — cold sub-pixel solves on dense *and* sparse areas, [PR #913](https://github.com/moumantai-gg/mithril/pull/913), merged; remaining work is porting the proven pipeline into the engine, see §8)
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
- **Pixel-exact rendezvous as a *guarantee*.** The renderer itself is exact (clean per-area isotropic similarity, **no** intra-area warp — the old ±10% "non-affine" band was *operational*, from the live Survey pipeline, and was disproven; see [PR #449](https://github.com/moumantai-gg/mithril/pull/449), resolved). Real-world accuracy is instead bounded by **auto-detection precision + zoom handling**, which this engine optimises but does not promise to pixel-perfect — "approximate location" UX remains the honest framing for that reason, not because of any renderer warp.

## 3. Why this is now possible

The gate study established the load-bearing facts:

- **H1:** per-area orientation is discrete ∈ {0, π}; the renderer is a per-area global isotropic similarity (sub-pixel fittable).
- **H4:** cold, zero-prior calibration is demonstrated on a landmark-dense area (Serbule: 0.93 px, 23 RANSAC inliers) with the proven `tools/MapCalibrationFromScreenshot` (RANSAC + type-aware assignment).
- The base CDN texture and the in-game map are the **same artwork** (local-NCC 0.86), which both powers the texture-deviation detector and means the map is **findable inside a looser capture** by texture registration.

The remaining gap was detection robustness on **sparse** zones — now closed: the texture-deviation → shape-filter front-end produces 14 clean icon candidates on Eltibule (vs ~3 stable RANSAC inliers from raw template NCC), demonstrated and merged ([PR #911](https://github.com/moumantai-gg/mithril/pull/911), §8). What remains is engineering integration, not a feasibility question.

## 4. Pipeline (end to end)

1. **Frame once.** The user positions the Legolas overlay over the in-game map panel (already a hard requirement — there is no live player position, so the overlay must sit on the map to guide). Its position + size persist.
2. **Trigger.** Player enters a zone, opens the map; calibration fires via the explicit "capture & calibrate" hotkey **or** auto-attempt (both require a map bbox to exist — see §10). The current area is known **from the log** (Arda/GameState), which selects the base texture + landmark/NPC reference set.
3. **Capture.** Blank the overlay for one frame, capture the persisted overlay rect from the OS framebuffer, restore the overlay.
4. **Locate.** Texture-registration (multi-scale NCC of the base texture against the captured frame) finds the map's true sub-rect inside the framed region — absorbing eyeball slop and per-zone letterboxing.
5. **Detect.** `ICalibrationDetector` produces icon candidates (v1 target: texture-deviation local-NCC → shape/size filter; fallback: direct template NCC).
6. **Solve.** Type-aware RANSAC over (candidate, reference) pairs, enumerate the discrete {0, π} orientation, feed to `LandmarkCalibrationSolver.Solve` → `AreaCalibration`.
7. **Validate & persist.** Gate on residual + inlier count; persist via `IMapCalibrationService` with `CalibrationSource.AutoCapture` only if confident, else stay uncalibrated and surface status. Overlay begins guiding.

## 5. Components & interfaces

Each is independently testable; the only one whose internals are unsettled is the detector.

| Seam | Responsibility | v1 implementation |
|---|---|---|
| `IGameWindowLocator` | Resolve the PG window handle + client rect (and focus state). | Reuse the existing game-process detection behind `ForegroundFocusGate` / `ControlPanel.GameProcessName` (the same setting that gates OS hotkeys). |
| `IMapCaptureRegionProvider` | Produce the desktop pixel rect to capture from the persisted overlay framing. | Framing persisted directly (PG restores its map window to the same place each session — see §7); validated at capture time. |
| `IScreenCapture` | Capture a desktop rect to a bitmap. | `BitBlt`/`Graphics.CopyFromScreen` for windowed PG (see §6 for the under-overlay handling). `Windows.Graphics.Capture` is the per-window upgrade path. |
| `IMapRegionRefiner` | Locate the map's true sub-rect inside the captured frame. | Texture-registration (multi-scale NCC vs the base texture). |
| `ICalibrationDetector` | Captured map → **typed** icon detections. | **Demonstrated end-to-end** (§8, PR #913): texture-deviation → shape-filter → type-aware template NCC within each blob → RANSAC. Cold-solves sparse areas sub-pixel. Fallback: whole-image template NCC. |
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

**The capture region and the Legolas overlay rect are the same rect** — there is only one. The overlay exists to guide the user *on the map*, so it must sit exactly over the map window; the map's bounding box and the overlay's bounds are therefore one and the same. There is no separate "capture rect" to keep in sync. The user can set this rect the existing way (move/resize the overlay window directly), and the **"draw map bbox" hotkey** (§10) is an *additional* way to set it — drag a rectangle over the map and the overlay snaps to it. Either path writes the same persisted rect.

This rect is **durable for free**: PG's world-map window is itself a user-movable, user-resizable sub-window whose size and position **PG persists across sessions**, so the map re-opens in the same place and the overlay (drawn over it once) keeps lining up. One bbox covers every area — only the rendered map *within* the window letterboxes per zone (a tall vs wide zone fills the window differently), which §4 step 4 (texture-registration refine) absorbs.

- **Storage:** persist the single overlay/bbox rect (desktop coords are fine, since PG restores the map window to the same place). Re-derive/validate at capture time.
- **The only thing that invalidates it is the user moving or resizing PG's map window** (or an in-game resolution / UI-scale change) without redrawing the bbox. When that happens the captured rect no longer lands on the map, §4's registration fails to lock, and the §9 confidence gate rejects the solve — so it degrades to "couldn't auto-calibrate, redraw the map bbox," never a silent bad calibration. Redrawing is a rare, user-initiated event, so this is a non-issue in practice.

## 8. Detection stage (sparse-zone risk retired; isolated behind an interface)

`ICalibrationDetector` is the one component whose internals were unproven on **sparse** zones at draft time. That risk is now **retired end-to-end** — the full pipeline (through RANSAC) **cold-solves both sparse 180° areas sub-pixel** ([PR #913](https://github.com/moumantai-gg/mithril/pull/913)), building on the shape-filter stage ([PR #911](https://github.com/moumantai-gg/mithril/pull/911)):

1. **Texture-deviation local-NCC** (`tools/MapTextureDeviationProbe`): align base texture to the captured map, sliding-window local NCC; terrain + border cancel (Serbule low-NCC 2.8%, Eltibule 13%), added content (icons + structures + fog) survives as low-NCC blobs.
2. **Shape/size filter** (`--blobs`, PR #911): threshold → optional `--border-mask` (PR #908) → morphological close → 8-connected components → classify each blob **icon / fog / structure** by area + solidity + aspect + peak-deviation. **Size is the primary separator**: icons are compact and icon-sized; fog is large + smooth (low peak deviation); structures are large + high-deviation.
3. **Type-aware template NCC within each candidate blob, then RANSAC** (PR #913): run the four pin templates *inside* each icon blob's bbox, keep the best above a type floor → a small **typed** detection pool; RANSAC over that pool. Restricting template NCC to the ~14–24 blob regions (instead of the whole screenshot) collapses the false-positive flood that starves whole-image correspondence.

**Demonstrated cold solves (PR #913)** — zero priors, no manual clicks, both committed to the baseline:

| area | scale | rotation | residual | inliers | orientation |
|---|---|---|---|---|---|
| AreaSerbule (dense) | 0.8226 | 0° | 0.93 px | 23 | 0° |
| AreaEltibule (sparse) | 0.763 | 179.98° | **0.65 px** | 5 | 180° ✓ |
| AreaKurMountains (sparse) | 0.569 | 180.00° | **0.73 px** | 8 | 180° ✓ |

The sparse 180° areas — which stay stuck at ~3 *degenerate* inliers under whole-image template NCC regardless of threshold/mask — solve cleanly through the blob pipeline, landing dead-on the discrete {0, π} class. **This is the engine's detection+solve front-end, proven across the dense/sparse and 0°/180° splits.** Tooling bridge: `MapTextureDeviationProbe --blobs --icons-dir` emits a typed-detections CSV; `MapCalibrationFromScreenshot --detections-csv` solves from it.

**Two load-bearing lessons (carry into the engine):**
- **Type the blobs, don't just locate them.** Anonymous centroids mis-register — the same clean blob set with the per-type RANSAC constraint *off* lands at the wrong orientation/scale on both areas. The type label is what collapses the assignment space (an npc blob pairs only with npc refs). Detection must *classify*, not merely find.
- **`--border-mask` is necessary but blunt.** Without it, rim rock types as icons and RANSAC locks a wrong-but-self-consistent 3-inlier fit at the wrong orientation; with it, both solve correctly. But the current edge-connected non-veg/water flood **over-masks** Eltibule's brown interior (eats real icons). Engine carry-over: replace the colour flood with an **edge-connected *deviation* flood** (drop the edge-touching deviation component) — it masks the rim ring without crossing the low-deviation interior.

**Known limits:**
- **Co-located icons that render as one visual mass** (Serbule's central keep: 28/46 refs in one footprint = one blob) are a fundamental detector limit; the solve succeeds on the separable minority.
- **Detector recall/precision are scored against baselines derived from the same blobs**, so inlier refs trivially hit — precision (Eltibule 86%) is the more meaningful axis; corroborated independently by sub-pixel residual + exact-π orientation + the H3 inset consistency.

**Fallback** for any zone where the deviation front-end underperforms: direct whole-image template NCC + `--border-mask` (the proven path; robust on dense, weak on sparse). The interface keeps both paths available.

**Open work (engine):** the cold pipeline is proven in the throwaway probe + offline calibrator; the engine must (a) port it behind `ICalibrationDetector` as a single in-process path (no CSV hand-off), and (b) adopt the deviation-flood rim mask above.

## 9. Self-validation gate

Auto-cal must **never silently persist a wrong transform** (the sparse/foggy zones are exactly where it would be tempted). Persist only if:

- residual ≤ the existing good-residual threshold (≈12 px, reuse the constant — do not hard-code a duplicate), AND
- inlier count ≥ a floor (tunable; ≥ the solver's minimum-references with margin).

On failure: keep the area uncalibrated, emit diagnostics, and surface a non-blocking "couldn't auto-calibrate — adjust framing / not zoomed out / sparse zone" status. The manual click path remains as the explicit fallback.

## 10. Trigger & UX

The **map bbox is the prerequisite; once it exists, anything goes.** Two hotkeys plus auto-attempt:

- **Hotkey — "draw map bbox":** drag a rectangle over the in-game map; the overlay snaps to it and the rect persists (§7). This is the one-time setup (and the redraw path if PG's map window ever moves). It's an *additional* way to set the overlay rect — the user can equally just move/resize the overlay directly.
- **Hotkey — "capture & calibrate":** explicit, on-demand solve for the current area. Gated to game focus via the existing gate (`ForegroundFocusGate` / `GameProcessName`). Predictable; the user presses it when the map is open and zoomed out.
- **Auto-attempt:** whenever a bbox exists and a map opens in an uncalibrated (or stale) area with the window focused, attempt a capture+solve in the background and silently upgrade on success. **The bbox's existence is the only gate** — no bbox, no auto-attempt (nothing to capture); bbox present, auto-attempt is free to run. ("It just stays calibrated.")
- All three share one path; the §9 confidence gate makes auto-attempt safe (a bad capture never persists). A gate failure surfaces "couldn't auto-calibrate — redraw the map bbox / zoom out."

## 11. Error handling

| Condition | Behaviour |
|---|---|
| No map bbox set yet | No auto-attempt; prompt to use the "draw map bbox" hotkey (§10). |
| Game window not found / not focused | No capture; status "PG not detected". |
| Map not open / not zoomed out | Refine/registration fails low-confidence → reject, status. |
| Capture failed (occlusion, minimized) | Reject, status; never feed a partial frame to the solver. |
| Low-confidence solve (sparse, fog, drift) | Do not persist; keep prior calibration; status. |
| Captured our own overlay | Prevented by §6; if it slips through, the gate rejects it. |

## 12. Testing

- **Per-seam units:** region refiner on the study screenshots (locates the map sub-rect within a framed capture), confidence gate thresholds, capture-region round-trip (persist → reload → same rect).
- **Replay fixtures:** the gate-study screenshots (`study/screenshots/*` — local, gitignored) run end-to-end detect→solve and assert the recovered `AreaCalibration` matches the committed Serbule baseline within tolerance.
- **Positive control:** self-NCC ≈ 1.0 (screenshot vs itself) in the detector tests — the guard that caught the wrong-filename false negative.
- **Negative controls:** black frame, wrong-area texture, overlay-not-hidden frame → all must be rejected by the gate, none persisted.
- **No live game required** (consistent with `MapCalibrationFromScreenshot`'s offline self-test).

## 13. Anti-cheat

Screen capture reads the **OS framebuffer** (`BitBlt` / `Windows.Graphics.Capture`) — the same data path as the user's manual screenshots. It is **not** process injection, memory reading, or DirectX hooking, so it does not cross PG's anti-injector (ACTk) line, which targets hooks (already foreclosed). One-line confirmation belongs in the implementation PR; this is the green path.

## 14. Dependencies & sequencing

1. ✅ **PR #909** (gate verdict) — merged; this spec cites it.
2. ✅ **Shape-filter stage** ([PR #911](https://github.com/moumantai-gg/mithril/pull/911)) + **cold sparse solves** ([PR #913](https://github.com/moumantai-gg/mithril/pull/913)) — merged; §8 detection+solve proven end-to-end (Eltibule 0.65 px, Kur 0.73 px, both cold). The texture-deviation → blob → type → RANSAC pipeline is the v1 detector.
3. File the **engine GitHub issue** (folding in this spec) and write the implementation plan. The first implementation step is the §8 "open work" — port the proven pipeline behind `ICalibrationDetector` as a single in-process path + adopt the deviation-flood rim mask.

## 15. Open risks

- ~~**Sparse-area detection**~~ — **retired end-to-end** (PR #913): the full pipeline cold-solves Eltibule (0.65 px) and Kur (0.73 px) sub-pixel. Residual: detector scoring is baseline-circular (§8), and the rim mask needs the deviation-flood refinement; neither blocks the engine.
- **Capture-under-overlay flicker** (§6) — hide-for-one-frame may be visible; WGC is the mitigation.
- **User moves/resizes PG's map window** (or changes in-game resolution / UI-scale) invalidates the stored bbox — caught by the confidence gate (low-confidence → no persist → prompt to redraw the map bbox). Rare, user-initiated; see §7.
- **Detection + zoom accuracy** — the real accuracy ceiling; "approximate location" UX, not pixel-perfect. (The renderer itself is exact; the disproven ±10% "non-affine warp" is **not** a factor — see §2.)

## 16. Out of scope / future

- (b) Zero-touch map discovery (capture whole game window, locate map with no framing).
- Live in-game-zoom tracking (full `CalibrationZoom` retirement).
- Community sync of auto-produced calibrations (`CalibrationSource.CommunitySync` already exists as a channel).
- Passive/crowdsourced capture path.
