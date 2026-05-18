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
| `LocalPlayer: ProcessUpdateSkill({…}, <bool>, <gained>, 0, 0)` | One skill's progression delta + XP earned this tick. | **Per-skill upsert** (copy-on-write) + a `Delta` change event carrying `XpGained`. |

### Capped skills go silent

Once a skill reaches its cap (`raw == max`), PG emits **no further
`ProcessUpdateSkill`** for it. This needs no special-casing: the change
channel simply stops producing `Delta` events for that skill; its absolute
state still arrives in every `ProcessLoadSkills` (where it shows `raw == max`
→ `IsCapped`). The *capping tick itself* is the last `Delta` for the skill and
is where `Previous.IsCapped == false && Current.IsCapped == true`.

### `XpGained` (the third positional) — chat-triangulated

`arg3` of `ProcessUpdateSkill` is the XP earned on that tick. It was validated
against the authoritative chat `[Status] You earned N XP in <Skill>.` line for
the same events, lining the `Player.log` (UTC) and `ChatLogs` (local, +1h)
timelines up:

| Event | Chat `[Status]` | `ProcessUpdateSkill` `arg3` |
|---|---|---|
| 11:36:42 UTC | "earned 26 XP in Endurance" | `{type=Endurance,…}, True, **26**, …` |
| 11:36:42 UTC | "earned 577 XP in Psychology" | `{type=Psychology,…}, False, **577**, …` |
| 11:36:52 UTC | "earned 48 XP in Bear and Bugbear Anatomy" | `{type=Anatomy_Bears,…}, True, **48**, …` |

Three exact matches across the offset. `arg3` is keyed by **internal name**
(`type=Anatomy_Bears`) whereas the chat line is display-named ("Bear and
Bugbear Anatomy") — so `Player.log` is the *better* ingestion source (no
fragile display→key reverse lookup); chat is the verification oracle. The
`<bool>` (announce / batch-vs-discrete) and the trailing `0, 0` are not parsed.

### Live level-up capture (verification resolved)

A captured Tailoring level-up:

```
[12:38:57] {type=Tailoring,raw=9, bonus=2,xp=199,tnl=210,max=50}, True, 160, 0, 0
           chat: "You earned 160 XP in Tailoring."
[12:39:02] {type=Tailoring,raw=10,bonus=2,xp=149,tnl=420,max=50}, True, 160, 0, 0
           chat: "You earned 160 XP and reached level 12 in Tailoring!"
```

Settles the open questions:

- **`arg3` is the *gross* XP gained, even across the rollover.** Tick 2
  crossed the boundary and `arg3 = 160` = the chat value; the engine does
  **not** split it pre/post-level. Summing `XpGained` stays correct through
  level-ups (arithmetic: 39 → +160 = 199 (<210) → +160 = 359, overflow
  359−210 = 149 into the new level, new `tnl` 420, `raw` 9→10). So
  `XpGained` is authoritative across a single-level rollover, not
  "best-effort."
- **`args 4,5` stay `0, 0` through a level-up** — not a levels-gained /
  skill-up count. Reserved/unused; no longer "verification owed."
- **No dedicated level-up line in `Player.log`.** The level-up is conveyed
  *only* by `raw` incrementing on the next `ProcessUpdateSkill` — exactly what
  `SkillChange.Previous.Level < Current.Level` detects.
- **Chat "reached level 12" = `raw(10) + bonus(2)`** (effective level);
  `Player.log` `raw` is the base. Reconfirms keeping `Level`/`BonusLevels`
  separate.

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

## Two subscription channels

- **`Subscribe(Action<PlayerSkillSnapshot>)`** — whole-state. Replays the
  current snapshot on attach, then pushes the full snapshot on every change.
  For "what is the player's skill state now" consumers.
- **`SubscribeChanges(Action<SkillChange>)`** — granular. **No replay** (a
  change is an event, not state — read `Current` for state). One `Delta` per
  `ProcessUpdateSkill` carrying `XpGained`; one `SnapshotReplace` per skill
  whose projection *actually differs* on a `ProcessLoadSkills` (a no-op re-sync
  emits nothing; `XpGained == 0` for snapshot changes). Derived signals fall
  out of `Previous`/`Current`: level-up = `Previous?.Level < Current.Level`;
  just-hit-cap = `Previous is { IsCapped: false } && Current.IsCapped` (then
  the skill goes silent — see above).

## Threading

`Current` is a reference read of an immutable object (lock-free). The snapshot
reference is swapped under an internal lock; both `Subscribe` (replay + attach)
and live dispatch happen **atomically under the same lock the ingestion loop
fires under**, which closes the late-subscribe race (mirrors
`InventoryService`). Snapshot handlers then change handlers fire under that
lock — do non-trivial work off-thread.

## Components

- `Skills/Parsing/SkillEvents.cs` — `SkillProgressRecord` (raw parse),
  `SkillsSnapshotEvent`, `SkillProgressUpdateEvent` (incl. `XpGained`).
- `Skills/Parsing/SkillLogParser.cs` — `ILogParser`; `LoadSkillsRx` /
  `UpdateSkillRx` guards, the `SkillTupleRx` workhorse, and `XpGainRx` for
  `arg3`. Catalogued in `log-patterns.json` as `shared.SkillLogParser.*` (the
  `shared` module prefix follows the precedent of the other `Mithril.GameState`
  parsers in the catalog; parity asserted by `LogPatternCatalogParityTests`).
- `Skills/IPlayerSkillState.cs` — interface + `PlayerSkillSnapshot` /
  `SkillProgressSnapshot` / `SkillStateSource`.
- `Skills/SkillChange.cs` — `SkillChange` / `SkillChangeKind` (the
  `SubscribeChanges` channel payload).
- `Skills/PlayerSkillStateService.cs` — `BackgroundService` + `IPlayerSkillState`,
  registered in `GameStateServiceCollectionExtensions.AddMithrilGameState`.

## Verification owed

- **`XpGained` (`arg3`) / trailing positionals — RESOLVED** by the live
  level-up capture above. `arg3` is the gross XP gained, chat-matched, correct
  across a single-level rollover; `args 4,5` stay `0, 0` through a level-up
  (not levels-gained). Remaining unknown is narrow and low-impact: a
  multi-level single tick (raw jumping by >1) is unobserved — the model still
  holds (`raw` is taken from the struct, `XpGained` is the gross total), only
  the per-level decomposition (which no consumer needs) is unverified.
- **`ProcessLoadSkills` trigger.** Confirmed at login + several mid-session
  (zone/relog) fires; whether it fires on *every* zone change is unconfirmed.
  The wholesale-replace design is robust either way (worst case: a slightly
  older but still-complete table until the next fire).
- **Pseudo-skill set.** Only Augmentation / Performance / Phrenology observed
  with `max==0`. The code uses the `max==0` predicate (not a name list), so a
  new one is handled automatically — but enumerate against the skills
  reference before relying on the exact set anywhere.
