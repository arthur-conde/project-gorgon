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
| **L2** | Verb-keyed dispatch + transform parsing | One envelope in → at most one transform → at most one `Frame<T>` out. Linear under verb-keyed routing (next section). |
| **L3** | Folders + change events + composers + views + modules | **Genuine fan-out, by data.** Multiple real subscribers per change event today (`PlayerInventoryAdded`, `CalendarTimeAdvanced`, view-emitted domain frames — all have N>1 consumers empirically, not hypothetically). Fan-out earns its keep here because the system has multiple genuine observers of the same event. |

The fan-out shape was always architecturally correct at L3. The previous "fan-out at the transform stage" framing was conflating L3's empirical fan-out with an L1→L2 abstraction borrowing the shape one layer too high. Verb-keyed routing at L2 + fan-out at L3 is the architecturally honest split.

### World closure — the world is closed under its source

A generalization of [`world-simulator.md`](world-simulator.md) principle 4. The world accepts envelopes from its source stream only. Cross-category inputs — `Mithril.Reference`, `Mithril.GameReports`, `ICommunityCalibrationService`, other worlds — do **not** flow into the world's state machines. Composition with those categories lives at the view layer, not inside the world.

The original principle 4 ("no service spans both Player.log and chat") was a specific instance of this rule with both inputs being log sources. The broader commitment: **the world is closed under any cross-category input**, not just cross-source-log. A state machine inside the world consuming a category-2 source (e.g., an Inventory SM reading report snapshots for vault contents) is the same shape of seal-break as a state machine consuming two log sources — it just uses a different category as the second input.

Consequences:

- **The world's state for any concept is whatever is derivable from its source stream alone.** Incompleteness is faithful, not a gap to paper over. Vault state pre-attach is unknown to the world because PG doesn't emit a vault snapshot at session start; that's what the world's view should faithfully reflect.
- **Consumers needing "complete current state" across categories go to a view.** Views are the universal composition point per principle 4 — composing world + world, world + report, world + reference data, world + community calibration.
- **Reports / reference / calibration services consume their own data sources independently.** They are not attached as inputs to a world; their output is consumed at the view layer.

The vault example (worked example 3 below) is the canonical case: vault deltas are log-observable (`ProcessAddToStorageVault` / `ProcessRemoveFromStorageVault`); the world owns those deltas; the pre-attach baseline lives in `Mithril.GameReports`; the composition lives in a view.

### Per-envelope dispatch loop contract

For each envelope arriving from the world's single subscription to its source (`IEnvelopeSource<TEnvelope>`):

1. **Clock advances** to the envelope's timestamp.
2. **`CalendarTimeAdvanced(now)` fires** on the external bus if the simulated second changed since the previous emission (deduped per simulated second, not per envelope).
3. **Pre-canonical-dispatch observers see the raw envelope** (side channel; see Observer surface below). No effect on canonical dispatch.
4. **Dispatch loop discriminates the envelope** (by line-kind first if the world subscribes to the unified classified stream, then by verb prefix / payload discriminator) and looks up the at-most-one registered transform for that key. If a transform is registered, it runs synchronously and returns `Frame<TPayload>?`. If no transform is registered for the discriminated key, no canonical work happens for this envelope.
5. **For the emitted frame (if any):**
   - Folder routed by payload type applies the frame; mutates world state; may emit change events on the external bus.
   - World publishes the frame to the **internal pipe** for composer consumption.
   - Composers subscribed to that frame type on the internal pipe read it; maintain their own state; may emit events on the external bus when their pattern is recognized.
6. **Post-canonical-dispatch observers** see `(envelope, optional emitted frame, optional change events / composer events produced)` (side channel).

**Contract notes:**

