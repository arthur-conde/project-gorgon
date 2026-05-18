# Player skill-state service (`Mithril.GameState.Skills`)

Shared, `Player.log`-fed live view of the player's current skill progression —
every skill, without requiring a character re-export. Issue: #462.

## Why it exists

The neutral leveling input (`Mithril.Leveling.SkillState`) historically had a
single producer: Elrond's `SnapshotPlanInput.ToSkillState(CharacterSnapshot)`,
fed from the character **export**. That export is the only reason leveling
advice goes stale until the player manually re-exports. `Player.log` already
carries the whole picture continuously, so this service makes it the live
source.

## What it reads

Two log lines, both built from the identical
`{type=…,raw=…,bonus=…,xp=…,tnl=…,max=…}` struct grammar:

| Line | Meaning | Service action |
|---|---|---|
| `LocalPlayer: ProcessLoadSkills({…}, {…}, …)` | Full skill-table dump (~125 rows). Emitted at login **and on every zone / session transition.** | **Wholesale replace** of tracked state. |
| `LocalPlayer: ProcessUpdateSkill({…}, <bool>, <delta>, 0, 0)` | One skill's progression delta. | **Per-skill upsert** (copy-on-write). |

### Field mapping (1:1, raw parse → projection)

| Log field | `SkillProgressRecord` (raw) | `SkillProgressSnapshot` (projection) |
|---|---|---|
| `type` | `SkillKey` | the map key |
| `raw` | `Level` | `Level` |
| `bonus` | `BonusLevels` | `BonusLevels` |
| `xp` | `XpTowardNextLevel` | `XpTowardNextLevel` |
| `tnl` | `XpNeededForNextLevel` | `XpNeededForNextLevel` |
| `max` | `MaxLevel` | `MaxLevel` + `IsCapped`/`IsTrainable` |

## Data caveats (interpreted into predicates, not assumed away)

These are encoded on `SkillProgressSnapshot` so every consumer applies them
identically:

- **Capped skills** (`raw == max`, max > 0) → `IsCapped == true`. `tnl`/`xp`
  are stale at the cap; do not present "N xp to next level" for them.
- **Pseudo-skills** (`max == 0`, e.g. Augmentation / Performance / Phrenology,
  reported `raw=0,bonus=N,max=0`) → `IsTrainable == false`. Kept in the
  snapshot (flagged, not dropped) so a consumer decides; the leveling
  constraint set should filter them out.
- **`bonus` is gear/buff/form-derived, not progression.** It is volatile (it
  moves as the player swaps equipment or shifts form). `Level` (raw) is the
  progression truth. The projection keeps them as separate fields and **never
  sums them** — consumers must not either.
- **Timestamps are UTC.** `Player.log`'s `[HH:MM:SS]` prefix is UTC;
  `PlayerSkillSnapshot.MeasuredAt` is therefore UTC. Surface it for freshness:
  a *live* snapshot can still be minutes old if the player has idled in one
  zone.
- **Skill keys are internal names** (`Anatomy_Bears`, `Performance_Dance`),
  kept verbatim. UI resolves the key → display name (model keeps the key —
  project-wide convention).

## Self-heal / warm-up contract

`ProcessLoadSkills` re-fires on every zone change, so a wholesale replace on
each one keeps state correct **even when Mithril starts tailing mid-session** —
the next zone transition re-establishes the full table (typically within
minutes). Until the first `ProcessLoadSkills` of the session:

- `Current` is `PlayerSkillSnapshot.Empty` (`Source == None`,
  `MeasuredAt == null`); **or**
- if isolated `ProcessUpdateSkill` lines arrive first, `Current` is a
  deliberately **partial** snapshot (`Source == LiveLog`) — better than
  nothing; the next `ProcessLoadSkills` makes it whole.

This window is the documented behaviour, not a bug. No reverse-scan startup
seed is built (the self-heal makes it unnecessary for v1); it remains a
possible future enhancement.

## Architecture decision: GameState-native type, no `Mithril.Leveling` dependency

The service exposes a **GameState-native** `PlayerSkillSnapshot` /
`SkillProgressSnapshot`, *not* `Mithril.Leveling.SkillState`. Rationale:

- `Mithril.GameState` is neutral shared infrastructure; coupling it to the
  leveling-math library for a consumer that isn't wired yet would be poor
  hygiene and widen the dependency graph of every module that already depends
  on game state.
- The established pattern is **consumer-owned adapters**: Elrond's
  `SnapshotPlanInput` already adapts the export's per-skill record → `SkillState`
  with a flat field copy. A future live-source adapter mirrors that exactly
  (`IPlayerSkillState` → `SkillState`) and lives on the consumer side.
- This is a refinement of the #462 scope wording ("`SkillState Current`"),
  recorded as a comment on the issue. It does not fork the neutral abstraction;
  it keeps the adapter where the codebase already puts it.

Consumer-side wiring (Elrond preferring live over export, freshness badges) is
explicitly **out of scope** here — service work only.

## Threading

`Current` is a reference read of an immutable object (lock-free). The snapshot
reference is swapped under an internal lock; `Subscribe` replays the current
snapshot and attaches the handler **atomically under the same lock the
ingestion loop fires under**, which closes the late-subscribe race (mirrors
`InventoryService`). Handlers run synchronously under that lock — do
non-trivial work off-thread.

## Components

- `Skills/Parsing/SkillEvents.cs` — `SkillProgressRecord` (raw parse),
  `SkillsSnapshotEvent`, `SkillProgressUpdateEvent`.
- `Skills/Parsing/SkillLogParser.cs` — `ILogParser`; `LoadSkillsRx` /
  `UpdateSkillRx` guards + the `SkillTupleRx` workhorse. Catalogued in
  `log-patterns.json` as `shared.SkillLogParser.*` (the `shared` module prefix
  follows the precedent of the other `Mithril.GameState` parsers in the
  catalog; parity asserted by `LogPatternCatalogParityTests`).
- `Skills/IPlayerSkillState.cs` — interface + `PlayerSkillSnapshot` /
  `SkillProgressSnapshot` / `SkillStateSource`.
- `Skills/PlayerSkillStateService.cs` — `BackgroundService` + `IPlayerSkillState`,
  registered in `GameStateServiceCollectionExtensions.AddMithrilGameState`.

## Verification owed

- **`ProcessUpdateSkill` trailing positionals** (announce bool, XP delta, two
  zeros) are intentionally **not parsed** — semantics are only inferred from a
  small sample. The leading struct is authoritative for state, so they are not
  needed. Revisit if a consumer wants "last XP gain" telemetry; capture a
  level-up sample first (all observed had the trailing `0, 0`).
- **`ProcessLoadSkills` trigger.** Confirmed at login + several mid-session
  (zone/relog) fires; whether it fires on *every* zone change is unconfirmed.
  The wholesale-replace design is robust either way (worst case: a slightly
  older but still-complete table until the next fire).
- **Pseudo-skill set.** Only Augmentation / Performance / Phrenology observed
  with `max==0`. The code uses the `max==0` predicate (not a name list), so a
  new one is handled automatically — but enumerate against the skills
  reference before relying on the exact set anywhere.
