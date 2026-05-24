# World-sim — single-source rethink

**Status:** preparatory design notebook. Captures the converged design for the world-sim rethink that came out of the design-partner session of 2026-05-24. Actionable plan, sizing, and phasing live in [#800](https://github.com/moumantai-gg/mithril/issues/800); this doc is the design rationale that co-evolves with the code. Implementation PR not yet drafted; this doc is the cold-session-resumable reference for picking up where the design conversation left off.

**This doc supersedes:**

- The "merger over N producers" framing in [`world-simulator.md`](world-simulator.md) principle 1.
- The `IFrameProducer<T>` IAsyncEnumerable contract and the per-producer `IModeAwareFrameProducer<T>.ReachedLive` plumbing.
- The implicit clock-tick-as-peer-producer mechanism ratified in [`world-simulator.md`](world-simulator.md) §Decisions ratified post-#642 (#644). The *outcome* of #644 (explicit clock-tick owner; no implicit advancement via folder-irrelevant frames) survives; the *mechanism* (a `WorldClockTickProducer` pretending to be a peer producer alongside the others) was the over-abstraction this rethink retires.

**Companion docs:**

- [`world-simulator.md`](world-simulator.md) — current world-sim architecture. The "Doc amendments owed" section below enumerates the surgical edits this rethink requires.
- [#800](https://github.com/moumantai-gg/mithril/issues/800) — actionable plan and sizing. The issue body's original "introduce alongside / migrate per-producer / retire" phasing is superseded by the sweep-PR approach below.
- [`cross-source-correlation.md`](cross-source-correlation.md) — tier hierarchy for cross-source pairing patterns. Unaffected by this rethink (the tier hierarchy operates at the view layer, above the world's dispatch loop).

## Trigger

[PR #799](https://github.com/moumantai-gg/mithril/pull/799) fixed a silent-drop bug in `PlayerLogPipeSplitter.SubscribeWithMarker` — a bounded 1024 channel with `DropOldest` evicted ~134 `ProcessAddItem` envelopes during a long-session cold-start. The user-visible symptom was *"area changes are not reaching Palantir or Legolas"*, but the failure chain exposed a deeper structural fragility in the world-sim merger that the channel fix doesn't address.

The merger's main loop in [`PlayerWorld.cs:127-185`](../src/Mithril.WorldSim.Player/PlayerWorld.cs) sequentially awaits each producer's first `MoveNextAsync` before dispatching any frame. If any one producer has no head, the entire merger blocks at that `await`. `AreaLoadingFrameProducer` is the canonical case: it never emits during the L1 session-start replay window by design (`LOADING LEVEL` lines are upstream of the session anchor `ProcessAddPlayer`, so the L1 driver's `FromSessionStart` window doesn't include them). Post-#799 the merger reliably reaches the area producer's `await` and parks there indefinitely until the user portals; state updates from skill / inventory / words-of-power land in portal-sized bursts.

This is the active correctness foot-gun the rethink fixes. But the structural critique is broader: the merger pattern is the wrong shape for the worlds we have.

## Architectural critique

The merger pattern is the natural shape for an N-way ordered merge over **independent streams**. None of the worlds we have today is actually that:

- **PlayerWorld** is single-source: every registered producer is a projection over the same `Player.log` line stream surfaced through the L1 driver. The L1 driver's source-`Sequence` (byte offset in the source file) is a total order.
- **ChatWorld** is single-source: the chat replay source.
- **Cross-source coordination** (e.g., `InventoryView` correlating PG inventory mutations with chat `[Status]` observations) does NOT happen inside a unified merger. Per principle 4 of [`world-simulator.md`](world-simulator.md), cross-source composition lives in *views* above the worlds. Each world's input is genuinely a single source.

So the merger inside each world is doing N-way ordered merge over N projections of a stream that is already totally ordered. The ordering it produces is identical to the ordering it would get by just iterating the source in read-order and asking each consumer "do you care about this envelope?". The IAsyncEnumerable abstraction adds a layer that needs cross-producer synchronisation (every producer must have a head before dispatch proceeds) where none is structurally required.

`world-simulator.md` principle 1 wrote:

> Frame = `(timestamp, payload)`. The unifying primitive. … **Each world is a timestamp-ordered merger over its N producers.**

That sentence is correct in the abstract — it describes what a world *would* need if its producers came from independent streams. It is the wrong shape for the worlds we actually built. **The world's contract is frames in read-order; the timestamp is a property of the data, not a sort key the world enforces.**

## Proposed architecture

### Layer-wide invariant: L0–L2 single-output, L3 fan-out

The single biggest design clarification from the session is to state the fan-out vs single-output invariant as a *layer-wide* commitment. It forecloses re-litigation at every future addition.

| Layer | Component | Cardinality |
|---|---|---|
| **L0** | `LogSourceTailer` + `PlayerLogClock` / `ChatLogClock` | One line in → one stamped `RawLogLine` out. Linear. |
| **L0.5** | `PlayerLogLineClassifier` + `PlayerLogPipeSplitter` | One `RawLogLine` in → one classified envelope out (`LocalPlayerLogLine` / `CombatActorLogLine` / `SystemSignalLogLine` / anomaly). One classification per line — discrimination, not distribution. Authoritative source of `IsReplay`. |
| **L1** | `ILogStreamDriver` | API admits multi-subscriber `Subscribe<T>`, but world-sim exercises it as **one subscription per world** (to the unified classified stream). Multi-subscriber capability persists for operational reasons (tests, opt-in consumers); architectural-layer usage is linear. |
| **L2** | Envelope → change (verb-keyed parsing) | One envelope in → at most one parsed change out (verb-keyed; PG's verbs are mutually exclusive at the line level). Linear. |
| **L3** | State machines on world.px + events on world.out + views in the composition pipeline + modules | **Genuine fan-out, by data.** world.px fans changes to multiple state machines (polysemy — same change consumed by multiple SMs). world.out fans events to multiple subscribers (a `GiftAccepted` reaches Arwen + Smaug calibration + UI + future telemetry). comp.in fans the world's + chat's + reports' outputs into N views. All three fan-outs are empirical (N>1 consumers in current modules), not hypothetical. |

The fan-out shape was always architecturally correct at L3. The previous "fan-out at the transform stage" framing was conflating L3's empirical fan-out with an L1→L2 abstraction borrowing the shape one layer too high. Verb-keyed routing at L2 + fan-out at L3 is the architecturally honest split.

### World closure — the world is closed under its source

A generalization of [`world-simulator.md`](world-simulator.md) principle 4. The world accepts envelopes from its source stream only. Cross-category inputs — `Mithril.Reference`, `Mithril.GameReports`, `ICommunityCalibrationService`, other worlds — do **not** flow into the world's state machines. Composition with those categories lives at the view layer, not inside the world.

The original principle 4 ("no service spans both Player.log and chat") was a specific instance of this rule with both inputs being log sources. The broader commitment: **the world is closed under any cross-category input**, not just cross-source-log. A state machine inside the world consuming a category-2 source (e.g., an Inventory SM reading report snapshots for vault contents) is the same shape of seal-break as a state machine consuming two log sources — it just uses a different category as the second input.

Consequences:

- **The world's state for any concept is whatever is derivable from its source stream alone.** Incompleteness is faithful, not a gap to paper over. Vault state pre-attach is unknown to the world because PG doesn't emit a vault snapshot at session start; that's what the world's view should faithfully reflect.
- **Consumers needing "complete current state" across categories go to a view.** Views are the universal composition point per principle 4 — composing world + world, world + report, world + reference data, world + community calibration.
- **Reports / reference / calibration services consume their own data sources independently.** They are not attached as inputs to a world; their output is consumed at the view layer.

The vault example (worked example 3 below) is the canonical case: vault deltas are log-observable (`ProcessAddToStorageVault` / `ProcessRemoveFromStorageVault`); the world owns those deltas; the pre-attach baseline lives in `Mithril.GameReports`; the composition lives in a view.

### Vocabulary — world.in, world.px, world.out, comp

Four named channels, with consistent abbreviations:

- **world.in** — the world's input pipe. Carries envelopes flowing from the Player Log Source (or chat source, for ChatWorld) through the World Filter into the world boundary. Single subscription per world.
- **world.px** — the world's internal pipe. Carries *changes* (parsed verb data) to state machines within the world. Typed pub-sub; state machines subscribe to change types they care about.
- **world.out** — the world's output pipe. Carries domain *events* emitted by state machines. Single channel; subscribers consume by typed event.
- **comp.in** — the composition pipeline's input. Composes inputs from world.out + chatout + reportout (and reference, when needed); the universal view-layer composition point.

Two directional invariants: nothing flows back into world.in from above; nothing flows back into world.px from state machines (state machines emit on world.out, not on world.px). The channels are unidirectional by construction.

### State machines — one kind of thing

Under the rethink, the **folder vs composer distinction collapses** into a single uniform concept: a **state machine within the world**. A state machine:

- Subscribes to specific *change types* on world.px (declares its interest at registration; the pipe fans out changes to subscribers)
- Maintains its own state (the world-fact it owns)
- Emits *domain events* on world.out when something externally meaningful happens

There is no "folder emits change events; composer emits domain frames" split. All world.out emissions are domain events; the bus and its subscribers don't categorize by emitter. Whether a state machine emits one event per change (atomic — the Inventory SM emits `InventoryItemAdded` upon processing a `ProcessAddItem` change) or emits after multi-change recognition (compound — the NPC SM emits `GiftAccepted` only after correlating `StartInteraction + DeleteItem + FavorDelta`) is the state machine's choice; the architectural shape is identical.

Today's `IFolder<T>` and `IComposer` collapse into a single interface (working name `IStateMachine`; implementation choice). Today's `IGiftSignalService` is a state machine; today's `PlayerInventoryFolder` becomes a state machine of the same shape.

### Per-envelope dispatch loop contract

For each envelope arriving on world.in:

1. **World Filter** drops envelopes lacking the timestamp-prefix shape (engine spew, multi-line dumps that inherited their L0 stamp from a prior line). Filtered envelopes never reach the world boundary.
2. **Clock advances** to the envelope's timestamp (envelope timestamp ≥ world clock; world clock is "last applied frame's timestamp" per principle 5).
3. **Pre-canonical-dispatch observers** see the raw envelope (side channel — see Observer surface below). No effect on canonical dispatch.
4. **Envelope is parsed into a change** — the verb's data exposed as typed properties. A change is a `(timestamp, verb-payload)` pair routed by verb-keyed lookup to its parser; at-most-one change per envelope (PG verbs are mutually exclusive at the line level).
5. **Change is published on world.px**. Every state machine subscribed to that change type receives it (typed pub-sub fan-out; ordering by registration is deterministic, doesn't matter when SMs don't share state).
6. **Each subscribed state machine** processes the change synchronously — mutates its own state, may emit domain events on world.out. Multiple state machines can consume the same change (polysemy); each does its own thing without coordination.
7. **Synthetic clock-tick change** (special case): the dispatch loop also publishes a "clock-tick" change on world.px when the simulated clock advances. The **Environment SM** subscribes to clock-ticks and derives all time-related events (`CalendarTimeAdvanced`, `TimeOfDayShift`, `GameDayChange`, plus log-driven `MoonPhaseChange`). This keeps "all world.out events come from state machines" uniform — time events aren't a special-case infrastructure emission; they're the Environment SM's emissions in response to clock-tick changes.
8. **Post-canonical-dispatch observers** see `(envelope, optional change, domain events emitted)` (side channel).

**Contract notes:**

- **Clock-first** is uniform across the dispatch loop. The clock advances to the envelope's timestamp *before* any state machine reads it; the synthetic clock-tick change reaches the Environment SM *before* per-verb changes reach their state machines; subscribers to time-related events on world.out see them *before* any other event from the same envelope.
- **No "subscribers in registration order" contract is owed** for the typed pub-sub on world.px. State machines under world-fact decomposition own disjoint state; the order in which they process the same change doesn't matter because they don't share state to race over.
- `CalendarTimeAdvanced` deduplication is **per simulated second**, not per envelope. The Environment SM tracks "last emitted second" and emits CalendarTimeAdvanced only when the second changes.
- world.out emission order within an envelope's resolution is determined by the dispatch graph's topological order, fixed at world-registration time. Subscribers see events in deterministic order across runs.

### Changes are polysemous; semantic inference is state-machine-side

The same change can mean different things to different state machines:

- `ProcessStartInteraction(npc)` change — vendor open / gift target / quest dialogue / NPC chat / training menu. Consumed by NPC SM, which owns the interaction context and discriminates from co-occurring changes.
- `ProcessDeleteItem(instanceId)` change — consumed by **both** Inventory SM (for ledger mutation, regardless of why the item was deleted) AND NPC SM (for gift / vendor sale / quest-turn-in detection within an open interaction context). Polysemy is first-class: two state machines, one change, each does its own thing.
- `ProcessCurrencyDelta(amount)` — vendor sale / vendor purchase / quest reward / council found / training cost. Inventory SM might consume for currency tracking; NPC SM consumes for vendor-sale discrimination.

The architectural property: state mutation (the SM's own state) is separated from semantic inference (the meaning derived from co-occurring changes). N state machines interpret the same change N different ways without coordination because each owns disjoint state under world-fact decomposition.

### World-fact decomposition for state machines

**Decompose state machines by the world-fact they own, not by the output event they emit.** When multiple semantic outcomes share a common context (NPC interaction, crafting, combat, travel), one state machine owns the context and emits all outcome events. When an outcome is solo and context-free, a single-purpose state machine is fine.

The canonical example: `INpcInteractionStateMachine` owns "is the player currently in an NPC interaction, and with whom?" and emits:

- `InteractionStarted(npc)` — interaction began
- `InteractionEnded(npc, outcome)` — interaction ended; outcome ∈ {gift, vendor sale, quest turn-in, no-op, ...}
- `GiftAccepted(npc, item, favorDelta)` — discriminated outcome
- `VendorSold(vendor, item, price)` — discriminated outcome
- `QuestTurnedIn(quest, npc)` — discriminated outcome
- (...additional outcome events as PG's verb vocabulary surfaces them)

It subscribes to the union of relevant changes (`ProcessStartInteraction`, `ProcessDeleteItem`, `ProcessFavorDelta`, `ProcessCurrencyDelta`, etc.) on world.px. The shared prefix (`StartInteraction + ItemDelete`) is *interaction context*, a real world fact. Outcome events are derived from the context's evolution upon arrival of the discriminating change.

Why this decomposition:

1. **The state being tracked is a real world fact, not just SM bookkeeping.** "Player is interacting with NPC X" is a property of the simulated world. Three separate SMs each maintaining their own copy is bookkeeping duplication of a real datum.
2. **Outcomes are mutually exclusive within an interaction.** One state machine naturally enforces this; three independent SMs each watching the shared prefix works only because the discriminating changes happen to be mutually exclusive in practice — the architecture doesn't enforce it.
3. **The interaction lifecycle is itself a meaningful surface.** `InteractionStarted` / `InteractionEnded` are useful regardless of outcome (UI cue, combat-readiness gating, telemetry). One state machine surfaces them.
4. **Aligns with the three-surface (Query/React/Bind) contract from [#707](https://github.com/moumantai-gg/mithril/issues/707).** The state machine exposes:
   - **Query** — `Current : InteractionContext?`
   - **React** — world.out emissions for `InteractionStarted`, `InteractionEnded`, `GiftAccepted`, etc.
   - **Bind** (if useful) — observable collection of recent interactions

**Generalizes beyond NPC interaction.** The suggested state machine decomposition for PlayerWorld:

- **NPC SM** — owns "is the player interacting with an NPC; what items/currency have moved; what was the outcome?" Inputs: `ProcessStartInteraction`, `ProcessDeleteItem`, `ProcessFavorDelta`, `ProcessCurrencyDelta`, quest-related verbs. Outputs: `InteractionStarted`, `InteractionEnded`, `GiftAccepted`, `VendorSold`, `QuestTurnedIn`, ...
- **Map SM** — owns area + position + pins + weather. Inputs: `LOADING LEVEL`, `ProcessMapPinAdd`/`Remove`, `ProcessAddPlayer`, `Initializing area`, `SPAWNING LOCAL PLAYER`, `ProcessMapFx`, weather verbs. Outputs: `PlayerPositionSet`, `MapChanged`, pin CRUD events, weather events. Owns: weather, pins. References: `areas.json` (the SM stores area IDs; consumers resolve to display names at the view layer — closure-consistent).
- **Inventory SM** — owns bag + storage delta ledger. Inputs: `ProcessAddItem`, `ProcessDeleteItem`, `ProcessUpdateItemCode`, `ProcessAddToStorageVault`, `ProcessRemoveFromStorageVault`. Outputs: `InventoryItemAdded`, `InventoryItemRemoved`, `InventoryItemUpdated`, `StorageItemAdded`, `StorageItemRemoved`. Owns: inventory ledger, storage delta ledger. (Storage pre-attach baseline is *not* in the SM — that's in `Mithril.GameReports`, composed at the view layer per the closure principle.)
- **Player SM** — owns effects + attributes + recipes + skills. Inputs: many. Outputs: many (`SkillProgressed`, `RecipeLearned`, `EffectAdded`, `EffectRemoved`, `AttributeChanged`, etc.). Owns: effects, attributes, recipes, skills. References: `effects.json`, `attributes.json` (SM stores identifiers; consumers resolve).
- **Words of Power SM (PlayerWorld side)** — owns word-of-power discovery state. Inputs: `ProcessBook` (or similar discovery verb). Outputs: `WordOfPowerDiscovered`. ChatWorld has its own WoP SM for the spent side; cross-world composition lives in the composition pipeline.
- **Environment SM** — owns time-related world state. Inputs: synthetic clock-tick changes (emitted by dispatch loop infrastructure on world.px), plus log-driven moon-phase verbs. Outputs: `CalendarTimeAdvanced`, `TimeOfDayShift`, `GameDayChange`, `MoonPhaseChange`. Owns: moon, current time-of-day, current game-day. The Environment SM is the canonical worked example of the synthetic-change pattern (see worked example 4).

The principle: identify the world-fact / activity context; one state machine owns it; it emits multiple outcome events derived from the context's evolution. Today's `IGiftSignalService`, `IPlayerInventoryState`, `IPlayerSkillState`, etc. either fold into one of these SMs or stay as today's narrower granularity — the decomposition above is suggestive, not normative; the implementation PR makes the granularity call.

### Observer surface

Verb-keyed canonical dispatch doesn't preclude multi-subscriber instrumentation; observers are a separate channel from canonical handlers. Required for diagnostics, test capture, replay tooling.

**Contract on observers:**

1. **Non-mutating.** An observer must not write to anything the canonical handler reads. Violating this re-introduces cross-handler coupling via the side channel — exactly what the verb-keyed canonical dispatch was designed to prevent.
2. **No claim semantics.** Observers don't suppress the canonical handler or alter the produced frame. Implementable as a literal separate channel or as a chain-with-handled-flag where observers don't set the flag; visible effect is the same.
3. **Ordering by registration**, deterministic. Observers don't depend on each other's side effects in any case; ordering only matters for deterministic test / diag output.

**Two placement points** in the dispatch loop:

- **Pre-canonical-dispatch observers** — see the raw envelope before verb routing. For "I want to know every envelope arrived." Perf tracer counting envelopes/sec; test harness recording every line for replay.
- **Post-canonical-dispatch observers** — see `(envelope, optional change, events emitted on world.out)`. For outcome-focused instrumentation.

Today's ad-hoc `IDiagnosticsSink.Info(...)` calls scattered through producers (`PlayerInventoryFrameProducer:80,92,95,101`; `SkillFrameProducer:81`; every producer carries similar) are doing this in per-producer form. Lifting them into a first-class observer surface at the dispatch loop is strictly cleaner: diag logic is per-envelope-and-per-dispatch-step (not per-producer); test/replay tooling stops having to plug into N transforms to capture everything.

### Composition Pipeline — universal cross-category composition

The composition pipeline is the named abstraction for what was previously called the view layer. Inputs flow into the pipeline on **comp.in** from three or more sources, depending on what's needed for the composition:

- **world.out** — events from PlayerWorld (one stream per world).
- **chatout** — events from ChatWorld (analogous; chat's `world.out`).
- **reportout** — events from `Mithril.GameReports` (snapshot-update notifications).
- **Reference data** — `Mithril.Reference` consumers may read identifier-to-display-name resolution from items.json, areas.json, effects.json, etc. (typically synchronous lookups rather than event subscriptions).

Inside the pipeline, **views** subscribe to whichever streams they need, maintain composed state, and expose Query / React / Bind surfaces to modules. Examples:

- **`IInventoryView`** — composes PlayerWorld's `InventoryItemAdded` events with ChatWorld's stack-observation events using the existing `PendingCorrelator` (Tier-1 cross-source pairing, relocated from inside today's `InventoryView` to inside this view in the composition pipeline). World + world composition.
- **`IStorageView`** — composes PlayerWorld's `StorageItemAdded`/`StorageItemRemoved` events with the latest `Mithril.GameReports` vault snapshot. World + report composition. See Worked example 3.
- **`IWordOfPowerView`** — composes PlayerWorld's `WordOfPowerDiscovered` events with ChatWorld's `WordOfPowerSpent` events (joined by code, no TTL). World + world composition, different join shape from `IInventoryView`.
- **Future views** as they're needed.

The pipeline is **category-agnostic about its inputs** — world + world, world + report, world + reference data are all the same structural pattern at comp.in. Views decide which inputs they compose; the pipeline doesn't enforce a particular shape.

Modules consume views from the composition pipeline (`Subscribe<TViewEvent>(handler)` on the view's Query/React/Bind surface). Modules may also consume world.out directly for events they don't need composition for (e.g., Samwise can consume `InventoryItemAdded` directly from PlayerWorld.out without going through `IInventoryView` if it doesn't need the chat-side stack size).

### Mode flip — single observation, source-reader-anchored

`IsReplay` is stamped at L0.5 (classifier + splitter), forwarded by L1 unchanged. The world's mode flip is the dispatch loop's observation of the first envelope where `IsReplay == false`. Consequences:

- **`IModeAwareFrameProducer<T>` retires entirely.** No per-producer `ReachedLive` Task plumbing.
- **The silent-producer mode-flip wedge fixes structurally.** Under today's per-producer model a silent producer never seeing a non-replay envelope can hold the world's mode-flip; under the rethink the source-reader sees every envelope and the flip is guaranteed once the live tail is reached, regardless of which transform handles it.
- **No new event channel needed.** Per-envelope `IsReplay` is the natural shape; the dispatch loop's "flip on first false" is one observation point per world, anchored at the source.

`IsReplay` is determined as close to L0 as it structurally can be — pushing it into L0 itself would require merging the splitter's snapshot-buffer + live-channel pair into L0, which conflates "tail bytes off disk" with "maintain replay buffer + live channel for late subscribers." The L0/L0.5 split exists for that reason. L0.5 is the right floor.

### Player Log Source — rotation-aware multi-file abstraction

The Player Log Source owns both `Player.log` and `Player-prev.log` and exposes them as a single seekable stream of lines. Necessary because PG rotates its log on startup:

1. PG deletes `Player-prev.log` if it exists
2. PG copies the current `Player.log` to `Player-prev.log`
3. PG creates a fresh empty `Player.log` and writes to it

Without rotation awareness, Mithril's session-replay seed (scanning backward for the most recent `Logged in as character ` / `ProcessAddPlayer(` marker) walks off the start of `Player.log` and falls through to byte 0 of the current file, missing any session content that lives in `Player-prev.log`. Today's `PlayerLogTailReader` is single-file; the Player Log Source generalizes it.

Consumers downstream don't have to know whether a given line came from `Player.log` or `Player-prev.log` — the source exposes the logical concatenation. Sequence numbers are derived from byte offsets within the logical stream (with rotation boundary handling — exact mechanics are an implementation choice; the *interface* is "one stream, monotonic Sequence within the source's lifetime").

## Worked examples

### Worked example 1 — Inventory (single-SM, atomic emission)

Source: `LocalPlayer:` lines containing `ProcessAddItem(instanceId, internalName, slot, bool)`, `ProcessDeleteItem(instanceId)`, `ProcessUpdateItemCode(instanceId, code, bool)`, `ProcessAddToStorageVault(...)`, `ProcessRemoveFromStorageVault(...)`.

```
L0.5: Classifier discriminates → LocalPlayerLogLine envelope (with IsReplay)
L1:   World's single subscription receives the envelope
World dispatch loop (per envelope on world.in):
  1. World Filter: envelope passes (has [HH:MM:SS] prefix)
  2. Clock advances to envelope.Timestamp
  3. Pre-canonical observers see raw envelope (side channel)
  4. Verb-keyed parse: ProcessAddItem → PlayerInventoryAddChange(instanceId, internalName)
  5. Change published on world.px
  6. Inventory SM (subscribed to PlayerInventoryAddChange) receives it:
     - mutates ledger (instanceId → internalName)
     - emits InventoryItemAdded event on world.out
  (NPC SM, Player SM, etc. are not subscribed to this change type; they ignore.)
  7. Synthetic clock-tick change published on world.px
  8. Environment SM (subscribed to clock-tick) receives it:
     - updates current-second tracker
     - emits CalendarTimeAdvanced on world.out if second changed (deduped)
  9. Post-canonical observers see (envelope, change, events emitted)
```

`InventoryItemAdded` flows on world.out. Modules and views consume it directly via typed subscription (`Subscribe<InventoryItemAdded>(handler)`). The cross-source `IInventoryView` in the composition pipeline subscribes to `InventoryItemAdded` from PlayerWorld.out AND the corresponding chat-stack-observation event from ChatWorld.out, composes the two via the existing `PendingCorrelator` shape (the correlator survives the rethink — only its mounting changes from "inside `InventoryView`" to "inside `IInventoryView` in comp pipeline").

### Worked example 2 — NPC interaction (multi-SM polysemy + compound emission)

Source: same `LocalPlayer:` stream. Relevant verbs:

- `ProcessStartInteraction(npc)` → `PlayerStartInteractionChange`
- `ProcessDeleteItem(instanceId)` → `PlayerInventoryDeleteChange`
- `ProcessDeltaFavor(npc, delta)` → `PlayerFavorDeltaChange`
- `ProcessUpdateCurrency(delta)` → `PlayerCurrencyDeltaChange`
- (...quest-related verbs)

Polysemy in action: `PlayerInventoryDeleteChange` is consumed by **both** Inventory SM (for ledger mutation, regardless of why) AND NPC SM (for interaction-outcome detection).

```
Per envelope on world.in:
  1. World Filter, clock advance, observers (as before)
  2. Verb-keyed parse → PlayerStartInteractionChange (or DeleteItem / FavorDelta / etc.)
  3. Change published on world.px

NPC SM (subscribed to: StartInteraction, DeleteItem, FavorDelta, CurrencyDelta, quest verbs):

  On PlayerStartInteractionChange:
    Open context: _current = new Interaction(npc, started=clock.Now)
    Emit on world.out: InteractionStarted(npc, started)

  On PlayerInventoryDeleteChange within an open context:
    Note deleted item: _current.AddDeletion(instanceId)
    (no world.out emission yet — discriminating change not arrived)

  On PlayerFavorDeltaChange within an open context with a deleted item:
    Emit on world.out: GiftAccepted(npc, item, delta)
    Close context with outcome=Gift
    Emit on world.out: InteractionEnded(npc, outcome=Gift, ended=clock.Now)

  On PlayerCurrencyDeltaChange within an open context with a deleted item:
    Emit on world.out: VendorSold(vendor=npc, item, price=delta)
    Close context with outcome=VendorSale
    Emit on world.out: InteractionEnded(npc, outcome=VendorSale, ended=clock.Now)

  On TTL eviction (no discriminating change within window):
    Close context with outcome=Inconclusive
    Emit on world.out: InteractionEnded(npc, outcome=Inconclusive, ended=clock.Now)

Inventory SM also subscribed to PlayerInventoryDeleteChange (polysemy):
  On PlayerInventoryDeleteChange:
    Mutate ledger (remove instanceId)
    Emit on world.out: InventoryItemRemoved(instanceId, eventTimestamp)

Both SMs process the same change independently — no coordination needed.
```

All outcome events flow on world.out. Smaug subscribes to `VendorSold`; Arwen to `GiftAccepted`; future modules to `InteractionStarted` / `InteractionEnded` for lifecycle.

The NPC SM owns:

- **Query surface** — `Current : InteractionContext?` ("are we in an interaction; with whom; what items/currency moved?")
- **React surface** — world.out emissions for all interaction events
- **Bind surface** (optional) — observable collection of recent interactions for UI / telemetry

Today's `IGiftSignalService` (gift-only) folds in. Smaug's vendor calibration migrates to consume `VendorSold` from this SM. Both legacy services retire as separate identities; the unified NPC SM replaces them.

### Worked example 3 — Storage (view-layer composition of world + reports)

The canonical worked example for view-layer composition where the second category is **reports** rather than another world. Same architectural slot as the cross-world `IInventoryView` (PlayerWorld inventory + ChatWorld inventory observations); different second category, same composition shape. Storage is also the canonical worked example of the world-closure principle: deltas are log-observable, so they live inside the world; the pre-attach baseline isn't log-observable, so it lives outside.

**In the world (Inventory SM):**

Subscribes to vault changes on world.px:

- `PlayerStorageVaultAddChange(instanceId, internalName)` — derived from `ProcessAddToStorageVault`
- `PlayerStorageVaultRemoveChange(instanceId)` — derived from `ProcessRemoveFromStorageVault` (already parsed today by `PlayerInventoryFrameProducer:50-51`)

Maintains a vault delta ledger keyed by instance-id. Emits events on world.out:

- `StorageItemAdded(instanceId, internalName, eventTimestamp)`
- `StorageItemRemoved(instanceId, eventTimestamp)`

State is faithful to what the world has observed: vault items added or removed since the world's session-start replay began. Vault contents *before* that point are not in the world's state — PG doesn't emit a vault snapshot at session start, so the world legitimately doesn't know. The world is closed under its source; it doesn't reach into reports for the baseline.

**In `Mithril.GameReports`:**

Independent foundation-layer service consuming the character export's storage section. Exposes the most recent report-derived vault snapshot. Updates via `FileSystemWatcher` when PG writes a fresh export. **No coupling to the world** — the report service is its own category-2 data source, not attached as an input to any world.

**At the composition pipeline (`IStorageView`):**

Composes the two inputs at comp.in:

- Subscribes to the world's `StorageItemAdded` / `StorageItemRemoved` events on world.out
- Subscribes to `Mithril.GameReports`'s snapshot-updated events on reportout
- Maintains "current vault contents" = most recent report snapshot ∘ world deltas since that snapshot's export timestamp
- Exposes the three-surface contract (#707): Query (`TryGetVaultContents()`, `Items`), React (`VaultContentsChanged`), Bind (observable collection)

When a fresh report snapshot arrives, the view rebases — takes the snapshot as the new baseline, re-applies any world deltas whose timestamps follow the snapshot's export time, emits a `VaultContentsRebased` event so subscribers can resynchronize.

**Module consumption pattern:**

- Modules wanting "what's currently in this character's vault" subscribe to `IStorageView`.
- Modules wanting "what was just added to the vault" subscribe directly to the world's `StorageItemAdded` event.
- Both surfaces co-exist; the view doesn't replace the world's direct event surface, it composes on top of it. Same pattern as `IInventoryView` co-existing with PlayerWorld's direct `PlayerInventoryAdded` event subscribers.

**The architectural property worth naming:** the composition pipeline is category-agnostic about its second input. World + world (cross-world composition) and world + report (world + category-2 composition) are the same structural pattern at comp.in — the pipeline's job is to compose, the inputs it composes are determined by what consumers need. The world stays closed; the composition pipeline is the universal composition point.

### Worked example 4 — Environment SM (synthetic clock-tick + world-clock-derived events)

The canonical example of the **synthetic-change** pattern. The Environment SM owns time-related world state and emits every world-clock-derived event, including `CalendarTimeAdvanced` itself. Demonstrates how time events fit the "all world.out events come from state machines" framing without breaking the dispatch loop's clock-first invariant.

**Mechanism:**

The dispatch loop, after advancing the clock, publishes a synthetic `WorldClockTickChange(now)` on world.px (in addition to the verb-keyed change for the envelope's payload, if any). The Environment SM is subscribed to this synthetic change type.

```
Per envelope on world.in:
  1. World Filter, clock advance
  2. (parallel:) synthetic WorldClockTickChange(now) published on world.px
                 verb-keyed change for the envelope's payload published on world.px
  3. Environment SM (subscribed to WorldClockTickChange):
     - If now-second > last-emitted-second:
         Emit on world.out: CalendarTimeAdvanced(now)
         If now crosses a time-of-day shift boundary:
            Emit on world.out: TimeOfDayShift(from, to, now)
         If now crosses the server's daily reset boundary:
            Emit on world.out: GameDayChange(day, now)
     - Update last-emitted-second to now-second
  4. Environment SM also subscribed to PlayerMoonPhaseChange (log-driven moon verb):
     - Update moon-phase state
     - Emit on world.out: MoonPhaseChange(phase, now)
```

**The Environment SM owns:**

- Moon phase (log-derived from moon-phase verbs)
- Current time-of-day band (world-clock-derived)
- Current game-day (world-clock-derived against the server's daily-reset offset — wall-clock-anchored boundary; the world's clock is derived from envelope timestamps, which are wall-clock at L0, so closure is preserved)
- Last-emitted-second (for `CalendarTimeAdvanced` deduplication)

**Query / React / Bind:**

- Query — `Current : EnvironmentState` (current time-of-day, current game-day, current moon phase)
- React — world.out emissions for `CalendarTimeAdvanced`, `TimeOfDayShift`, `GameDayChange`, `MoonPhaseChange`
- Bind — usually not needed for time state; could expose an observable for current-time display

**Why this pattern:**

- Keeps "all world.out events come from state machines" uniform. Time events aren't a dispatch-loop infrastructure emission with its own special case; they're the Environment SM's emissions in response to a synthetic change.
- The synthetic clock-tick change pattern generalizes if any future infrastructure-driven inputs to state machines surface (none today; available if needed).
- Game-day discrimination is wall-clock-anchored (PG's daily reset is at a specific wall-clock time, offset from midnight). The Environment SM does this discrimination against envelope timestamps (which carry wall-clock at L0), not against `TimeProvider.System.GetUtcNow()` — closure preserved, replay-deterministic.
- Today's `WorldClockTickProducer` + `WorldClockTickFolder` + `TimeOfDayShiftComposer` chain (#644 ratified) folds into this single Environment SM. The #644 ratification's *outcome* (explicit clock-tick ownership; no implicit advancement via folder-irrelevant frames; `CalendarTimeAdvanced` and `TimeOfDayShift` emitted explicitly) survives; the *mechanism* (a producer-folder-composer chain pretending to be peer producers alongside the others) is replaced by one Environment SM consuming a synthetic change.

**Gandalf consumes Environment SM events for scheduler-collapse work (#613) the same way it would have consumed the world-clock-tick-producer chain — same event types on world.out, different emission site. Module side unchanged.**

## What survives, what retires

| Surviving (unchanged or near-unchanged) | Retiring (replaced by rethink) |
|---|---|
| `IWorldClock` and `WorldMode` | `IFrameProducer<T>` (IAsyncEnumerable contract) |
| `IWorldEventBus` (world.out surface) | `IModeAwareFrameProducer<T>` (per-producer ReachedLive) |
| Three-surface Query/React/Bind contract for state machines (#707) | Merger machinery in `PlayerWorld` / `ChatWorld` (~150 lines each: `RunMergerAsync`, `Compare`, `UpdateModeIfReady`, `ProducerAdapter<T>`, `ProducerRuntimeState`) |
| `Mithril.GameReports` / `Mithril.Reference` / composition pipeline (cross-category composition) | `IWorld.RegisterProducer` + `IWorld.StartMerger` (renamed to `StartDispatch` or similar) |
| L0 / L0.5 / L1 stack (unchanged) | `WorldClockTickProducer` + `WorldClockTickFolder` + `TimeOfDayShiftComposer` (all fold into Environment SM consuming synthetic clock-tick changes) |
| Per-frame DAG resolution (principle 11) — applies to event flow within an envelope's processing | `IFolder<T>` (collapses into `IStateMachine` — single uniform interface for state machines that subscribe to changes on world.px and emit events on world.out) |
| Folder-emitted change events + composer-emitted domain frames on the world bus (#643) — survive in concept, both now uniformly called "events" emitted by state machines on world.out | `IComposer.Observe(eventPayload, clock) → IReadOnlyList<IFrame>` (collapses into `IStateMachine`'s event emission; no separate composer interface) |
| | Per-producer `IDiagnosticsSink.Info(...)` calls inside producer subscribe callbacks (lift to observer surface; follow-on cleanup) |
| | `IGiftSignalService` (gift-only) — folds into NPC SM as one of its outcome-event channels |
| | The 8 producer files in `Mithril.GameState/*/Producers/` and `Mithril.WorldSim.{Player,Chat}/Producers/` (~800 lines total — replaced by SMs that subscribe to changes on world.px and own their parsing) |
| | The merger's "every producer must have a head" invariant + cross-producer synchronization |
| | The `WorldMergerStartHostedService` (renamed but kept; trailing-start ordering invariant from Call 2 still applies, just to a renamed seam) |
| | Folder/composer terminology distinction (collapsed into uniform "state machine") |

**New (introduced by rethink):**

- **`world.in` / `world.px` / `world.out` / `comp.in`** — named channels with clear directional roles. world.in: envelopes into the world. world.px: changes within the world (state-machine inputs). world.out: events out of the world (state-machine outputs). comp.in: composition pipeline input (world.out + chatout + reportout).
- **Composition Pipeline** — formalized; the universal composition point for cross-category state. Hosts views that subscribe to comp.in from one or more sources.
- **`IStateMachine` (working name)** — single uniform interface replacing `IFolder<T>` + `IComposer`. Declares change-type subscriptions (`Subscribes : IReadOnlyCollection<Type>`); processes changes synchronously (`Observe(changePayload, clock)`); emits events on world.out.
- **`IFrameTransform<TEnvelope, TChange>`** — verb-keyed transformer at L2 (envelope → change). Synchronous; at-most-one transform per envelope (PG verbs mutually exclusive).
- **`IEnvelopeSource<TEnvelope>`** — thin abstraction over the source: `Subscribe(handler) → IDisposable` with per-envelope `IsReplay`. PlayerWorld over `ILogStreamDriver.Subscribe<IClassifiedPlayerLogLine>`; ChatWorld over `ChatLogReplaySource`.
- **Synthetic clock-tick change** — dispatch loop publishes `WorldClockTickChange(now)` on world.px when the clock advances. Environment SM subscribes; emits time-related events. Pattern available for any future infrastructure-driven SM inputs.
- **Observer surface** — pre- and post-canonical-dispatch hook points on `IEnvelopeSource` or on the world; non-mutating, no-claim contract.
- **Suggested state machine decomposition** (PlayerWorld): NPC, Map, Inventory, Player, Words of Power, Environment. Decomposition is suggestive, not normative; the implementation PR makes the granularity call.

## Sizing and phasing

**World-sim has not shipped to end users.** Module code is malleable, not load-bearing. The phasing decision is therefore about sequential development clarity, not about preserving module compatibility.

**Option A — single PR sweep.** Foundation rethink + `INpcInteractionStateMachine` + Smaug/Arwen consumer migration in one PR. ~4–6 days. Cleanest final state on landing; whole architectural shape arrives intact.

- Net ~-1500 to -2000 LoC. Producer files (~800 lines across 8 files) mostly delete; merger machinery (~150 lines each in `PlayerWorld` / `ChatWorld`) deletes; producer-adapter boxing trio (~70 lines per world) deletes. New: ~30-50 lines of dispatch loop per world, ~200-300 lines for `INpcInteractionStateMachine` + consumer migrations.
- Test rewrite: `tests/Mithril.WorldSim.Player.Tests` and `tests/Mithril.WorldSim.Chat.Tests` lose merger-shape tests (priority tie-breaks, "every producer has a head" mode-aware quorum); gain per-envelope dispatch tests. Verb-parsing tests survive as transform tests. State-machine behavior tests (Inventory SM, NPC SM, Environment SM, etc.) replace today's folder/composer test surface.

**Option B — two-PR sequential.** Phase 1 lands the foundation rethink (merger gone, transforms, observers, internal pipe / external bus split, dispatch loop contract); Phase 2 lands `INpcInteractionStateMachine` + Smaug/Arwen migration. ~2-3 days each.

- Reviewer comprehension benefit: smaller per-PR surface; foundation change reviewable independently of state-machine decomposition / consolidation.
- Interim state during Phase 1's life: `IGiftSignalService` still exists in gift-only form; vendor-sale doesn't have its proper home yet; module code mostly unchanged.

Either option works. The single-PR sweep is probably cleaner now that the module-constraint framing is dropped (the "modules untouched" virtue of Phase 1 wasn't actually a virtue — it was treating module changes as a cost they aren't).

**Risk:** medium. The honest concern is the per-envelope dispatch loop sequence becoming a load-bearing contract: clock-first, transform-second, internal-pipe-publish-third, etc. The doc + an integration test pinning the order is the only place where a sweep PR could leave a subtle regression.

## Doc amendments owed for the rethink PR

When the implementation PR ships, [`world-simulator.md`](world-simulator.md) needs surgical edits. Checklist:

- [ ] **Principle 1 rewrite.** From *"each world is a timestamp-ordered merger over its N producers"* → *"each world consumes envelopes from world.in (a single subscription to its source), parses each envelope into a change, publishes the change on world.px to subscribed state machines, and emits state-machine-derived events on world.out. Observers may fan out from the dispatch loop for diagnostics / test / replay without participating in canonical dispatch."*
- [ ] **Principle 4 generalization.** Extend the existing principle 4 ("no service spans both Player.log and chat") to its broader form: *"the world is closed under any cross-category input — reports, reference data, community calibration, other worlds. Composition across categories lives in the composition pipeline, not inside the world."* The original cross-source-log rule becomes a specific instance of this broader closure commitment.
- [ ] **Principle 10 collapse — folders/composers/producers retire.** The three-state-machine-kinds taxonomy collapses. There is no producer concept (replaced by `IFrameTransform` + `IEnvelopeSource` at L2). There is no folder-vs-composer distinction (collapsed into uniform `IStateMachine` consuming changes on world.px and emitting events on world.out). New principle 10: *"A state machine within the world subscribes to specific change types on world.px (typed pub-sub), maintains state for the world-fact it owns, and emits domain events on world.out. State machines are decomposed by the world-fact they own, not by the output event they emit."*
- [ ] **§Three categories of data — "Vault items — the canonical case that requires GameReports" paragraph.** Wrong as written. The vault verbs (`ProcessAddToStorageVault` / `ProcessRemoveFromStorageVault`) ARE in the log; today's `PlayerInventoryFrameProducer:50-51` already parses `ProcessRemoveFromStorageVault`. The corrected framing: *"Vault contents at world-attach are not observable in the log — PG doesn't emit a vault snapshot at session start. The world observes vault deltas via the storage-vault verbs; the pre-attach baseline comes from `Mithril.GameReports`. The composition lives in `IStorageView` at the composition pipeline, same architectural slot as cross-world views — and the canonical worked example of comp-pipeline composition across world + reports."*
- [ ] **New section — Layer-wide invariant.** Document the L0–L2 single-output, L3 fan-out commitment. L3 fan-out is empirical (multiple state machines on world.px subscribe to the same change; multiple consumers on world.out subscribe to the same event; multiple views in the composition pipeline compose the same world.out / chatout / reportout streams).
- [ ] **Contracts section.**
  - Retire `IFolder<T>`, `IComposer`, `IFrameProducer<T>`, `IModeAwareFrameProducer<T>` interfaces.
  - Retire `IWorld.RegisterProducer`; rename `IWorld.StartMerger` → `IWorld.StartDispatch`.
  - Introduce `IStateMachine` (uniform state-machine interface), `IFrameTransform<TEnvelope, TChange>` (verb-keyed L2 parsers), `IEnvelopeSource<TEnvelope>` (world.in abstraction).
  - Document the per-envelope dispatch loop contract (envelope on world.in → filter → clock-advance → parse to change → publish on world.px → state machines emit events on world.out).
  - Define the synthetic clock-tick change pattern (dispatch loop emits `WorldClockTickChange` on world.px; Environment SM derives all time-related events from it).
  - Define the observer surface contract (non-mutating; no-claim; pre and post placement; registration ordering).
- [ ] **Vocabulary section.** Retire "Producer," "Merger," "WorldClockTickProducer," "synthetic-frame producer," "Folder," "Composer," "Change event vs domain frame." Introduce "Transform," "EnvelopeSource," "world.in / world.px / world.out / comp.in," "State Machine," "Composition Pipeline," "Synthetic clock-tick change," "Observer surface," "World-fact decomposition," "Polysemous change."
- [ ] **Decisions ratified post-#642 — #644 section.** Mark superseded with pointer to this rethink doc and #800. The producer-folder-composer chain for clock-tick collapses into the Environment SM consuming a synthetic clock-tick change. Leave reasoning intact for trail legibility.
- [ ] **Mode-flip section.** Add explicit layer attribution: *"`IsReplay` is stamped at L0.5 (classifier + splitter) and forwarded by L1. The world's mode flip is the dispatch loop's observation of the first envelope where `IsReplay == false`; no per-producer plumbing remains."*
- [ ] **Layered architecture diagram.** "Producers" boxes/arrows retire. Show: Player Log Source → world.in → World Filter → parse → world.px → state machines → world.out → comp.in → composition pipeline → modules. Player Log Source spans both `Player.log` and `Player-prev.log` as one seekable stream.
- [ ] **Worked example 1 (Inventory)** in `world-simulator.md` rewrites to the state-machine shape: Inventory SM subscribes to inventory changes on world.px; emits events on world.out.
- [ ] **New worked examples.** Add NPC interaction SM (compound emission + polysemy), Storage SM (closure + comp-pipeline composition), Environment SM (synthetic clock-tick change pattern).
- [ ] **Composer-decomposition guidance** rewrites to **State machine decomposition guidance**: *"Decompose state machines by the world-fact they own, not by the output event they emit. When N outcomes share a common context, one SM owns the context and emits all N outcomes. When an outcome is solo and context-free, a single-purpose SM is fine."*
- [ ] **Player Log Source as an L0 concept.** Today's L0 (`LogSourceTailer` + `PlayerLogTailReader`) is single-file; the rethink makes it span both `Player.log` and `Player-prev.log` as one seekable stream. PG's startup rotation (delete prev, copy current to prev, create new) means a single Mithril session can need to read across the rotation boundary — today's seed strategy doesn't handle this. The Player Log Source abstraction owns the rotation-aware behavior.
- [ ] **`docs/glossary.md`.** Mirror the vocabulary updates. Add "world.in / world.px / world.out / comp.in," "State Machine," "Composition Pipeline," "Polysemous change," "Synthetic clock-tick change," "World-fact decomposition." Retire "Producer," "Folder," "Composer" entries (or mark them as superseded).
- [ ] **`docs/world-sim-migration-audit.md`.** Synthetic-frame-producer references swap to "absorbed into Environment SM via synthetic clock-tick change" pointing at this doc. Audit otherwise historical; doesn't need full reworking.
- [ ] **`docs/cross-source-correlation.md`.** Tier-1 cross-source correlator (`PendingCorrelator`) survives unchanged in concept; it relocates from "inside `InventoryView`" to "inside the composition pipeline's `IInventoryView` view." Doc may need a minor pointer-update; tier hierarchy itself is unaffected.
- [ ] **`docs/module-signal-map.md`.** No structural change. Module-signal-map's tables of "modules consume X service" still hold; the underlying services are now state machines instead of folders/composers.

## Cross-references

- **[#800](https://github.com/moumantai-gg/mithril/issues/800)** — issue body + comment thread. Original proposal in body; ratified refinements in comments. This doc consolidates the ratified state.
- **[#799](https://github.com/moumantai-gg/mithril/pull/799)** — the splitter unbounded-channel fix that surfaced the structural critique.
- **[#643](https://github.com/moumantai-gg/mithril/issues/643)** — change events on world bus (ratified); compatible in concept (events flow on world.out for module consumption), but the folder/composer distinction the original framing assumed collapses under this rethink. Both kinds of emission become uniform "events emitted by state machines."
- **[#644](https://github.com/moumantai-gg/mithril/issues/644)** — explicit clock-tick owner (ratified); outcome survives, mechanism superseded by Environment SM consuming a synthetic clock-tick change.
- **[#707](https://github.com/moumantai-gg/mithril/issues/707)** — three-surface (Query/React/Bind) contract; applies uniformly to state machines under this rethink (no folder-vs-composer distinction in surface).

---

**Open for discussion:** any pushback on the world-fact decomposition principle, the internal-pipe vs external-bus split, or the single-PR-sweep vs phased sizing.