- "Clock first" is uniform: both the value (`clock.Now`) and the announcement (`CalendarTimeAdvanced`) precede any state-machine work observing them. A folder reading `clock.Now` during `Apply` sees the envelope's timestamp; a `CalendarTimeAdvanced` subscriber sees the tick before any change events whose timestamps belong to it.
- **No "transforms in registration order" contract is owed.** PG's verbs are mutually exclusive at the line level; no envelope matches more than one transform. If overlapping transforms ever surface, the natural fix is "merge into one transform that emits the right frame type," not "let registration order pick a winner."
- `CalendarTimeAdvanced` deduplication is **per simulated second**, not per envelope. Many envelopes within the same second emit no tick; the second-boundary envelope emits one.
- The dispatch loop's emission order on the bus within a single envelope's resolution is determined by the dispatch graph's topological order, fixed at world-registration time via `IComposer.Subscribes`. Subscribers see events in deterministic order across runs.

### Internal pipe vs external bus

Two distinct channels with distinct semantic roles:

- **Internal pipe** — carries `Frame<T>` for composer consumption. Composers subscribe to frame types they care about; the pipe fans out frames to all subscribed composers in dispatch-graph topological order. Not exposed to modules.
- **External bus** — carries events for module / view / observer consumption. Both folders and composers emit here. Folders emit *change events* post-Apply (`PlayerInventoryAdded`, `PlayerSkillProgressed`, etc.). Composers emit *outcome events* / domain frames (`GiftAccepted`, `VendorSold`, `CalendarTimeAdvanced`, etc.). Modules subscribe to specific typed channels.

The split is structurally clean:

- **Internal pipe direction** — flows from dispatch loop *down* into composers within the world.
- **External bus direction** — flows from folders + composers *out* to consumers (modules, views, observers).
- No bidirectional traffic on either channel.

### Composers read frames, not change events

A key refinement: composers consume **raw frames from the internal pipe**, not folder-emitted change events from the external bus. Concretely:

