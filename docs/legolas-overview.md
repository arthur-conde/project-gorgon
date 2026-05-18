# Legolas — architectural overview

A bootstrap-context doc for future contributors (human or LLM) starting work on Legolas. Covers Survey mode end-to-end. Motherlode is out of scope here — see [`MotherlodeFlowController`](../src/Legolas.Module/Flow/MotherlodeFlowController.cs) and [`MotherlodeViewModel`](../src/Legolas.Module/ViewModels/MotherlodeViewModel.cs) for that side.

Companion docs:
- [`docs/agent-plans/legolas-state-machine.md`](agent-plans/legolas-state-machine.md) — the refactor plan that produced the current FSM. Read for *why* the FSM exists.
- [`docs/agent-plans/legolas-wizard.md`](agent-plans/legolas-wizard.md) — UI direction (post-FSM).

## What Legolas does

Project Gorgon's survey/treasure items (gated on **Geology**, **Mining**, or **Treasure Cartography** — "Surveying" is loose shorthand here) go in the player's inventory. Using one prints a chat line like `[Status] The Iron Vein is 50m east and 30m north`. The item is consumed (and grants XP) only when used while *standing on* the target spot.

> **As-built — [#454](https://github.com/moumantai-gg/mithril/issues/454) landed.** Survey/treasure placement is now **absolute**: Legolas consumes `IPlayerLogStream` and keys off Player.log `LocalPlayer: ProcessMapFx((X,Y,Z), …)`, which carries the target's exact world coordinate. The relative-offset model, the anchor click, `CoordinateProjector.Refit`, the `AwaitingPosition`/`Ready` FSM states, and the `Gathering` survey-drop are **retired for Survey**. `SessionState.PlayerPosition` / `CoordinateProjector` survive but are **Motherlode-only** now (its triangulation records the player position from a map click). Sections below that still describe the relative/anchor model are flagged inline; the FSM and coordinate sections are updated. Cold-start calibration moved to a wizard `Calibrating` step driven by the map overlay — see [#460](https://github.com/moumantai-gg/mithril/issues/460).
>
> **As-built — [#476](https://github.com/moumantai-gg/mithril/issues/476) landed.** The anchor click was *also* (undesigned) the player-position reference for Survey: the "you are here" marker, the route start, and the initial "head here next" segment. #460 collateral-removed all three. #476 restores them from a **better source** — the GameState `IPlayerPositionTracker` world coordinate projected through the area calibration (`AreaCalibration.ProjectWorld`, the same transform `ProcessMapFx` pins use), **not** a manual click. Placement stays absolute; the relative/anchor model does not return. The signal is sparse (zone-in / teleport only — there is no per-tick movement feed in Player.log), so it is freshest at Optimize time and goes stale as the player walks; `MeasuredAt`/`Source` are surfaced (`MapOverlayViewModel.PlayerAnchorStatus`, shown in the overlay header) and it is never drawn as live. **Option C also landed**: a *manual override* — the optional `SurveyFlowState.SettingPosition` detour ("Set my position" in the wizard → click the map) corrects a stale anchor. The auto GPS is still the zero-click default; a manual pixel wins until the next *fresh* tracker fix (zone-in / teleport) supersedes it.

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

`[Status]` lines are **relative offsets** from the player's *current* position at the moment the survey item was used. There is no absolute coordinate available anywhere *in the chat log* — Legolas can only know "the player thinks these N pins are at these offsets *from wherever they were standing each time*". (Superseded — Player.log `ProcessMapFx` does carry the absolute target coordinate per use; see the premise correction above and [#454](https://github.com/moumantai-gg/mithril/issues/454).)

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

[`SurveyFlowController`](../src/Legolas.Module/Flow/SurveyFlowController.cs). **#454 collapsed this to three states** — `ProcessMapFx` targets are absolute, so there is no anchor click and no `AwaitingPosition`/`Ready` bootstrap. **#476 added one optional state**, `SettingPosition`, for the manual position-override detour (Option C). Initial state is `Listening`.

| State | Meaning |
|---|---|
| `Listening` | Default working state. Absolute pins auto-place as `ProcessMapFx` arrives. The first pin of a cycle stamps `SessionState.StartedAt`. |
| `Gathering` | Route optimised; user is walking it. New targets are **accepted** (the old position-anchor "drop new surveys" constraint is retired with the relative model). |
| `Done` | All surveys collected. If `AutoResetWhenAllCollected`, `Reset()` immediately follows. |
| `SettingPosition` | **#476, optional.** The manual player-GPS override is active: the overlay awaits a "click where you are" and the wizard shows a Cancel affordance. Entered on demand from `Listening`/`Gathering`; returns to whichever it came from (`ReturnState`). The auto tracker GPS is the zero-click default — this state only exists to *correct a stale anchor*, so users who never invoke it never see it. |

### Transitions

| From | Trigger | To | Notes |
|---|---|---|---|
| `Listening` | `Surveys.Add` (first pin, count 0→1) | `Listening` | No transition — **stamps `SessionState.StartedAt`** (the canonical "session started" time). Re-stamps every cycle since `Reset` returns to `Listening`. |
| `Listening` | `OptimizeRoute()` | `Gathering` | Caller has assigned `RouteOrder`s. `CanOptimize` = `Listening && Surveys.Count > 0` (the non-empty precondition is now explicit, not structural). |
| `Listening` \| `Gathering` | `AllCollected` event (auto) | `Done` | Fires when the last uncollected survey is marked. |
| `Listening` \| `Gathering` | `RequestSetPosition()` | `SettingPosition` | #476. Parks the current state in `ReturnState`. No-op from any other state. The auto anchor is untouched until the click confirms. |
| `SettingPosition` | `ConfirmPosition()` (map click) | `ReturnState` | #476. `MapOverlayViewModel` writes the manual pixel to the session *before* this call (`SurveyPlayerIsManual = true`); the FSM only owns the phase. |
| `SettingPosition` | `CancelSetPosition()` | `ReturnState` | #476. Returns with the anchor unchanged. |
| `Done` | `Reset()` (auto, if `AutoResetWhenAllCollected`) | `Listening` | Clears `Surveys` + `StartedAt`. |
| any | `Reset()` | `Listening` | Always — there is no anchor precondition. Clears `Surveys` + `StartedAt` (also exits `SettingPosition`). |

There is no longer a `NoteSurveyDetected`/`CanAcceptSurvey`/`DescribeWhyDropped` drop path: absolute pins are added straight to `SessionState.Surveys` and the controller reacts via `OnSurveysChanged`. The cold-start "uncalibrated area" case is handled in the wizard (a `Calibrating` gate step — see #460), not the FSM. The `SettingPosition` detour is *not* surfaced as its own `WizardStep` — `RecomputeStep` maps it back to its `ReturnState`'s step so the wizard panel stays put while the overlay collects the click.

## Pin calibration: the guided two-phase walkthrough & the label-agnostic reconciliation

> **Read this before "fixing" the calibration UI.** The label-agnostic rule and the named-pin prompt look contradictory and are not.

> **As-built — [#477](https://github.com/moumantai-gg/mithril/issues/477) Part A landed.** The in-flow (#460) calibration is now a **guided, two-phase, correctable** walkthrough driven by `PinCalibrationCoordinator`. The earlier "existing-pins route vs. freshly-dropped turn-order route" branch and the per-pin turn-order queue are **retired** — there is one model with two explicit phases. The paragraphs below describe the current code; the standalone `CalibrationSessionViewModel` (landmark window) still uses its own turn-order queue and is unchanged.

Cold-start calibration pairs *(world coordinate ↔ overlay pixel)* points and feeds them to `LandmarkCalibrationSolver` via `IAreaCalibrationService.CalibrateCurrentArea`. The world coordinates come from the player's map pins.

**Pin source (#468).** The pin set is owned by the GameState-tier `IPlayerPinTracker` (`Mithril.GameState.Pins`), *not* Legolas. It parses `ProcessMapPin{Add,Remove}` (the only two verbs PG has — a rename/move is Remove+Add; there is no clear/edit verb), is **area-scoped** (keyed off the shared `PlayerAreaTracker`, swapped on area change), and **owns the login/area-entry replay**: PG bulk-re-emits every pin as an `Add` burst on each entry, which the tracker folds into an idempotent upsert keyed by rounded coordinate. Consumers just subscribe. Legolas's `PlayerLogParser` keeps only `ProcessMapFx` (survey targets are Legolas-owned, not shared pins).

### The two phases

A transparent overlay's click-through is **all-or-nothing** — it either passes a right-click to the game *or* captures the user's left-click, never both. So calibration is two **explicit, user-toggled** phases (`CalibrationPhase`), never an automatic FSM edge. The phase trigger lives on the **wizard panel** (a normal, always-clickable window) plus an optional unbound `IHotkeyCommand` — *not* on the transparent overlay.

- **Drop.** Overlay click-through ON. The user right-clicks the in-game map to place ≥3 well-spread pins (or relies on ones already there); Legolas only *observes* the live count (`PinsAvailable`). Entry starts here only when <3 usable pins exist.
- **Pair.** Overlay captures clicks. The coordinator names **one pin at a time** (`SuggestedPin`) by its in-game identity (`MapPin.DisplayName` + `Appearance`, e.g. *"red dot — 'Fire Magic 25'"*), chosen for **spread** (farthest-point from already-paired). The user left-clicks that pin's game-rendered dot through the overlay. **Advance is implicit** — pairing the next named pin *is* the advance; there is no per-pin confirm key. A pin can be **skipped** (deferred) or **overridden** (`OverridePin` — pick any pin). Entry starts here when ≥3 usable pins already exist (the common case).

**Correction.** Placed pairs are `CalibrationMarker` VMs (not bare points), so a marker can be selected (`TrySelectMarkerAt`), dragged (`DragSelectedTo`), or arrow-nudged (`NudgeSelected`) — the default nudge target is the just-placed marker. Correction edits **only the pixel half**; the world coord is tracker-supplied and never mutated.

**Marker appearance ([#478](https://github.com/moumantai-gg/mithril/issues/478) landed).** The in-flow markers are styleable, **per-family** (one style for all in-flow markers, not per-marker), via `LegolasSettings.CalibrationPinStyle` — a `LegolasPinStyle` whose `Outer` is the selection ring (drawn only while a marker `IsSelected`) and `Center` the always-on dot. The overlay DataTemplate (`MapOverlayView.xaml`) renders it through the **same converter/brush infra as the survey pins** (`PinShapeToGeometryConverter`, the stroke converters, the `LegolasBrushes` forwarder's `Calibration*` brushes), so live settings edits repaint without a restart; the zero-size-Canvas + `-Size/2` offset (`NegativeHalfConverter`) keeps each shape pinned to the click. `CalibrationDefaults()` reproduces the pre-#478 hardcoded look, so the **v3 → v4** schema bump is a visual no-op. The standalone `CalibrationOverlayView` window's markers were explicitly out of scope and are unchanged.

**Live, non-persisting residual.** Once ≥3 pairs exist, every add/nudge/drag re-runs the pure `LandmarkCalibrationSolver` *in-process* (`PreviewResidual`) — no persist, no `IAreaCalibrationService.Changed`. Only the terminal **Confirm** / **Finish anyway** calls the persisting `CalibrateCurrentArea`. Confirm is gated on `≥3 pairs && residual ≤ LegolasSettings.CalibrationGoodResidualPx` (12 px default); "Finish anyway" persists despite a high residual so the user is never trapped at the non-affine ±10% map ceiling.

**Gesture/phase table.** Right-click = in-game pin drop (Drop; observed, never captured). Left-click on overlay = pair the named pin / grab a marker to drag (Pair; overlay captures). Arrow keys = nudge the selected/just-placed marker. Wizard-panel button (+ optional hotkey) = phase toggle / terminal Confirm. The view drives the overlay's click-through from the phase (`IsCalibrationDropping` ⇒ ON, `IsCalibrationCapturing` ⇒ OFF), overriding the user's `ClickThroughMap` preference for the duration.

**Why this does not violate #454's "never pair by name" rule.** The rule's intent is *no automatic name→point pairing*. It holds because the **solve is purely `(WorldCoord ↔ PixelPoint)`** — name/colour/shape never reach `LandmarkCalibrationSolver`; identity is used **only to help the human** decide which service-supplied world point they are deliberately clicking. The pairing is always a human click against a named target, never an inferred name→point map.

### In-flow recalibration (#477 Part B)

`IAreaCalibrationService.ClearCurrentAreaCalibration()` removes + persists the deletion and fires `Changed`. The Listening step offers a **"Recalibrate this area"** affordance (only when `CanRecalibrate` — i.e. the area is already calibrated) behind a **confirm guard** (`IsConfirmingRecalibrate`) so a misclick can't wipe a good calibration. Confirming clears the calibration → `Changed` → `RecomputeStep` routes back into `WizardStep.Calibrating` via the *same pin route as cold start* (`OnCurrentStepChanged` re-arms `PinCalibration`), so Part A's guided correctable flow applies on the redo. No new top-level FSM state — the existing edges are reused. The standalone window's Recalibrate (landmark route) is left as the alternative.

## SessionState

[`SessionState`](../src/Legolas.Module/ViewModels/SessionState.cs) is the shared observable model. The fields that matter for Survey:

| Field | Lifetime | Notes |
|---|---|---|
| `Surveys : ObservableCollection<SurveyItemViewModel>` | Session | Cleared by `Reset()`. Fires `AllCollected` event when last uncollected is marked. |
| `PlayerPosition : PixelPoint` | Session | **Motherlode-only** (#454). The manual map-click position its triangulation records. Mutating it re-fires `RebuildRouteGeometry` + wedges. Survey never reads it. |
| `HasPlayerPosition : bool` | Session | True once `SetPlayerPosition` runs (Motherlode click). |
| `SurveyPlayerPixel : PixelPoint?` | Session | **Survey-only** (#476). The `IPlayerPositionTracker` world fix projected through the current area's calibration. Null until a fix lands in a calibrated area. Route start + rendered marker + pre-first-collection segment. Set by `MapOverlayViewModel`; distinct from `PlayerPosition` so the two modes can't cross-contaminate. |
| `SurveyPlayerMeasuredAt : DateTimeOffset?`, `SurveyPlayerSource : PlayerPositionSource?` | Session | Staleness of `SurveyPlayerPixel`. Surfaced via `MapOverlayViewModel.PlayerAnchorStatus` — never drawn as live. (`Source` is null for a manual override.) |
| `SurveyPlayerIsManual : bool` | Session | **#476 Option C.** True when `SurveyPlayerPixel` came from the user's "set my position" click (the `SettingPosition` detour), not the tracker projection. A manual pixel is calibration-independent, survives a calibration re-apply, and is superseded by the next *fresh* tracker fix. `PlayerAnchorStatus` shows `"You — set manually"`. |
| `SelectedSurvey : SurveyItemViewModel?` | UI | Drives nudge-command targeting. Auto-set to the most-recently-placed survey by `LogIngestionService` so arrow-key adjustments target the new pin. |
| `IsMapVisible`, `IsInventoryVisible : bool` | UI | Overlay visibility intent — `OverlayController` reacts. `IsInventoryVisible` is set true by `NoteSurveyDetected` so the user sees which slot to pick next. |
| `MapOpacity`, `InventoryOpacity : double` | **Persisted** | Bidirectionally synced with `LegolasSettings` in [`LegolasModule.Register`](../src/Legolas.Module/LegolasModule.cs). |
| `Mode : SessionMode` | Session | `Survey \| Motherlode`. |

### `IsAnchorEditable` (issue #120)

> **Retired by [#454](https://github.com/moumantai-gg/mithril/issues/454).** There is no editable Survey anchor any more — `IsAnchorEditable` no longer exists and the player marker is non-interactive. [#476](https://github.com/moumantai-gg/mithril/issues/476) reinstated a *non-editable* Survey player marker sourced from `IPlayerPositionTracker` (see the `SurveyPlayerPixel` row above and the #476 banner near the top), not a click the user can drag. The paragraphs below describe the pre-#454 model and are kept only as history.

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

### Pin-nudge target precedence (issues #119, #120; #477 A/C)

`NudgePinCommandBase.ExecuteAsync` and the on-screen nudge pad both call the **single** `MapOverlayViewModel.Nudge(dx, dy, step)` so the keyboard and pad can't diverge. Precedence:

1. A selected **calibration marker** (#477A — the guided walkthrough's just-placed/selected marker) → `PinCalibrationCoordinator.NudgeSelected`.
2. The selected `SessionState.SelectedSurvey` pin → `CorrectSurveyCommand` (a survey always wins over the manual anchor).
3. The **manual** Survey player anchor (#477C) — only when no survey is selected and `SurveyPlayerIsManual`: mutate `SurveyPlayerPixel` only, keep the manual flag (a fresh tracker fix still supersedes it per #476), never touch the Motherlode `PlayerPosition` or the retired `MoveAnchor`/`IsAnchorEditable` model.
4. Else → no-op.

The auto/tracker-projected anchor is intentionally **non-interactive** — nudging a data-sourced fix would mask staleness. `NudgePinCommandBase.IsRegistrable` and `NudgePadViewModel.IsAvailable` track all of (1)–(3) so the arrow keys aren't eaten system-wide when there's nothing to nudge (#139).

> The pre-#454 `IsAnchorEditable`/`MoveAnchor` fall-through is gone. The #477C manual anchor is *not* that model — it is a raw screen pixel on the shared marker layer, superseded by the next fresh tracker fix.

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

~~This is not a heuristic; it's a hard constraint. Don't relax it without solving the position-tracking problem.~~

> **Superseded — [#454](https://github.com/moumantai-gg/mithril/issues/454).** The position-tracking problem *is* solved by the game itself: every survey/treasure-map use emits Player.log `ProcessMapFx` with the target's absolute world coordinate (verified: `Check Survey` 4/4 in a live log; `Check Map`/Treasure Cartography shares the same item template). Once Legolas reads `ProcessMapFx`, targets are absolute, movement no longer invalidates anything, and the `Gathering` survey-drop mitigation is unnecessary for placement. This paragraph describes the current code only.
>
> **[#476](https://github.com/moumantai-gg/mithril/issues/476) caveat — this only solved *target* placement, not *player* tracking.** The Survey "you are here" marker / route-start (`SurveyPlayerPixel`) comes from `IPlayerPositionTracker`, whose fixes are *also* sparse (`ProcessAddPlayer`/`ProcessNewPosition` = zone-in / teleport only — there is genuinely no per-tick footstep feed in Player.log). The marker is therefore accurate at zone-in and goes stale as the player walks the route — by design, not a bug to "fix". Do not relax the staleness surfacing (`PlayerAnchorStatus` / `MeasuredAt` / `Source`); never present the marker as live. The user's recourse when it *is* stale is the Option C manual override (`SurveyFlowState.SettingPosition`) — that is the designed answer, not making the auto signal pretend to be denser than it is. Target *pins* are unaffected (they're absolute identities).

### Anchor is "manually editable" only before the first survey

> **Retired by [#454](https://github.com/moumantai-gg/mithril/issues/454).** `IsAnchorEditable`, the editable player marker, `Refit`, and the re-anchor commands are gone for Survey (placement is absolute). `IsAnchorEditable` no longer exists; `PlayerPosition` is Motherlode-only. The paragraph below describes the pre-#454 model and is kept only as history.

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

> **Display-scaling correctness depends on `D2DOverlaySurface` hosting the `D3DImage` with `Stretch.Fill`, not `Stretch.None`** ([#481](https://github.com/moumantai-gg/mithril/issues/481)). The back buffer is device-pixel-sized but the `D3DImage` is left at the default 96 DPI; `Stretch.None` would map one back-buffer pixel to one DIP and mis-scale the entire pin layer by the display-scale factor at any scaling ≠ 100% (pins drift off the map proportional to distance from the top-left, bottom-right pins clip). `Fill` composites the `(W·s)×(H·s)` buffer 1:1 onto the `W×H` DIP box — lossless, and the only thing keeping the "1:1 with the game map" invariant true off-100%. Don't switch it back to `None`.

### Second-run `StartedAt` regression — stamp on the first pin in `Listening`

`SessionState.StartedAt` is stamped inside `SurveyFlowController.OnSurveysChanged` the moment the first pin of a cycle lands (count 0→1) while `Listening`. Post-#454 there is no `Ready`/`AwaitingPosition` — but the invariant is unchanged in spirit: `AutoResetWhenAllCollected = true` returns the FSM to `Listening` and clears `StartedAt`, so the next cycle's first pin must re-stamp. Symptom if broken: the share-card render shows `0s elapsed` despite a real multi-minute run. The regression test pinning this is `SecondCycle_after_AutoReset_re_stamps_StartedAt` in `SurveyFlowControllerTests`.

### Renderer is Direct2D in a D3DImage, not WPF retained-mode

The pin / route / wedge / anchor layer is drawn immediate-mode by `PinSceneRenderer` on a Direct2D render target presented through a `D3DImage`. WPF chrome (header, hint banner, nudge pad, resize grips) is still vanilla XAML. The window keeps `AllowsTransparency="True"` — `D3DImage` bypasses the software-rendering penalty for its child surface, and the WPF chrome is small enough that its software rendering cost is invisible.

Implications:
- Hit-testing for pins is via the existing `Viewport_MouseLeftButton*` handlers on the WPF Viewport (the D2D surface is `IsHitTestVisible=False`). Selection comes from the wizard ListBox; no per-pin D2D hit-test exists.
- Tooltips on hover are not reproduced — WPF's `ToolTip="{Binding Name}"` had no D2D equivalent without a custom popup. If users miss it, the right answer is a separate WPF popup driven by mouse position + a virtual hit-test against the latest `PinScene`, not a re-introduction of WPF pins.
- No retained-mode layout for pins means the active-pin "shift" bug (issue tracked at the start of the rewrite) is structurally impossible — D2D draws at exact coordinates with no Grid auto-sizing.
