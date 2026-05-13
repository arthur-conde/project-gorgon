# Legolas — architectural overview

A bootstrap-context doc for future contributors (human or LLM) starting work on Legolas. Covers Survey mode end-to-end. Motherlode is out of scope here — see [`MotherlodeFlowController`](../src/Legolas.Module/Flow/MotherlodeFlowController.cs) and [`MotherlodeViewModel`](../src/Legolas.Module/ViewModels/MotherlodeViewModel.cs) for that side.

Companion docs:
- [`docs/agent-plans/legolas-state-machine.md`](agent-plans/legolas-state-machine.md) — the refactor plan that produced the current FSM. Read for *why* the FSM exists.
- [`docs/agent-plans/legolas-wizard.md`](agent-plans/legolas-wizard.md) — UI direction (post-FSM).

## What Legolas does

Project Gorgon's **Surveying** skill produces survey items that go in the player's inventory. Using one prints a chat line like `[Status] The Iron Vein is 50m east and 30m north`. The item is consumed (and grants XP) only when used while *standing on* the target spot.

Legolas tails the chat log, parses these offsets, and:

1. Asks the user to mark their current position on a map overlay (sets the initial projector anchor).
2. Plots every detected survey at the projected pixel position. The user never has to click to confirm placement.
3. Lets the user drag/nudge mis-projected pins; ≥2 corrections trigger a least-squares refit of all four similarity-transform parameters (origin, scale, rotation). The visible anchor follows the refitted origin.
4. Optimises a route through the unvisited pins.
5. Watches for `… collected!` lines to mark pins done; auto-resets when the last one drops (configurable).