- Folder for `PlayerInventoryDeleteFrame` advances inventory ledger and emits `PlayerInventoryRemoved` change event on external bus (for module consumption).
- Gift composer reads the same `PlayerInventoryDeleteFrame` from internal pipe (NOT the folder's `PlayerInventoryRemoved` change event).

**Why:** composers should be *self-contained*. Each composer owns its own state and its own resolution; folders shouldn't owe enrichment to downstream pattern recognizers. The folder's contract narrows to "frame in → state mutation"; the "frame in → state mutation + enrichment for downstream consumers" coupling dissolves.

This is the same shape `IGiftSignalService` already implements today (the post-#688 Tier-2 signal service that owns its own L1 subscription and verb-triple correlation, with its own `instanceId → InternalName` resolution map). The rethink generalizes that pattern.

### Frames are polysemous; semantic inference is composer-side

A direct consequence of "composers read frames": *the same frame can mean different things to different composers, and the folder doesn't classify which.* Examples:

- `PlayerStartInteractionFrame(npc)` — vendor open / gift target / quest dialogue / NPC chat / training menu. ≥4 composer interpretations.
- `PlayerInventoryDeleteFrame(instanceId)` — gift / vendor sale / vault deposit / item destroy / quest turn-in / recipe-craft consume / food consume. ≥6 composer interpretations.
- `PlayerCurrencyDeltaFrame(amount)` — vendor sale / vendor purchase / quest reward / council found / training cost. Multiple.

The inventory folder mutates the ledger for every `PlayerInventoryDeleteFrame` regardless of *why* the item was deleted. Composers downstream interpret the *meaning* from co-occurring frames. Separating "state mutation" (folder's job) from "semantic inference" (composer's job) is what enables N composers to interpret the same frame N different ways without coordination.

### World-fact decomposition for composers

**Decompose composers by the world-fact they own, not by the output event they emit.** When multiple semantic outcomes share a common context (NPC interaction, crafting, combat, travel), one state machine owns the context and emits all outcome events. When an outcome is solo and context-free, a single-purpose composer is fine.

The canonical example: `INpcInteractionStateMachine` owns "is the player currently in an NPC interaction, and with whom?" and emits:

- `InteractionStarted(npc)` — interaction began
- `InteractionEnded(npc, outcome)` — interaction ended; outcome ∈ {gift, vendor sale, quest turn-in, no-op, ...}
- `GiftAccepted(npc, item, favorDelta)` — discriminated outcome
- `VendorSold(vendor, item, price)` — discriminated outcome
- `QuestTurnedIn(quest, npc)` — discriminated outcome
- (...additional outcome events as PG's verb vocabulary surfaces them)

It subscribes to the union of relevant frames (`PlayerStartInteractionFrame`, `PlayerInventoryDeleteFrame`, `PlayerFavorDeltaFrame`, `PlayerCurrencyDeltaFrame`, etc.) on the internal pipe. The shared prefix (`StartInteraction + ItemDelete`) is *interaction context*, a real world fact. Outcome events are derived from the context's evolution upon arrival of the discriminating frame.

Why this is the right decomposition:

1. **The state being tracked is a real world fact, not just composer bookkeeping.** "Player is interacting with NPC X" is a property of the simulated world. Three composers each maintaining their own copy of this fact is bookkeeping duplication of a real datum.
2. **Outcomes are mutually exclusive within an interaction.** One state machine naturally enforces this; three independent composers each watching the shared prefix works only because the discriminating frames happen to be mutually exclusive in practice — the architecture doesn't enforce it.
3. **The interaction lifecycle is itself a meaningful surface.** `InteractionStarted` / `InteractionEnded` are useful regardless of outcome (UI cue, combat-readiness gating, telemetry). One state machine surfaces them; outcome-specific composers wouldn't.
4. **Aligns with the three-surface (Query/React/Bind) contract from [#707](https://github.com/moumantai-gg/mithril/issues/707).** The state machine exposes:
   - **Query** — `Current : InteractionContext?`
   - **React** — bus emissions for `InteractionStarted`, `InteractionEnded`, `GiftAccepted`, etc.
   - **Bind** (if useful) — observable collection of recent interactions

The `IComposer` interface already supports this — `Observe(eventPayload, clock) → IReadOnlyList<IFrame>` is generic across input event types and across output frame types. One composer can subscribe to N frame types and emit M event types. No interface change needed.

**Generalizes beyond NPC interaction.** Other world-fact-decomposable contexts:

- **Crafting state machine** — owns "player is crafting recipe R"; emits `CraftStarted`, `CraftSucceeded(item)`, `CraftFailed(reason)`, `CraftCanceled`.
- **Combat state machine** — owns "player is in combat with targets {T1, T2, ...}"; emits `CombatEntered`, `EnemyDefeated`, `PlayerDied`, `CombatExited`.
- **Travel state machine** — owns "player is portalling / waypointing"; emits `TravelStarted`, `TravelCompleted`.

The principle: identify the world-fact / activity context; one state machine owns the context; it emits multiple outcome events derived from the context's evolution. Today's `IGiftSignalService` is the gift-only special case; the rethink generalizes.

### Observer surface

Verb-keyed canonical dispatch doesn't preclude multi-subscriber instrumentation; observers are a separate channel from canonical handlers. Required for diagnostics, test capture, replay tooling.

**Contract on observers:**

1. **Non-mutating.** An observer must not write to anything the canonical handler reads. Violating this re-introduces cross-handler coupling via the side channel — exactly what the verb-keyed canonical dispatch was designed to prevent.
2. **No claim semantics.** Observers don't suppress the canonical handler or alter the produced frame. Implementable as a literal separate channel or as a chain-with-handled-flag where observers don't set the flag; visible effect is the same.
3. **Ordering by registration**, deterministic. Observers don't depend on each other's side effects in any case; ordering only matters for deterministic test / diag output.

**Two placement points** in the dispatch loop:

- **Pre-canonical-dispatch observers** — see the raw envelope before verb routing. For "I want to know every envelope arrived." Perf tracer counting envelopes/sec; test harness recording every line for replay.
- **Post-canonical-dispatch observers** — see `(envelope, optional emitted frame, change events / composer events produced)`. For outcome-focused instrumentation.

Today's ad-hoc `IDiagnosticsSink.Info(...)` calls scattered through producers (`PlayerInventoryFrameProducer:80,92,95,101`; `SkillFrameProducer:81`; every producer carries similar) are doing this in per-producer form. Lifting them into a first-class observer surface at the dispatch loop is strictly cleaner: diag logic is per-envelope-and-per-dispatch-step (not per-producer); test/replay tooling stops having to plug into N transforms to capture everything.

### Mode flip — single observation, source-reader-anchored

`IsReplay` is stamped at L0.5 (classifier + splitter), forwarded by L1 unchanged. The world's mode flip is the dispatch loop's observation of the first envelope where `IsReplay == false`. Consequences:

- **`IModeAwareFrameProducer<T>` retires entirely.** No per-producer `ReachedLive` Task plumbing.
- **The silent-producer mode-flip wedge fixes structurally.** Under today's per-producer model a silent producer never seeing a non-replay envelope can hold the world's mode-flip; under the rethink the source-reader sees every envelope and the flip is guaranteed once the live tail is reached, regardless of which transform handles it.
- **No new event channel needed.** Per-envelope `IsReplay` is the natural shape; the dispatch loop's "flip on first false" is one observation point per world, anchored at the source.

`IsReplay` is determined as close to L0 as it structurally can be — pushing it into L0 itself would require merging the splitter's snapshot-buffer + live-channel pair into L0, which conflates "tail bytes off disk" with "maintain replay buffer + live channel for late subscribers." The L0/L0.5 split exists for that reason. L0.5 is the right floor.

## Worked examples

### Worked example 1 — Inventory (folder-only case)

Source: `LocalPlayer:` lines containing `ProcessAddItem(instanceId, internalName, slot, bool)`, `ProcessDeleteItem(instanceId)`, `ProcessUpdateItemCode(instanceId, code, bool)`, `ProcessRemoveFromStorageVault(...)`.

```
L0.5: Classifier discriminates → LocalPlayerLogLine envelope (with IsReplay)
L1:   Forwarded to world's unified-pipe subscription
World dispatch loop:
  1. Clock advances
  2. CalendarTimeAdvanced fires if second changed
  3. Verb-keyed lookup: ProcessAddItem → PlayerInventoryAddTransform
  4. Transform returns Frame<PlayerInventoryAddFrame>(timestamp, addPayload)
  5. PlayerInventoryFolder.Apply(frame, clock):
     - mutates ledger (instanceId → internalName)
     - emits PlayerInventoryAdded change event on external bus
  6. World publishes Frame<PlayerInventoryAddFrame> to internal pipe
  7. Composers subscribed to PlayerInventoryAddFrame on internal pipe see it
     (e.g., InventoryEventTracker composer if one wants to compose stack-tracking, etc.)
```

`PlayerInventoryAdded` flows on the external bus. Modules and views consume it directly. No view-layer composition needed for the Player.log side (the cross-source `InventoryView` joins Player.log with chat at a layer above the world; it subscribes to `PlayerInventoryAdded` AND `ChatInventoryObserved` from both worlds' external buses).

### Worked example 2 — NPC interaction state machine (composer with multiple outcomes)

Source: same `LocalPlayer:` stream. Relevant verbs:

- `ProcessStartInteraction(npc)` → `PlayerStartInteractionFrame`
- `ProcessDeleteItem(instanceId)` → `PlayerInventoryDeleteFrame`
- `ProcessDeltaFavor(npc, delta)` → `PlayerFavorDeltaFrame`
- `ProcessUpdateCurrency(delta)` → `PlayerCurrencyDeltaFrame`
- (...quest-related verbs)
- `ProcessEndInteraction(npc)` (or implicit end via new interaction start)

```
Each frame:
  - Goes to its folder (interaction folder, inventory folder, favor folder, currency folder, ...)
  - Folder advances its own state; may emit change event on external bus
  - World publishes the frame to internal pipe

INpcInteractionStateMachine (composer on internal pipe):
  Subscribes : { PlayerStartInteractionFrame, PlayerInventoryDeleteFrame,
                 PlayerFavorDeltaFrame, PlayerCurrencyDeltaFrame, ... }

  On PlayerStartInteractionFrame:
    Open context: _current = new Interaction(npc, started=clock.Now)
    Emit: InteractionStarted(npc, started)

  On PlayerInventoryDeleteFrame within an open context:
    Note deleted item in context: _current.AddDeletion(instanceId)
    (no emission yet — discriminating frame not arrived)

  On PlayerFavorDeltaFrame within an open context with a deleted item:
    Emit: GiftAccepted(npc, item, delta)
    Close context with outcome=Gift
    Emit: InteractionEnded(npc, outcome=Gift, ended=clock.Now)

  On PlayerCurrencyDeltaFrame within an open context with a deleted item:
    Emit: VendorSold(vendor=npc, item, price=delta)
    Close context with outcome=VendorSale
    Emit: InteractionEnded(npc, outcome=VendorSale, ended=clock.Now)

  On TTL eviction (no discriminating frame within window):
    Close context with outcome=Inconclusive
    Emit: InteractionEnded(npc, outcome=Inconclusive, ended=clock.Now)
```

All outcome events flow on the external bus. Smaug subscribes to `VendorSold`; Arwen to `GiftAccepted`; future modules to `InteractionStarted` / `InteractionEnded` for lifecycle.

The state machine owns:

- **Query surface** — `Current : InteractionContext?` ("are we in an interaction; with whom; what items got noted?")
- **React surface** — bus emissions for all outcome events
- **Bind surface** (optional) — observable collection of recent interactions for UI / telemetry

`IGiftSignalService`'s gift-only logic folds in. Smaug's vendor calibration migrates to consume `VendorSold` from this state machine. Both legacy services retire as separate identities; the unified state machine replaces them.

### Worked example 3 — Storage (view-layer composition of world + reports)

The canonical worked example for view-layer composition where the second category is **reports** rather than another world. Same architectural slot as the cross-world `IInventoryView` (PlayerWorld inventory + ChatWorld inventory observations); different second category, same composition shape. Storage is also the canonical worked example of the world-closure principle: deltas are log-observable, so they live inside the world; the pre-attach baseline isn't log-observable, so it lives outside.

**In the world (Inventory SM):**

Subscribes to vault verbs on the internal pipe:

- `PlayerStorageVaultAddFrame(instanceId, internalName)` — derived from `ProcessAddToStorageVault`
- `PlayerStorageVaultRemoveFrame(instanceId)` — derived from `ProcessRemoveFromStorageVault` (already parsed today by `PlayerInventoryFrameProducer:50-51`)

Maintains a vault delta ledger keyed by instance-id. Emits change events on worldout:

- `StorageItemAdded(instanceId, internalName, eventTimestamp)`
- `StorageItemRemoved(instanceId, eventTimestamp)`

State is faithful to what the world has observed: vault items added or removed since the world's session-start replay began. Vault contents *before* that point are not in the world's state — PG doesn't emit a vault snapshot at session start, so the world legitimately doesn't know. The world is closed under its source; it doesn't reach into reports for the baseline.

**In `Mithril.GameReports`:**

Independent foundation-layer service consuming the character export's storage section. Exposes the most recent report-derived vault snapshot. Updates via `FileSystemWatcher` when PG writes a fresh export. **No coupling to the world** — the report service is its own category-2 data source, not attached as an input to any world.

**At the view layer (`IStorageView`):**

Composes the two inputs:

- Subscribes to the world's `StorageItemAdded` / `StorageItemRemoved` events on worldout
- Subscribes to `Mithril.GameReports`'s snapshot-updated events
- Maintains "current vault contents" = most recent report snapshot ∘ world deltas since that snapshot's export timestamp
- Exposes the three-surface contract (#707): Query (`TryGetVaultContents()`, `Items`), React (`VaultContentsChanged`), Bind (observable collection)

When a fresh report snapshot arrives, the view rebases — takes the snapshot as the new baseline, re-applies any world deltas whose timestamps follow the snapshot's export time, emits a `VaultContentsRebased` event so subscribers can resynchronize.

**Module consumption pattern:**

- Modules wanting "what's currently in this character's vault" subscribe to `IStorageView`.
- Modules wanting "what was just added to the vault" subscribe directly to the world's `StorageItemAdded` event.
- Both surfaces co-exist; the view doesn't replace the world's direct event surface, it composes on top of it. Same pattern as `IInventoryView` co-existing with PlayerWorld's direct `PlayerInventoryAdded` event subscribers.

**The architectural property worth naming:** the view layer is category-agnostic about its second input. World + world (cross-world composition) and world + report (world + category-2 composition) are the same structural pattern — the view's job is to compose, the inputs it composes are determined by what consumers need. The world stays closed; the view layer is the universal composition point.

## What survives, what retires

| Surviving (unchanged or near-unchanged) | Retiring (replaced by rethink) |
|---|---|
| `Frame<T>` and `IFrame` — dispatch envelope | `IFrameProducer<T>` (IAsyncEnumerable contract) |
| `IFolder<T>` — payload-typed state mutator | `IModeAwareFrameProducer<T>` (per-producer ReachedLive) |
| `IComposer.Observe(eventPayload, clock) → IReadOnlyList<IFrame>` | Merger machinery in `PlayerWorld` / `ChatWorld` (~150 lines each: `RunMergerAsync`, `Compare`, `UpdateModeIfReady`, `ProducerAdapter<T>`, `ProducerRuntimeState`) |
| `IWorldClock` and `WorldMode` | `IWorld.RegisterProducer` + `IWorld.StartMerger` (renamed to `StartDispatch` or similar) |
| `IWorldEventBus` — external bus surface | `WorldClockTickProducer` + `WorldClockTickFolder` (clock advancement absorbs into dispatch loop) |
| Folder-emitted change events on external bus (#643) | Per-producer `IDiagnosticsSink.Info(...)` calls inside producer subscribe callbacks (lift to observer surface; follow-on cleanup) |
| Composer-emitted domain frames on external bus (#643) | `IGiftSignalService` (gift-only) — folds into generalized `INpcInteractionStateMachine` |
| Three-surface Query/React/Bind contract for state holders (#707) | The 8 producer files in `Mithril.GameState/*/Producers/` and `Mithril.WorldSim.{Player,Chat}/Producers/` (~800 lines total — replaced by ~30-line transforms each or folded into folders) |
| `Mithril.GameReports` / `Mithril.Reference` / view layer (cross-world composition) | The merger's "every producer must have a head" invariant + cross-producer synchronization |
| L0 / L0.5 / L1 stack (unchanged) | The `WorldMergerStartHostedService` (renamed but kept; trailing-merger ordering invariant from Call 2 still applies, just to a renamed seam) |

**New (introduced by rethink):**

- `IFrameTransform<TEnvelope, TPayload>` — synchronous per-envelope transformer returning `Frame<TPayload>?`. Verb-key declaration determines dispatch routing.
- `IEnvelopeSource<TEnvelope>` — thin abstraction: `Subscribe(handler) → IDisposable` with per-envelope `IsReplay`. PlayerWorld over `ILogStreamDriver.Subscribe<IClassifiedPlayerLogLine>`; ChatWorld over `ChatLogReplaySource`.
- Internal pipe (`IInternalFramePipe` or similar; implementation choice — could be a sub-bus, could be direct dispatch) — composer subscription surface for typed frames.
- Observer surface — pre- and post-canonical-dispatch hook points on `IEnvelopeSource` or on the world; non-mutating, no-claim contract.
- `INpcInteractionStateMachine` (Phase 2) — the world-fact-decomposed composer that generalizes `IGiftSignalService` and absorbs Smaug's vendor-sale recognition + future interaction outcomes.

## Sizing and phasing

**World-sim has not shipped to end users.** Module code is malleable, not load-bearing. The phasing decision is therefore about sequential development clarity, not about preserving module compatibility.

**Option A — single PR sweep.** Foundation rethink + `INpcInteractionStateMachine` + Smaug/Arwen consumer migration in one PR. ~4–6 days. Cleanest final state on landing; whole architectural shape arrives intact.

- Net ~-1500 to -2000 LoC. Producer files (~800 lines across 8 files) mostly delete; merger machinery (~150 lines each in `PlayerWorld` / `ChatWorld`) deletes; producer-adapter boxing trio (~70 lines per world) deletes. New: ~30-50 lines of dispatch loop per world, ~200-300 lines for `INpcInteractionStateMachine` + consumer migrations.
- Test rewrite: `tests/Mithril.WorldSim.Player.Tests` and `tests/Mithril.WorldSim.Chat.Tests` lose merger-shape tests (priority tie-breaks, "every producer has a head" mode-aware quorum); gain per-envelope dispatch tests. Parser tests survive as folder tests. Composer behavior tests for the new state machine.

**Option B — two-PR sequential.** Phase 1 lands the foundation rethink (merger gone, transforms, observers, internal pipe / external bus split, dispatch loop contract); Phase 2 lands `INpcInteractionStateMachine` + Smaug/Arwen migration. ~2-3 days each.

- Reviewer comprehension benefit: smaller per-PR surface; foundation change reviewable independently of composer refactor.
- Interim state during Phase 1's life: `IGiftSignalService` still exists in gift-only form; vendor-sale doesn't have its proper home yet; module code mostly unchanged.

Either option works. The single-PR sweep is probably cleaner now that the module-constraint framing is dropped (the "modules untouched" virtue of Phase 1 wasn't actually a virtue — it was treating module changes as a cost they aren't).

**Risk:** medium. The honest concern is the per-envelope dispatch loop sequence becoming a load-bearing contract: clock-first, transform-second, internal-pipe-publish-third, etc. The doc + an integration test pinning the order is the only place where a sweep PR could leave a subtle regression.

## Doc amendments owed for the rethink PR

When the implementation PR ships, [`world-simulator.md`](world-simulator.md) needs surgical edits. Checklist:

- [ ] **Principle 1 rewrite.** From *"each world is a timestamp-ordered merger over its N producers"* → *"each world dispatches its source's envelopes through a single canonical handler per envelope, verb-keyed at the transform stage. Observers may fan out from the dispatch loop for diagnostics / test / replay without participating in canonical dispatch."*
- [ ] **Principle 4 generalization.** Extend the existing principle 4 ("no service spans both Player.log and chat") to its broader form: *"the world is closed under any cross-category input — reports, reference data, community calibration, other worlds. Composition across categories lives at the view layer, not inside the world."* The original cross-source-log rule becomes a specific instance of this broader closure commitment.
- [ ] **§Three categories of data — "Vault items — the canonical case that requires GameReports" paragraph.** Wrong as written. The vault verbs (`ProcessAddToStorageVault` / `ProcessRemoveFromStorageVault`) ARE in the log; today's `PlayerInventoryFrameProducer:50-51` already parses `ProcessRemoveFromStorageVault`. The corrected framing: *"Vault contents at world-attach are not observable in the log — PG doesn't emit a vault snapshot at session start. The world observes vault deltas via the storage-vault verbs; the pre-attach baseline comes from `Mithril.GameReports`. The composition lives in a view (`IStorageView`), same architectural slot as cross-world views — and the canonical worked example of view-layer composition across world + reports."* This is the canonical worked example for the world-closure principle; document it as such.
- [ ] **New section — Layer-wide invariant.** Document the L0–L2 single-output, L3 fan-out commitment. The empirical L3 subscriber-multiplicity is what justifies the fan-out shape there; L0 through L2 are linear because each layer's work is discrimination + transformation, not distribution.
- [ ] **Contracts section.**
  - Retire `IFrameProducer<T>`, `IModeAwareFrameProducer<T>`.
  - Retire `IWorld.RegisterProducer`; rename `IWorld.StartMerger` → `IWorld.StartDispatch` (or similar — keep the named seam Call 2's trailing hosted service hits).
  - Introduce `IFrameTransform<TEnvelope, TPayload>` + `IEnvelopeSource<TEnvelope>` + internal pipe interface.
  - Document the per-envelope dispatch loop contract precisely (the six-step sequence).
  - Define the observer surface contract (non-mutating; no-claim; pre and post placement; registration ordering).
- [ ] **Vocabulary section.** Retire "Producer," "Merger," "WorldClockTickProducer," "synthetic-frame producer." Introduce "Transform," "EnvelopeSource," "Internal pipe," "Observer surface," "World-fact decomposition." Update "Frame" to clarify it's the dispatch envelope (no merger involved). Update "Composer" to note: composers read raw frames from internal pipe, not folder-emitted change events.
- [ ] **Decisions ratified post-#642 — #644 section.** Mark superseded with pointer to this rethink doc and #800. Leave reasoning intact for trail legibility.
- [ ] **Mode-flip section.** Add explicit layer attribution: *"`IsReplay` is stamped at L0.5 (classifier + splitter) and forwarded by L1. The world's mode flip is the dispatch loop's observation of the first envelope where `IsReplay == false`; no per-producer plumbing remains."*
- [ ] **Layered architecture diagram.** "Producers" boxes/arrows become "Transforms" with one source reader per world. Show the internal-pipe / external-bus split.
- [ ] **Worked example 1 (Inventory)** in `world-simulator.md` may need updating to reflect the folder + internal-pipe shape (folder emits change events on bus; composers if any read frames from internal pipe).
- [ ] **New worked example.** Add NPC interaction state machine as a worked example of world-fact decomposition.
- [ ] **Composer-decomposition guidance.** New short section: "Decompose composers by world-fact, not by output event. When N outcomes share a common context, one state machine owns the context and emits all N outcomes. When an outcome is solo and context-free, a single-purpose composer is fine."
- [ ] **`docs/glossary.md`.** Mirror the vocabulary updates. Add "Verb-keyed dispatch," "Observer surface," "Canonical handler vs observer," "World-fact decomposition," "Polysemous frame." Retire matching entries.
- [ ] **`docs/world-sim-migration-audit.md`.** Synthetic-frame-producer references swap to "absorbed into per-envelope dispatch" pointing at this doc. Audit otherwise historical; doesn't need full reworking.
- [ ] **`docs/cross-source-correlation.md`.** No change (tier hierarchy operates on view-layer composition, not on the producer/merger layer).
- [ ] **`docs/module-signal-map.md`.** No change (producer concept doesn't surface here; modules consume change events / views).

## Cross-references

- **[#800](https://github.com/moumantai-gg/mithril/issues/800)** — issue body + comment thread. Original proposal in body; ratified refinements in comments. This doc consolidates the ratified state.
- **[#799](https://github.com/moumantai-gg/mithril/pull/799)** — the splitter unbounded-channel fix that surfaced the structural critique.
- **[#643](https://github.com/moumantai-gg/mithril/issues/643)** — change events on world bus (ratified); compatible with this rethink — folders still emit change events on external bus for module consumption.
- **[#644](https://github.com/moumantai-gg/mithril/issues/644)** — explicit clock-tick owner (ratified); outcome survives, mechanism superseded by absorption into the dispatch loop.
- **[#707](https://github.com/moumantai-gg/mithril/issues/707)** — three-surface (Query/React/Bind) contract; applies to composers as well as folders under this rethink.

---

**Open for discussion:** any pushback on the world-fact decomposition principle, the internal-pipe vs external-bus split, or the single-PR-sweep vs phased sizing.
