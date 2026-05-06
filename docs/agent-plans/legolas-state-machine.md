# Legolas state machine

**Tracked in:** #4

Refactor Legolas's session state into two explicit FSMs (Survey, Motherlode)
plus a thin shell that picks between them. Prerequisite for the modernised
UI (wizard for first-time users, condensed dashboard for experienced
users) and for cleanly addressing the rest of issue #4.

This is the *first* PR on the way to the new UI. The visible user-facing
change after this PR lands is small — the existing panel keeps working —
but every state mutation in the module flows through one controller per
mode, so the wizard/dashboard split becomes a XAML problem instead of a
"rewire five files" problem.

## Why now

Issue #4's last bullet ("maybe a discrete state machine?") is the unlock
for the other four bullets:

- **Splitting Survey/Motherlode** is naturally expressed as two
  independent FSMs, not "toggle Visibility on a GroupBox at the bottom
  of one giant scrollviewer".
- **Consolidating the show-overlay buttons** is a side-effect of
  entering `Listening`, not a button the user has to remember to click.
- **Auto click-through on inventory** is a side-effect of the same
  transition. One place, not three.
- **Wizard UI** is an FSM rendered one step at a time. Without the FSM
  it's a pile of `Visibility` bindings and stale flags.

## Today's state model

`SessionState` carries three independent fields that together describe
"where in the workflow are we":