The overlay is a HUD layered over the in-game map — strictly 1:1 with it. Internal zoom/pan was removed in [#126](https://github.com/moumantai-gg/mithril/pull/127).

## End-to-end Survey flow

```
ChatLog tail              [src/Mithril.Shared/Logging — IChatLogStream]
   │ raw lines
   ▼
ChatLogParser            [Services/ChatLogParser.cs]   regex → SurveyDetected | ItemCollected | MotherlodeDistance | UnknownLine
   │
   ▼
LogIngestionService      [Services/LogIngestionService.cs]   dedup, auto-place every survey, surface inventory overlay
   │
   ▼
SurveyFlowController     [Flow/SurveyFlowController.cs]    state machine; logs/diagnoses out-of-state arrivals
   │
   ▼
SessionState             [ViewModels/SessionState.cs]   observable shared state (Surveys, SelectedSurvey, PlayerPosition…)
   │                        ▲
   │                        │ user input (drag/nudge to correct)
   │                        │
   ▼                     MapOverlayViewModel             [ViewModels/MapOverlayViewModel.cs]
CoordinateProjector      [Services/CoordinateProjector.cs]   metres → pixels; 4-DOF refit of origin/scale/rotation
   │
   ▼
PinScene + renderer      [Rendering/]                       per-frame snapshot drawn via Direct2D in a D3DImage
   │
   ▼
MapOverlayView (XAML)    [Views/MapOverlayView.xaml]   thin WPF chrome (header, hint banner, nudge pad, resize grips) +
                                                       a single D2DOverlaySurface element for pins/routes/wedges/anchor
```

The user's only inputs to the loop are: clicking the map *once* in `AwaitingPosition` (sets the initial anchor), dragging/nudging existing pins to correct them, and pressing **Optimize Route**. Survey placement is automatic — there is no per-survey click-to-confirm gesture.

## Coordinate model

### The raw data

`[Status]` lines are **relative offsets** from the player's *current* position at the moment the survey item was used. There is no absolute coordinate available anywhere in the chat log — Legolas can only know "the player thinks these N pins are at these offsets *from wherever they were standing each time*".

The parser is at [`ChatLogParser.cs`](../src/Legolas.Module/Services/ChatLogParser.cs#L9-L11):

```csharp
[GeneratedRegex(@"\[Status\] The (?<name>.+?) is (?<a>\d+)m (?<aDir>north|south|east|west) and (?<b>\d+)m (?<bDir>north|south|east|west)\b", …)]
```

Output is a [`MetreOffset(East, North)`](../src/Legolas.Module/Domain/MetreOffset.cs) where `East`/`North` are signed (W = -East, S = -North).

### The projector

[`CoordinateProjector`](../src/Legolas.Module/Services/CoordinateProjector.cs) carries three pieces of state:

| Field | Meaning |
|---|---|
| `_origin : PixelPoint` | Screen pixel that represents "the player's anchor". Set by `SetOrigin` (map click), seeded by `CalibrateFromClick` for tests, or refitted from corrections by `Refit`. |
| `_scale : double` | Pixels per metre. |
| `_rotation : double` | Radians. Map-north relative to overlay-up, measured clockwise. |

Three operations:

- **`Project(MetreOffset) → PixelPoint`** — applies rotation, then scale, then offsets from origin. Y is inverted because WPF's screen Y grows downward but map north grows upward:
  ```csharp
  rotE = E·cos(θ) + N·sin(θ)
  rotN = -E·sin(θ) + N·cos(θ)
  pixel = (origin.X + scale·rotE,  origin.Y - scale·rotN)
  ```
- **`SetOrigin(PixelPoint)`** — moves the anchor without touching scale/rotation. Called by the "Set Player Position" map-click flow as the initial anchor.
- **`Refit(IReadOnlyList<(MetreOffset, PixelPoint)>)`** — closed-form 2D similarity LSQ. Solves for **all four parameters** (origin X, origin Y, scale, rotation) simultaneously, expressed via 2D-complex arithmetic for compactness:
  ```
  z_i = east_i − j·north_i           (− to flip world-N to screen-up)
  w_i = px_i + j·py_i
  c   = Σ (w_i − w̄)·conj(z_i − z̄) / Σ |z_i − z̄|²
  scale    = |c|
  rotation = arg(c)
  origin   = w̄ − c·z̄
  ```
  Equivalent to the Umeyama 1991 algorithm specialised to 2D with uniform scale.
  - **Threshold:** silently no-ops unless ≥2 *non-degenerate* corrections (centred metre vectors with non-zero magnitude). Coincident-points case no-ops cleanly instead of dividing by ~0.
  - **Why 4-DOF instead of 2-DOF:** the user's "Set Player Position" click is rarely pixel-perfect, so any residual anchor bias used to be absorbed into scale/rotation as a permanent residual error — worst near the anchor, basically invisible far away. Solving for the origin as well removes this bias. After Refit, `MapOverlayViewModel` propagates `_projector.Origin` back to `Session.PlayerPosition` so the visible anchor follows the projector's belief.

### Bearing convention

The map uses `atan2(East, North)` (not `atan2(N, E)`) for the offset bearing, paired with `atan2(dx, -dy)` for the pixel bearing — both are measured **clockwise from "up"** (map-north and screen-negative-Y, respectively). This matches in-game compass usage.

## SurveyFlowController FSM

[`SurveyFlowController`](../src/Legolas.Module/Flow/SurveyFlowController.cs). Five states:

| State | Meaning |
|---|---|
| `AwaitingPosition` | No anchor set yet. Map clicks become the anchor. |
| `Ready` | Anchor set, no surveys placed yet. Accepts the first survey but doesn't expose the route-optimise control. The state where `IsAnchorEditable` is true. |
| `Listening` | Anchor set; surveys auto-place as they arrive. The default mid-run working state. |
| `Gathering` | Route optimised; user is walking it. **New `SurveyDetected` events are dropped** (position-anchor constraint). |
| `Done` | All surveys collected. If `AutoResetWhenAllCollected`, `Reset()` immediately follows. |

`Ready` exists so every Listening session has a fresh `StartedAt` stamp — the timestamp is bound to the Ready→Listening edge (first-survey arrival), which fires on every cycle, instead of the once-per-app-session AwaitingPosition→Listening edge that the previous design used. See "Second-run StartedAt regression" in pitfalls below.

### Transitions

| From | Trigger | To | Notes |
|---|---|---|---|
| `AwaitingPosition` | `ConfirmPlayerPosition()` | `Ready` | After caller has updated `PlayerPosition` and the projector origin. |
| `Ready` | `Surveys.Add` (first pin lands) | `Listening` | Driven by `OnSurveysChanged` in the controller. **Stamps `SessionState.StartedAt`** at this moment — that's the canonical "session started" time. |
| `Ready` \| `Listening` | `NoteSurveyDetected(sd)` | (same) | No transition. Surfaces inventory overlay; `LogIngestionService` has already auto-placed the pin (and the Ready→Listening edge above is what closes the loop). |
| `Listening` | `OptimizeRoute()` | `Gathering` | Caller has assigned `RouteOrder`s. The "non-empty surveys" precondition is structural: Listening state implies `Surveys.Count > 0`. |
| `Listening` \| `Gathering` | `AllCollected` event (auto) | `Done` | Fires when last uncollected survey is marked. |
| `Done` | `Reset()` (auto, if setting on) | `Ready` \| `AwaitingPosition` | Routes through `Reset` — see below. |
| any | `RequestSetPlayerPosition()` | `AwaitingPosition` | Re-anchor. **Preserves `Surveys`** — their offsets are still valid; only the projector origin will move. |
| any | `Reset()` | `Ready` if `HasPlayerPosition`, else `AwaitingPosition` | Clears `Surveys` (and `StartedAt`). **Does not reset the projector** — caller is responsible. |

### Dropped-survey diagnostics

`NoteSurveyDetected` checks `CanAcceptSurvey` (true in `Ready` and `Listening`). When dropping (called from `AwaitingPosition`, `Gathering`, or `Done`), `SessionState.LastLogEvent` is set to a human-readable reason — surfaced in the panel's status strip. Reason map: see `DescribeWhyDropped` in the controller.

## SessionState

[`SessionState`](../src/Legolas.Module/ViewModels/SessionState.cs) is the shared observable model. The fields that matter for Survey:

| Field | Lifetime | Notes |
|---|---|---|
| `Surveys : ObservableCollection<SurveyItemViewModel>` | Session | Cleared by `Reset()`. Fires `AllCollected` event when last uncollected is marked. |
| `PlayerPosition : PixelPoint` | Session | The projector anchor in pixel space. Initially set by the user's click; updated to follow `_projector.Origin` after every Refit. Mutating this re-fires `RebuildRouteGeometry` + wedges. |
| `HasPlayerPosition : bool` | Session | True once `SetPlayerPosition` runs. |
| `IsAnchorEditable : bool` | Derived | `HasPlayerPosition && Surveys.Count == 0`. See below. |
| `SelectedSurvey : SurveyItemViewModel?` | UI | Drives nudge-command targeting. Auto-set to the most-recently-placed survey by `LogIngestionService` so arrow-key adjustments target the new pin. |
| `IsMapVisible`, `IsInventoryVisible : bool` | UI | Overlay visibility intent — `OverlayController` reacts. `IsInventoryVisible` is set true by `NoteSurveyDetected` so the user sees which slot to pick next. |
| `MapOpacity`, `InventoryOpacity : double` | **Persisted** | Bidirectionally synced with `LegolasSettings` in [`LegolasModule.Register`](../src/Legolas.Module/LegolasModule.cs). |
| `Mode : SessionMode` | Session | `Survey \| Motherlode`. |

### `IsAnchorEditable` (issue #120)

The anchor is editable — i.e. draggable via the player thumb, nudgeable via hotkeys — **only** while:

```csharp
HasPlayerPosition && Surveys.Count == 0
```

This is the window between "user clicked the map" and "first survey landed". It exists because the original behaviour (no editing post-click) made fat-finger anchor placement painful — the only fix was a full Reset. After the first survey arrives, the anchor is load-bearing for projection and re-locks automatically (the `Surveys` collection-changed handler notifies `IsAnchorEditable` change).

The drag handler in [`MapOverlayView.xaml.cs`](../src/Legolas.Module/Views/MapOverlayView.xaml.cs) checks editability both at drag-start and at drag-completion — a survey that lands mid-drag silently cancels the move.

## Settings & persistence

[`LegolasSettings`](../src/Legolas.Module/Domain/LegolasSettings.cs) is global (no per-character override). Property highlights:

| Property | Default | Notes |
|---|---|---|
| `SurveyDedupRadiusMetres` | 5.0 | New `SurveyDetected` whose offset is within this radius of an uncollected pin updates that pin instead of creating a new one. |
| `SurveyPinRadiusMetres` | 8.0 | Pin diameter, in **screen pixels** (not metres — multiplying by projector scale made pins visibly resize on every refit, which felt buggy). |
| `MapOpacity`, `InventoryOpacity` | 1.0 | Floored at `MinInteractiveOpacity = 0.01` so a faded overlay stays clickable. See [#124](https://github.com/moumantai-gg/mithril/pull/124). |
| `ClickThroughMap`, `ClickThroughInventory` | false | Toggles `WS_EX_TRANSPARENT` on the window. |
| `AutoClickThroughInventoryDuringSession` | true | Issue #4 bullet 4 — auto-engage click-through on inventory while `Listening`. |
| `AutoHideOverlaysOnGameUnfocused` | true | Issue #116. |
| `GameProcessName` | "ProjectGorgon" | Substring filter for the focus-detection check. |
| `AutoResetWhenAllCollected` | true | After `Done`, controller calls `Reset()`. |
| `ShowBearingWedges` | true | Render uncertainty arcs for uncorrected pins. |
| `NudgeStepDefault \| Fast \| Fine` | 1.0, 5.0, 0.25 | Pixel magnitudes for the three nudge variants. Read fresh on every command execute, so changes apply immediately. |
| `MapOverlay`, `InventoryOverlay : WindowLayout` | — | Position + size. Bound via `WindowLayoutBinder` in each window's ctor. |

JSON persistence uses an AOT source-generated context: [`LegolasSettingsJsonContext`](../src/Legolas.Module/Domain/LegolasSettingsJsonContext.cs). Property naming is camelCase, output is pretty-printed.

Auto-save is wired through [`SettingsAutoSaver<LegolasSettings>`](../src/Mithril.Shared/Settings/SettingsAutoSaver.cs), registered by `AddMithrilSettings<LegolasSettings>()` in `LegolasModule.Register`. The saver subscribes to `PropertyChanged`, debounces, and flushes synchronously on shutdown. `WindowLayoutBinder.Bind(window, layout, saver.Touch)` calls `Touch()` on drag/resize — those mutations don't fire `PropertyChanged` on `LegolasSettings` itself (the layout object is a sibling), so `Touch` is the explicit dirty signal.

## Hotkeys & click-through

24 `IHotkeyCommand` implementations, all defined in [`Hotkeys/Commands.cs`](../src/Legolas.Module/Hotkeys/Commands.cs) and registered in `LegolasModule.Register`:

- 3 session: `StartSession`, `MarkCurrentCollected`, `SetPlayerPosition`.
- 2 mode: `SetSurveyMode`, `SetMotherlodeMode`.
- 7 overlay: `ToggleMapOverlay`, `ToggleInventoryOverlay`, `ToggleAllOverlays`, `ToggleBearingWedges`, `ToggleMapClickThrough`, `ToggleInventoryClickThrough`, `OptimizeRoute`.
- 12 pin-nudge: `NudgePin{Up,Down,Left,Right}{,Fast,Fine}` — three step magnitudes × four directions.

None ship with a default key binding. Arrow keys would collide with in-game movement, so the user opts into specific bindings via `Settings → Hotkeys`.

### Pin-nudge fallback to anchor (issues #119, #120)

`NudgePinCommandBase.ExecuteAsync` ([Commands.cs:206](../src/Legolas.Module/Hotkeys/Commands.cs)):

1. If `SessionState.SelectedSurvey` is non-null and has a pixel position → nudge it via `MapOverlayViewModel.CorrectSurveyCommand`.
2. Else if `SessionState.IsAnchorEditable` → nudge the anchor via `MapOverlayViewModel.MoveAnchor`.
3. Else → no-op.

This means the same arrow keys that nudge a selected pin will fall through to the anchor when no pin is selected and the anchor is still editable — the "fine-tune what I just clicked" workflow.

### Click-through

[`Controls/ClickThrough.cs`](../src/Legolas.Module/Controls/ClickThrough.cs) wraps `GetWindowLong`/`SetWindowLong` to flip `WS_EX_TRANSPARENT | WS_EX_LAYERED` on a window. Applied on window load and re-applied whenever `LegolasSettings.ClickThroughMap` / `ClickThroughInventory` changes. `ForceTopmost` is also called on activate, so a click-through overlay can't fall behind the game.

[`Services/AutoOverlayCoordinator.cs`](../src/Legolas.Module/Services/AutoOverlayCoordinator.cs) auto-engages `ClickThroughInventory` while the FSM is in a survey-active state, so the inventory overlay stops eating clicks meant for the game.

## Diagnostics

A frame-time logger plus a synthetic load harness ship with the module so future renderer / FSM changes can be measured against the same fixture.

| Component | Purpose |
|---|---|
| [`Diagnostics/FrameTimeLogger.cs`](../src/Legolas.Module/Diagnostics/FrameTimeLogger.cs) | Hooks `CompositionTarget.Rendering`, samples wall-clock dt, writes a CSV (one row per frame) + a `.txt` summary (mean / p50 / p95 / p99 / max / stutter count > 33 ms) plus a config snapshot per run. Singleton; `Start` and `Stop` are reentrant. |
| [`Diagnostics/SurveyPerfHarness.cs`](../src/Legolas.Module/Diagnostics/SurveyPerfHarness.cs) | Drives a synthetic load through the live overlay: resets the session, anchors at the map centre, injects deterministic surveys, captures Listening (with `SelectedSurvey` set so the active treatment runs) for 15 s, then `OptimizeRoute` → Gathering for 15 s. `RunTreatmentSweepAsync` iterates Halo → Glow back-to-back so a single press produces matched A/B reports. |

Three hotkey commands under **Legolas · Diagnostics** drive this; all are gated under `ShellSettings.DeveloperMode` via [`IHotkeyCommand.IsDeveloperOnly`](../src/Mithril.Shared/Hotkeys/IHotkeyCommand.cs) and have no default key bindings. With Developer Mode off they simply don't appear in Settings → Hotkeys.

| Command | What it does |
|---|---|
| Toggle Frame-Time Logger | Manual start/stop. Use during a real session to capture what the user feels. Stop writes a report. |
| Run Survey Perf Harness | Single sweep with the current treatment (~31 s). |
| Run Perf Harness — Treatment Sweep (Halo+Glow) | Both treatments back-to-back (~65 s, four reports). |

Pin count for both harness commands is read from `LegolasSettings.PerfHarnessPinCount` (default 30, clamped 1–1000) — adjust under Settings → Legolas → Diagnostics so a single command covers the 30-pin median and 100-pin tail-of-distribution cases.

Reports land in `%LocalAppData%/Mithril/Legolas/perf/`. Every CSV/`.txt` pair includes the active config (treatment, pin count, transparency mode, FSM state, window size) so two runs can be compared without remembering which knobs were on.

### Acceptance criteria — perf floor

The D2D rewrite was scoped against this 100-pin-with-game baseline. Future renderer changes shouldn't fall below it without explicit discussion:

| Metric | Floor |
|---|---|
| `fps_mean` | ≥ 110 |
| `dt_ms_p99` | < 18 ms |
| `stutter_>33ms` per 15 s phase | < 1 |

Pre-rewrite WPF baseline was **67–84 fps mean / p99 27–30 ms / 2–4 stutters** for the same fixture; today the renderer comfortably clears the floor on all four (Halo / Glow) × (Listening / Gathering) cells.

## Key files

| File | What's in it |
|---|---|
| [`LegolasModule.cs`](../src/Legolas.Module/LegolasModule.cs) | DI registration, hosted services, hotkey command list. Module entry point. |
| [`Flow/SurveyFlowController.cs`](../src/Legolas.Module/Flow/SurveyFlowController.cs) | Survey FSM. |
| [`Flow/MotherlodeFlowController.cs`](../src/Legolas.Module/Flow/MotherlodeFlowController.cs) | Motherlode-mode FSM (out of scope here). |
| [`Services/ChatLogParser.cs`](../src/Legolas.Module/Services/ChatLogParser.cs) | Regex parsing of `[Status]` survey, collect, and motherlode lines. |
| [`Services/LogIngestionService.cs`](../src/Legolas.Module/Services/LogIngestionService.cs) | `BackgroundService` consuming `IChatLogStream`. Dedup, auto-place, FSM dispatch. |
| [`Services/CoordinateProjector.cs`](../src/Legolas.Module/Services/CoordinateProjector.cs) | The metre→pixel projection. |
| [`Services/AutoOverlayCoordinator.cs`](../src/Legolas.Module/Services/AutoOverlayCoordinator.cs) | Auto click-through during sessions. |
| [`Services/OverlayController.cs`](../src/Legolas.Module/Services/OverlayController.cs) | Window lifecycle: open/close, focus-loss hide. |
| [`ViewModels/SessionState.cs`](../src/Legolas.Module/ViewModels/SessionState.cs) | Shared observable state. |
| [`ViewModels/MapOverlayViewModel.cs`](../src/Legolas.Module/ViewModels/MapOverlayViewModel.cs) | Click handling, pin placement, route + wedge geometry rebuilds. |
| [`ViewModels/SurveyItemViewModel.cs`](../src/Legolas.Module/ViewModels/SurveyItemViewModel.cs) | Per-survey VM wrapper. |
| [`ViewModels/ControlPanelViewModel.cs`](../src/Legolas.Module/ViewModels/ControlPanelViewModel.cs) | Panel-side VM (start/stop, mode switching). |
| [`Views/MapOverlayView.xaml{,.cs}`](../src/Legolas.Module/Views/MapOverlayView.xaml) | Overlay UI: WPF chrome + a single D2D surface element for the rendered pin layer. Hosts drag/click handlers. |
| [`Rendering/D2DOverlaySurface.cs`](../src/Legolas.Module/Rendering/D2DOverlaySurface.cs) | WPF `FrameworkElement` wrapping a `D3DImage`. Drives the render loop via `CompositionTarget.Rendering`, applies per-monitor DPI, fires a `Render` event with the live D2D `RenderTarget`. |
| [`Rendering/D3DDeviceLifecycle.cs`](../src/Legolas.Module/Rendering/D3DDeviceLifecycle.cs) | D3D11 + D3D9Ex device pair, shared-handle texture that bridges them, D2D render target on top. The "shared-surface dance" that lets `D3DImage` (D3D9-only) present GPU pixels written by D2D 1.1 (D3D11-only). |
| [`Rendering/PinScene.cs`](../src/Legolas.Module/Rendering/PinScene.cs) | Immutable per-frame snapshot — pin positions, route points, wedges, active treatment, brush colours, dash offset. |
| [`Rendering/PinSceneRenderer.cs`](../src/Legolas.Module/Rendering/PinSceneRenderer.cs) | Pure draw logic. Routes, active segment (dashed marching ants), bearing wedges, survey pins (outer + centre), active-pin treatments (Halo, Glow, ScaleUp, FillSwap), player anchor. |
| [`Rendering/D2DBrushCache.cs`](../src/Legolas.Module/Rendering/D2DBrushCache.cs) | ARGB-keyed `ID2D1SolidColorBrush` cache so the renderer doesn't allocate per draw call. Reset on render-target rebuild (resize, DPI change). |
| [`Rendering/MarchingAntsClock.cs`](../src/Legolas.Module/Rendering/MarchingAntsClock.cs) | Stopwatch-based dash-offset advancer. Replaces the WPF `Storyboard` that used to invalidate continuously. |
| [`Diagnostics/FrameTimeLogger.cs`](../src/Legolas.Module/Diagnostics/FrameTimeLogger.cs) | Per-frame dt sampler + CSV/summary writer. Driven by the manual hotkey or the harness. |
| [`Diagnostics/SurveyPerfHarness.cs`](../src/Legolas.Module/Diagnostics/SurveyPerfHarness.cs) | Synthetic-load + treatment-sweep driver. See "Diagnostics" section above. |
| [`Hotkeys/Commands.cs`](../src/Legolas.Module/Hotkeys/Commands.cs) | All hotkey commands (24 user-facing + 3 developer-only diagnostics). |
| [`Controls/ClickThrough.cs`](../src/Legolas.Module/Controls/ClickThrough.cs) | `WS_EX_TRANSPARENT` P/Invoke helpers. |
| [`Domain/LegolasSettings.cs`](../src/Legolas.Module/Domain/LegolasSettings.cs) | Persisted settings. |
| [`Domain/GameEvent.cs`](../src/Legolas.Module/Domain/GameEvent.cs) | `SurveyDetected`, `ItemCollected`, `MotherlodeDistance`, `UnknownLine`. |
| [`Domain/{PixelPoint,MetreOffset,Survey}.cs`](../src/Legolas.Module/Domain/) | Coordinate value types + survey record. |

## Constraints & pitfalls

A working list of "things that have bitten contributors". Read these before changing projection / FSM logic.

### Offsets are relative; movement is undetectable

The chat log emits no movement events. If the player walks (or falls / teleports) and surveys again, the new offsets are interpreted relative to the *original* anchor — pins land in the wrong place and the user has no obvious indicator that anything is wrong.

The FSM's `Gathering` state is the explicit mitigation: once the route is optimised and the player is walking, **any new `SurveyDetected` is dropped**, with the diagnostic `"route in progress; reset to start a new session"` shown in `LastLogEvent`. The user must `Reset()` and re-anchor to start a fresh batch.

This is not a heuristic; it's a hard constraint. Don't relax it without solving the position-tracking problem.

### Anchor is "manually editable" only before the first survey

`IsAnchorEditable` flips to false the instant `Surveys.Count` goes 0→1. From that point on, manual drag/nudge of the player marker is disabled — but **the projector's origin still moves** automatically as Refit runs. The visible anchor follows `_projector.Origin` after every refit.

If you need to re-anchor *manually* mid-session, the user has to `Reset()` (loses all pins) or trigger `RequestSetPlayerPosition` (keeps pins; the next `ConfirmPlayerPosition` overrides the projector origin to wherever the user clicked).

### Refit threshold is ≥2 *non-degenerate* corrections

`Refit` skips corrections whose centred metre vector has near-zero magnitude (Σ |z'|² < 1e-9). So two coincident pins, or all pins at exactly the same offset, won't refit. The effective minimum for a refit to actually run is two corrections at meaningfully different metre offsets.

### Placement is automatic; correction requires a drag

After the auto-place rework, surveys are placed at the projected pixel position the moment they arrive — the user never has to click to confirm. The only correction gesture is **dragging** (or arrow-key nudging) an existing pin. A drag sets `ManualOverride`; that's the sole signal that a pin's pixel position is user-vouched-for.

This is a deliberate split: in the old flow, every forced click became a "correction" the projector trusted, even when the user had no idea where the in-game ping really was and was just clicking to satisfy the FSM. The wrong-click-becomes-bad-calibration data flowed into Refit and made everything worse. With the split, only deliberate user input drives Refit.

### Coordinate-system inversion

WPF screen Y grows downward; map north grows upward. `Project` negates the rotated north component before adding to `origin.Y`. Bearings are `atan2(East, North)` for offsets and `atan2(dx, -dy)` for pixels — both clockwise from "up". If you find yourself writing `atan2(N, E)` or `atan2(dy, dx)`, you're producing math-textbook bearings, not screen/map bearings, and rotation will silently come out wrong.

### Pin radius is in pixels, not metres

`SurveyPinRadiusMetres` is misnamed — it's read as a pixel value. An earlier version multiplied by `_projector.Scale`, which made pins visibly resize on every refit. The fix kept the property name. Don't "fix" the units without the user-visible consequences.

### Overlay is strictly 1:1 with the game map

No internal zoom/pan ([#126](https://github.com/moumantai-gg/mithril/pull/127)). The window size and position are user-controlled (header drag + edge resize via `WindowLayoutBinder`); the D2D canvas inside renders at exactly 1 DIP per CSS pixel, and the D3D11 back buffer is sized in device pixels for per-monitor DPI correctness. If you find yourself adding a `RenderTransform` or scaling factor to anything inside `Viewport`, stop and reconsider — the entire model assumes canvas pixel == screen pixel == game-map pixel.

### Second-run `StartedAt` regression — keep the stamp on the Ready→Listening edge

`SessionState.StartedAt` is stamped inside `SurveyFlowController.OnSurveysChanged` at the moment the first pin lands while in `Ready`. Don't move it to `ConfirmPlayerPosition` (or any AwaitingPosition→x edge) — the original design did that, and `AutoResetWhenAllCollected = true` (the default) returns the FSM to a non-AwaitingPosition state, so the second run's first-survey edge had nothing to re-trigger the stamp. Symptom: the share-card render shows `0s elapsed` despite a real multi-minute run, with `StartedAt` and `CompletedAt` differing only by the gap between two consecutive `_clock.GetUtcNow()` calls in `LegolasReportService.BuildPayload`. The regression test pinning this is `SecondCycle_after_AutoReset_re_stamps_StartedAt` in `SurveyFlowControllerTests`.

### Renderer is Direct2D in a D3DImage, not WPF retained-mode

The pin / route / wedge / anchor layer is drawn immediate-mode by `PinSceneRenderer` on a Direct2D render target presented through a `D3DImage`. WPF chrome (header, hint banner, nudge pad, resize grips) is still vanilla XAML. The window keeps `AllowsTransparency="True"` — `D3DImage` bypasses the software-rendering penalty for its child surface, and the WPF chrome is small enough that its software rendering cost is invisible.

Implications:
- Hit-testing for pins is via the existing `Viewport_MouseLeftButton*` handlers on the WPF Viewport (the D2D surface is `IsHitTestVisible=False`). Selection comes from the wizard ListBox; no per-pin D2D hit-test exists.
- Tooltips on hover are not reproduced — WPF's `ToolTip="{Binding Name}"` had no D2D equivalent without a custom popup. If users miss it, the right answer is a separate WPF popup driven by mouse position + a virtual hit-test against the latest `PinScene`, not a re-introduction of WPF pins.
- No retained-mode layout for pins means the active-pin "shift" bug (issue tracked at the start of the rewrite) is structurally impossible — D2D draws at exact coordinates with no Grid auto-sizing.
