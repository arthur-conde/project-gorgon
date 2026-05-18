# Player map-pin service (`Mithril.GameState.Pins`)

Shared, `Player.log`-fed live view of the player's **map pins** for the
current area — the authoritative, replay-deduped, area-scoped pin set. Issue:
[#468](https://github.com/moumantai-gg/mithril/issues/468).

## Why it exists

Pin parsing started life inside Legolas (`PlayerLogParser` →
`MapPinAdded`, #459) purely to feed cold-start map calibration. But the pin
set is general live game-state — several map-overlay modes (calibrate,
verify, landmarks, motherlode) want "what pins does the player have here?" —
and PG's pin log has two awkward properties every consumer otherwise
re-implements:

1. **It bulk-replays the whole set on every login / area entry** with no
   "clear" beforehand. A naïve consumer either double-counts or has to
   hand-roll an "arm" gate to discard the backlog (the old
   `CalibrationSessionViewModel` and `PinCalibrationCoordinator` each did).
2. **There is no edit/move/clear verb** — only `Add` and `Remove`. A rename
   or move is `Remove`+`Add`. Lifecycle reconstruction is non-trivial.

Promoting it to a GameState-tier service (mirroring the
`AreaTransitionParser` → `Mithril.GameState.Areas` promotion in #456, and the
`PlayerPositionTracker` shape from #461) makes the service own that lifecycle
once, so consumers just read a correct current set.

## What it reads

Two log lines, the **only** two pin verbs PG emits:

| Line | Meaning | Service action |
|---|---|---|
| `LocalPlayer: ProcessMapPinAdd(A, B, C, (X, 0.00, Z), "label")` | A pin was placed. Also **bulk-replayed** for the whole set on every login / area entry. | **Upsert** keyed by rounded `(X,Z)`. Idempotent — a replayed pin at an unchanged coordinate raises no event. |
| `LocalPlayer: ProcessMapPinRemove(A, B, C, (X, 0.00, Z), "label")` | A pin was deleted. | **Remove** by coordinate key. |

There is no `Edit`, `Move` or `Clear` verb. The client models those as:

- **Rename:** `Remove(coord, oldLabel)` then `Add(coord, newLabel)` (same timestamp-second).
- **Move:** `Add(newCoord, label)` then `Remove(oldCoord, label)`.

So a pin's **identity is its coordinate**, never its label.

### Argument grammar (decoded 2026-05-18)

Decoded from three live captures (`Player.log`, `Player-prev.log`,
`oldPlayer.log`) cross-referenced with the in-game pin-editor screenshot.

| Arg | Meaning | Mapping |
|---|---|---|
| **A** | Opaque. Invariant `1` in every capture (pin-list / visible flag). | Surfaced verbatim as `MapPin.RawList`; never interpreted. |
| **B** | **Shape** (the editor's "Design" row). | `0` = `Dot` (circle+dot), `1` = `Square` (square+dot). Out-of-range → `Unknown`. |
| **C** | **Colour** (the 10-swatch palette). | Index left→right, top row then bottom: `0` White, `1` Red, `2` Orange, `3` Yellow, `4` Green, `5` Cyan, `6` Blue, `7` Purple, `8` Pink, `9` Black. Out-of-range → `Unknown`. |
| `Y` (middle of triple) | Always `0.00` — pins are 2-D map markers. | Dropped. |

`0`=White is user-confirmed; `1`=Red is empirical (the captured campfire
pins, which the player had deliberately coloured red); `2–9` are ratified
from the palette reading order. There is **no system-assigned colour or
shape** — every value is the player's own choice, which is what makes them a
good *human* disambiguator for the existing-pins calibration route.

Coordinates are **signed** (negative X/Z common) and area-local — the same
per-area engine-unit world frame as `npcs.json` `Pos` / `landmarks.json`
`Loc` and the player's own position (memory: `pg_reference_coords_are_world_frame`).

## Lifecycle model

```
                ┌─────────────── area change (PlayerAreaTracker key delta)
                │                 → clear set, raise AreaChanged(empty)
                ▼
  ProcessMapPinAdd ──► upsert by rounded (X,Z)
        │                 • new coord / changed label  → raise Added
        │                 • identical re-add (replay)   → no-op, no event
        ▼
  ProcessMapPinRemove ──► remove by (X,Z) ──► raise Removed (if it existed)
```

- **Replay = idempotent upsert.** The login/area-entry burst is `Add`s with
  no preceding clear; identical coordinates collapse, so the post-replay set
  equals the pre-replay set and consumers stay quiet through the backlog.
- **Area transition = swap.** Pins are area-local; on any
  `PlayerAreaTracker.CurrentArea` change the set is dropped and an
  `AreaChanged` (empty) notification is raised. The new area's replay burst —
  which follows the `LOADING LEVEL` line that moved the key — repopulates it.
- **No area reverse-scan seed** (unlike `PlayerAreaTracker`/`PlayerPositionTracker`):
  the pin replay burst lands *inside* the live tail window (after the login
  `ProcessAddPlayer` the window is seeded to), so the set self-populates.

## Public API

| Member | Purpose |
|---|---|
| `IPlayerPinTracker.CurrentArea` | The area key the tracked set belongs to, or `null`. |
| `IPlayerPinTracker.CurrentAreaPins` | Immutable snapshot of the current area's pins. |
| `IPlayerPinTracker.Subscribe(Action<PinSetChanged>)` | Replays a `Snapshot` synchronously, then live `Added`/`Removed`/`AreaChanged` deltas. Returns a disposable unsubscribe token. |
| `MapPin` | `X`, `Z`, `Label`, `Shape`, `Color`, `RawList`; `Appearance` (`"red dot"`), `DisplayName` (label or `"Unnamed pin"`). |
| `PinSetChanged` | `Kind`, `Area`, `Pin` (the single affected pin for Added/Removed), `Pins` (full post-change snapshot), `ObservedAt` (`DateTimeOffset`). |

`MapPinLogEvent` (parser output) carries the `LogEvent`-mandated `DateTime`;
the tracker converts it to a `DateTimeOffset` at the boundary via the
established `ToOffset(ts) => new(DateTime.SpecifyKind(ts, DateTimeKind.Utc))`
pattern, so every model/notification timestamp is an unambiguous UTC instant
without widening `ILogParser`.

## Threading

`PlayerPinTracker` is a self-feeding `BackgroundService` (mirrors
`PlayerPositionTracker`). Ingestion runs on the hosted-service loop thread;
state mutation, `CurrentArea`/`CurrentAreaPins` reads and subscriber dispatch
are serialised under one lock. Every notification carries an **immutable
snapshot**, so a handler may hold `Pins` safely. Handlers run on the
ingestion thread — non-trivial / UI work must marshal off (e.g. WPF consumers
dispatch to the UI thread, exactly as the calibration coordinator does).

## Consumers

- **Legolas cold-start calibration** (`PinCalibrationCoordinator`,
  `CalibrationSessionViewModel`). Two routes, one solve — see the
  "Pin calibration: two routes & the label-agnostic reconciliation" section
  of [`legolas-overview.md`](legolas-overview.md). The solve only ever sees
  `(WorldCoord ↔ pixel)`; colour/shape/label are UX-only disambiguation.
- Future map-overlay modes (verify / landmarks / motherlode) can read the
  same area-scoped set instead of re-deriving it.

## Owed verification / data ceiling

- **Arg A** semantics are unknown (invariant `1`); intentionally opaque, no
  risk. Surfaced as `RawList` in case a future capture varies it.
- `PinShape.Square` (`B=1`) never appeared in any capture — its value is
  screenshot-derived. Low risk; revisit if a varied capture disagrees.
- The full colour map (`2–9`) is palette-order-ratified by the maintainer,
  not separately log-confirmed per index — acceptable, not flagged as a
  blocking unknown.

## Related

- Tier/pattern precedent: `Mithril.GameState.Movement` (#461),
  `Mithril.GameState.Areas` parser promotion (#456).
- Memory: `pg_map_pin_log_grammar`, `legolas_calibration_findings`,
  `pg_reference_coords_are_world_frame`, `prefer_datetimeoffset`.