- [`SessionMode Mode`](../../src/Legolas.Module/ViewModels/SessionState.cs#L64)
  — `Survey | Motherlode`, orthogonal to phase.
- [`SurveyPhase SurveyPhase`](../../src/Legolas.Module/ViewModels/SessionState.cs#L72)
  — `Idle | Surveying | AwaitingPin`. Survey-only; Motherlode ignores it.
- [`bool HasPlayerPosition`](../../src/Legolas.Module/ViewModels/SessionState.cs#L60)
  — checked alongside `SurveyPhase` (e.g. `StartSession` picks Idle vs
  Surveying based on this flag).

Phase mutations happen in **four** places:

| Where | What it does |
|---|---|
| [`ControlPanelViewModel.StartSession`](../../src/Legolas.Module/ViewModels/ControlPanelViewModel.cs#L86) | Clears surveys, sets phase to `Idle` or `Surveying` based on `HasPlayerPosition` |
| [`ControlPanelViewModel.SetPlayerPosition`](../../src/Legolas.Module/ViewModels/ControlPanelViewModel.cs#L96) | Clears `PendingSurvey`, sets phase to `Idle` (so the next map click sets origin) |
| [`MapOverlayViewModel.HandleMapClick`](../../src/Legolas.Module/ViewModels/MapOverlayViewModel.cs#L132) | `Idle` → set origin → `Surveying`; `AwaitingPin` → place pin → `Surveying` |
| [`LogIngestionService.HandleSurveyDetected`](../../src/Legolas.Module/Services/LogIngestionService.cs#L91) | `Surveying` → store `PendingSurvey` → `AwaitingPin` (and a side-branch when ≥2 manual corrections exist that *bypasses* `AwaitingPin` entirely) |

Plus an `AllCollected` event on the `Surveys` collection that fires when
the last uncollected survey is marked, but doesn't flip a phase — only a
settings flag (`AutoResetWhenAllCollected`) decides whether anything
happens.

Motherlode has its own progression — `RecordedPositions` 0→1→2→3, then
`Distances.Count` 0→1→2→3 per slot, then trilateration kicks in — but
it's all implicit in `MotherlodeViewModel`, with no named phase.

This works, but the wizard can't bind to it: there's no single
`CurrentStep` value to template-select on, and each transition's
side-effects (auto-show overlay, auto-click-through, etc.) would have to
be replicated at every mutation site.

## The position-anchor constraint that shapes the design

The game's chat log emits survey results as `[Status]` lines containing
*relative* offsets ("X east, Y north") from the player's *current*
position. **There is no absolute coordinate available.** The projector
is anchored when the user clicks the map to set the player position,
plus optionally refit by 2+ manual pin corrections.

If the player moves and surveys again mid-session, the new offsets are
relative to the new location but the projector still maps against the
old anchor. We have no way to detect this — the chat log doesn't emit
movement events, and a phantom pin at (50E, 30N) post-movement looks
identical to a real new survey at (50E, 30N) from the original anchor.

**Implication for the FSM:** a Survey session is bounded by one player
position. Once the route is optimised and the player is walking, further
`SurveyDetected` events are dropped with a diagnostic. Adding new
targets mid-route requires an explicit Reset, which forces the user to
re-anchor. This is not paranoia — it's the only safe contract given
that we can't tell when the player has moved.

## Proposed design: two controllers + one shell

Two independent FSMs, one per mode, mirroring the prior-art shape of
[GardenStateMachine](../../src/Samwise.Module/State/GardenStateMachine.cs#L22)
(no FSM library; transition methods *are* the public API).

A thin `LegolasShell` (just a property on `SessionState`, or a small
sibling object — TBD during implementation) carries `ActiveMode = None |
Survey | Motherlode`. `PickMode` is the only shell-level command;
everything else delegates to whichever FSM is active.

### Why two FSMs instead of one tagged enum

- Survey and Motherlode share zero transitions. A unified enum would be
  textual concatenation, not semantic unity.
- The compiler enforces "you can't `OnSurveyDetected` on the Motherlode
  controller" — the unified-enum version was a runtime no-op + diag,
  which is the weaker version of the same check.
- Tests partition naturally per controller; no cross-mode interference
  cases.
- "Mode-not-picked" stops being a state of either workflow — it's a
  shell-level concern, which is where mode selection actually lives.
- Maps directly onto #4 bullet 1 (split Survey/Motherlode views): two
  FSMs → two wizards → no shared template gymnastics.

### `SurveyFlowController`

```
SurveyFlowState:

AwaitingPosition
   │ ConfirmPlayerPosition(p)
   ▼
Listening ◄──────────────────┐
   │ OnSurveyDetected(sd)    │ ConfirmPin(px)
   ▼                          │
AwaitingPin ──────────────────┘

Listening: OptimizeRoute()
   │
   ▼
Gathering          ← OnSurveyDetected here is IGNORED + diagnostic
   │ MarkCollected(...)  — last one
   ▼
Done
   │ Reset() (auto if AutoResetWhenAllCollected, else manual)
   ▼
AwaitingPosition
```

Linear, no back-edges across phases. The only cycle is the natural
`Listening ⇄ AwaitingPin` loop while a survey is being placed.

**Internal names** (`AwaitingPosition`, `Listening`, `AwaitingPin`,
`Gathering`, `Done`) are technical and not user-facing. The wizard owns
its own copy strings ("Click on the map where you are now", "Use a
survey — we're listening", etc.) so naming the states precisely doesn't
constrain UX vocabulary.

### `MotherlodeFlowController`

```
MotherlodeFlowState:

AwaitingPos1
   │ RecordPosition(p1)
   ▼
AwaitingDist1
   │ RecordDistance(d1)
   ▼
AwaitingPos2 → AwaitingDist2 → AwaitingPos3 → AwaitingDist3
   │ (trilateration computed once Pos3+Dist3 land)
   ▼
Ready
   │ OptimizeRoute()
   ▼
Optimized
   │ MarkCollected (last)
   ▼
Done
   │ Reset()
   ▼
AwaitingPos1
```

The position/distance progression is currently implicit in
`MotherlodeViewModel`'s `Slots.Count` plumbing. Surfacing it as named
states lets the wizard render distinct steps for "stand somewhere new"
vs. "tell me the distance you're seeing".

### Transition side-effects (bound to enter-state, not callsites)

These hooks are why the FSMs exist. Each is one place after the
refactor:

| Transition | Side effect |
|---|---|
| `Survey: → AwaitingPosition` | `Surveys.Clear()`, `PendingSurvey = null`, fire setting-driven auto-show overlays |
| `Survey: → Listening` | If `Settings.AutoClickThroughInventoryWhileGathering`, set `Settings.ClickThroughInventory = true`. (#4 bullet 4 — applies as soon as we're listening since that's when overlays go up) |
| `Survey: → AwaitingPin` | Set `PendingSurvey`. View binds to render the "click here" prompt. |
| `Survey: → Gathering` | Drop further `SurveyDetected` events with diagnostic. |
| `Survey: → Done` | Fire `AllCollected`; if `Settings.AutoResetWhenAllCollected`, schedule `Reset()` via dispatcher post (avoids re-entrant transition). |
| `Motherlode: → AwaitingDistN` | Increment `CurrentRound` exposed on the VM (decouples from `Slots.Count` plumbing). |

### Single `Transitioned` post-event per controller

```csharp
public sealed record SurveyTransition(
    SurveyFlowState From,
    SurveyFlowState To,
    string Trigger);

public event Action<SurveyTransition>? Transitioned;
```

Fires after every successful state change. Two listeners benefit:

1. **Diagnostics sink** — logs every transition in one place instead of
   `_diag?.Transition(...)` sprinkled in every transition method.
2. **Tests** — assert transition sequences directly
   (`transitions.Should().Equal(...)`), more expressive than poking
   final state.

The VM doesn't need this event — it binds to `[ObservableProperty]
SurveyFlowState _currentState` via `INotifyPropertyChanged` for wizard
re-templating.

**No pre-events / veto.** The controller is the only thing deciding
whether a trigger is valid; nothing external should veto. Pre-events
would be ceremony without payoff.

### Where corrections fit

The existing two-corrections-then-skip-pin path in
[`HandleSurveyDetected`](../../src/Legolas.Module/Services/LogIngestionService.cs#L112)
becomes part of `SurveyFlowController.OnSurveyDetected(sd)`: the
controller decides internally whether to enter `AwaitingPin` or place
the pin automatically. The decision logic doesn't change — just moves.
This stays implicit (no dedicated state) — the user experience is "I've
taught the projector enough, it just works now", which doesn't need its
own wizard step.

## Call-site map after the refactor

| File | Today | After |
|---|---|---|
| `ControlPanelViewModel.cs` | Mutates `Session.SurveyPhase` directly | Calls `_surveyFlow.Reset()`, `_surveyFlow.RequestSetPlayerPosition()`, `_surveyFlow.MarkCurrentCollected()` |
| `MapOverlayViewModel.HandleMapClick` | `switch` on `SurveyPhase`; mutates it | Calls `_surveyFlow.OnMapClicked(where)`; the controller decides what the click means |
| `MapOverlayViewModel.PlacePendingPinAt` | Mutates `SurveyPhase`/`PendingSurvey` | Calls `_surveyFlow.ConfirmPin(where)`; placement helper stays pure |
| `LogIngestionService.HandleSurveyDetected` | Mutates `SurveyPhase`/`PendingSurvey`; mode check | Calls `_shell.OnSurveyDetected(sd)` which routes to active controller (no-op if not Survey mode) |
| `LogIngestionService.HandleMotherlodeDistance` | Calls VM command | Calls `_motherlodeFlow.RecordDistance(d)` |
| `MotherlodeViewModel` commands | Mutate internal state directly | Call `_motherlodeFlow.RecordPosition()`, `_motherlodeFlow.RecordDistance(d)`, etc. The VM keeps the data; the controller owns transitions. |
| `SessionState` | `[ObservableProperty] SurveyPhase`; `HasPlayerPosition` flag | Splits: shared bits (`PlayerPosition`, `LastLogEvent`, opacity, click-through) stay; `Surveys` and `PendingSurvey` move to a `SurveySession` data record co-owned by `SurveyFlowController`. `SessionMode` becomes `ActiveMode`, derived from "which controller is live". |

## What stays out of scope for PR1

These all build on the FSMs but are deliberately separate PRs:

1. **Wizard view.** New `SurveyWizardView.xaml` and
   `MotherlodeWizardView.xaml` with per-state `DataTemplate`s, gated by
   a top-level `ContentControl` switching on `ActiveMode`. (#4
   follow-up.)
2. **Dashboard ("pro") shell.** Condensed status strip + contextual
   action panel that swaps content based on each FSM's `CurrentState`.
3. **`LegolasUiMode = Wizard | Dashboard` setting** with first-run
   default and "skip wizard next time" toggle.
4. **Configurable pin/marker colors.** Adds `LegolasColors` sub-settings
   + `LegolasBrushes` VM that observes it. Sized for a small parallel
   PR — not blocked on the FSM, but lands together with the wizard
   since the wizard is what surfaces the new color picker UI.
5. **Motherlode wizard polish** (the trilateration math is fine; only
   the prompts change).
6. **The Survey/Motherlode top-level split into separate panels** (#4
   bullet 1). The two-FSM design enables it; the layout work is the
   wizard PR.

## Test strategy

New `tests/Legolas.Tests/Flow/` directory with one file per controller.
Pattern mirrors [`GardenStateMachineTests`](../../tests/Samwise.Tests/) —
drive the controller through transition methods, assert state and
transition sequences via the `Transitioned` event.

**`SurveyFlowControllerTests`** covers at minimum:

- Happy path: ConfirmPlayerPosition → OnSurveyDetected → ConfirmPin →
  OptimizeRoute → MarkCollected ×N → Done.
- Auto-reset: with `AutoResetWhenAllCollected = true`, `Done` →
  `AwaitingPosition` (player position preserved).
- Two-corrections short-circuit: third+ `OnSurveyDetected` after two
  manual overrides skips `AwaitingPin`.
- Post-Optimize survey drop: `OnSurveyDetected` while in `Gathering` is
  a no-op + diagnostic, no state change, no new pin.
- Illegal triggers (e.g. `ConfirmPin` while in `AwaitingPosition`) are
  no-ops with a diagnostic — match Gandalf precedent.
- `Transitioned` event fires once per real transition with correct
  `(from, to, trigger)`.

**`MotherlodeFlowControllerTests`** covers:

- Happy path: 3× (RecordPosition → RecordDistance) → Ready →
  OptimizeRoute → MarkCollected → Done.
- Trilateration kicks in only after the third `(Pos, Dist)` pair lands.
- Distance-before-position is a no-op (or whatever the existing VM
  does — preserve current behaviour).

**Shell-level test** (`LegolasShellTests` or similar): `PickMode`
swaps which controller receives `OnSurveyDetected` /
`OnMotherlodeDistance`; calling the wrong one is a silent no-op with a
diagnostic.

Existing tests in `tests/Legolas.Tests/` (parser, optimisers,
trilateration) are untouched.

## Open questions

- **Where does shared state live?** `PlayerPosition`, `LastLogEvent`,
  opacity, click-through — these aren't owned by either FSM but are
  read by both. Two options: (a) keep them on `SessionState` and pass
  it to both controllers, or (b) split into `SurveySession` /
  `MotherlodeSession` with cross-references. Lean (a) for less
  upheaval; revisit if it leaks.
- **DI registration.** `SurveyFlowController` and
  `MotherlodeFlowController` as singletons in `LegolasModule.Register`,
  alongside the existing `SessionState`. Both get
  `IDiagnosticsSink` injected to listen on their own `Transitioned`
  events.
- **Backwards compat.** No persisted state references `SurveyPhase`
  (it's session-scoped, not in `LegolasSettings`). No migration needed.
