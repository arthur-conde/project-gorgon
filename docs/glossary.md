# World-sim glossary

Authoritative definitions of the terminology used across the world-simulator design corpus. Each entry stands alone — read this glossary first, then read the canonical doc cited under "See also:" for design rationale and worked examples.

This glossary covers the architectural docs (`docs/world-simulator.md`, `docs/world-sim-migration-audit.md`, `docs/module-signal-map.md`, `docs/cross-source-correlation.md`), the orchestration docs (`docs/world-simulator-orchestration-plan.md`, `docs/world-sim-shepherd.md`), and the operational agent files in `.claude/agents/world-sim-*.md`.

When a term has known drift across the corpus, the entry carries a "⚠ Note:" line citing the contradiction and the most authoritative current source.

**Naming conventions in this glossary.** Entry headers use the [forward-target naming conventions](#folder-interface-convention) ratified in #657: folder interfaces are `I<World><Domain>State`, change-event types are past-tense participles with mandatory world prefix, view event types are past-tense participles with no world prefix. Where the code today still carries a historical name (`*Service`, `*StateMachine`, `*Tracker`, `*Journal`), the entry carries a `Previous names:` line documenting the legacy form and the rename's tracking PR/issue. Entries that explicitly describe pre-migration concepts (e.g., [Source spanning](#source-spanning)'s reference to `IInventoryService`) intentionally keep the historical name — those entries are *about* the pre-split state.

---

## Architectural primitives

### Change event

What a [folder](#folder) emits when applying a [frame](#frame). Flows to intra-world [composers](#composer) during a frame's resolution AND is published on the world's typed bus as a first-class single-world surface — single-world consumers may subscribe to a concrete change-event type via `world.Bus.Subscribe<TConcreteChange>(...)`. Change events are bound to one [world](#world); they never cross the world boundary.

⚠ Note: the bus-publication framing is post-#642. The original commitment ("world-internal; never cross the world boundary") conflated two separate properties — the resolution-graph topology (still true: folders feed composers within a frame's dispatch) and the consumability of the typed channel on the bus (revised: change events ARE first-class bus output for single-world consumers). See [Decisions ratified post-#642](world-simulator.md#decisions-ratified-post-642-2026-05-22).

See also: [Composer](#composer), [Domain frame](#domain-frame), [IWorldEventBus](#iworldeventbus), [Folder](#folder).
Canonical source: [`docs/world-simulator.md` §Vocabulary, principle 10, Decisions ratified post-#642](world-simulator.md).

### ChatWorld

The [world](#world) reconstructed from the chat log files (`ChatLogs/`). Deterministic over the chat stream. Replays from the PG-session-start chat banner symmetric with [PlayerWorld](#playerworld) (principle 9). Has its own folders (chat-inventory mirror, chat-WoP-spent in v1), its own clock, and its own bus.

See also: [PlayerWorld](#playerworld), [IChatWorld](#ichatworld), [World](#world).
Canonical source: [`docs/world-simulator.md` §Vocabulary, principles 2 + 9](world-simulator.md).

### Composer

One of the three state-machine kinds. Consumes [change events](#change-event) and/or [domain frames](#domain-frame) from upstream composers; emits domain frames when its multi-frame pattern is satisfied. Composers *recognize* multi-frame patterns in events PG already emits; they do not anticipate or synthesize PG behavior. Composers chain via subscribe within a frame's resolution — they never re-emit into the world's merger. Intra-world composers live inside one world; [cross-world composers are views](#view).

Composers exist for recognition, not relabeling — under the [#661 passthrough refinement](#661-passthrough-refinement), a view may publish a folder change event through to its bus when the change event already IS the view's unified-semantic-surface event (e.g., [InventoryItemRemoved](#inventoryitemremoved) passthrough of [PlayerInventoryRemoved](#playerinventoryremoved)). The composer-for-recognition rule still applies to multi-frame composition; the passthrough exception is for views whose surface is intentionally a thin renaming of a single upstream event for cross-world consumption.

See also: [Folder](#folder), [Producer](#producer), [Domain frame](#domain-frame), [View](#view), [IComposer](#icomposer), [#661 passthrough refinement](#661-passthrough-refinement).
Canonical source: [`docs/world-simulator.md` principle 10 + Contracts §Composer interface](world-simulator.md).

### Cross-source

Refers to any pairing or fusion that spans **raw log streams** (Player.log + chat). The [Tier 1–4](#tier-14-correlation) correlation hierarchy operates at this layer. Distinct from [cross-world](#cross-world), which operates one layer up (on PlayerWorld + ChatWorld outputs). A consumer that fuses Player.log `ProcessAddItem` with chat `[Status] added` is cross-source.

See also: [Cross-world](#cross-world), [Tier 1–4 (correlation)](#tier-14-correlation).
Canonical source: [`docs/cross-source-correlation.md`](cross-source-correlation.md).

### Cross-world

Refers to any composer or view operating above PlayerWorld and ChatWorld, joining their [domain frame](#domain-frame) outputs. Distinct from [cross-source](#cross-source) (which works on raw log streams). Cross-world composers ARE [views](#view) by definition.

See also: [Cross-source](#cross-source), [View](#view), [Composer](#composer).
Canonical source: [`docs/world-simulator.md` principle 4 + §Layered architecture](world-simulator.md).

### Domain frame

What a [composer](#composer) emits when its multi-frame pattern is satisfied. The cross-world consumption contract — views join PlayerWorld and ChatWorld on domain frames. Domain frames are typed and flow on the world's [IWorldEventBus](#iworldeventbus). They carry timestamps from the event(s) the composer correlated, not from `IWorldClock.Now`.

See also: [Change event](#change-event), [Composer](#composer), [View](#view), [Frame](#frame).
Canonical source: [`docs/world-simulator.md` §Vocabulary, principle 10](world-simulator.md).

### Folder

One of the three state-machine kinds. Consumes [frames](#frame), emits [change events](#change-event). Exactly one folder per [frame payload type](#frame) (registering a second throws). Folders mutate world state; they live inside one [world](#world). Examples: [PlayerSkillState](#iplayerskillstate) (XP frame → skill snapshot mutation), [ChatInventoryState](#ichatinventorystate) (stack-observation frame → name-keyed observation).

Existing folder implementations may still use historical suffixes (`*Service`, `*StateMachine`, `*Tracker`, `*Journal`); these rename opportunistically per the [migration policy in #657](#folder-interface-convention) — a Phase-2-or-later PR that touches a service for substantive reasons also renames it. Pre-rename code-current names appear under each entry's `Previous names:` line.

⚠ Note: `world-sim-migration-audit.md` uses the older name `IFrameHandler<T>` in places. The audit advertises itself as a dated snapshot; the current canonical name is `IFolder<TPayload>`.

See also: [Composer](#composer), [Producer](#producer), [Change event](#change-event), [IFolder](#ifolder), [Folder interface convention](#folder-interface-convention).
Canonical source: [`docs/world-simulator.md` principle 10 + Contracts §Folder interface](world-simulator.md).

### Frame

The unifying primitive: `(timestamp, payload)`. Every input that mutates simulated state is a frame; every producer stamps its output with the event time the frame represents (not the wall-clock when it was synthesized). Each [world](#world) is a timestamp-ordered merger over its N producers.

⚠ Note: the term "synthetic-frame producer" appears in `world-sim-migration-audit.md` and `module-signal-map.md` for wake-at-T schedulers, predating principle 10's narrowing of [producers](#producer) to external-input sources only. See the Producer entry for the resolution.

See also: [Producer](#producer), [Folder](#folder), [Change event](#change-event), [Domain frame](#domain-frame).
Canonical source: [`docs/world-simulator.md` principle 1 + Contracts §Frame model](world-simulator.md).

### IChangeEvent

Marker interface for [change event](#change-event) types in `Mithril.WorldSim.Core`. Concrete change events implement it; folders return `IReadOnlyList<IChangeEvent>` from `Apply`.

See also: [Change event](#change-event), [Folder](#folder), [IFolder](#ifolder).
Canonical source: [`docs/world-simulator.md` §Contracts](world-simulator.md); `src/Mithril.WorldSim.Core/IChangeEvent.cs`.

### IChatWorld

`IWorld` specialization for chat. Consumes chat log files with session-replay-from-banner per principle 9. Genuinely small: two folders (inventory + WoP) in v1, no intra-world composers, self-scopes `(Server, Character)` from the chat banner.

See also: [ChatWorld](#chatworld), [IPlayerWorld](#iplayerworld), [IWorld](#iworld).
Canonical source: [`docs/world-simulator.md` §Contracts §Concrete worlds](world-simulator.md).

### IComposer

Contract for [composers](#composer). Declares `Subscribes : IReadOnlyCollection<Type>` (input event types) and `Observe(eventPayload, clock) : IReadOnlyList<IFrame>`. The world's resolution loop uses `Subscribes` to topologically order composer dispatch within a frame.

See also: [Composer](#composer), [IFolder](#ifolder), [IFrameProducer](#iframeproducer).
Canonical source: [`docs/world-simulator.md` §Contracts §Composer interface](world-simulator.md).

### IFolder

Contract for [folders](#folder), generic on the [frame](#frame) payload type: `IFolder<TPayload>`. Single method: `IReadOnlyList<IChangeEvent> Apply(Frame<TPayload> frame, IWorldClock clock)`. Mutations must depend only on the frame's payload and the folder's prior state; never reads `DateTime.UtcNow`.

See also: [Folder](#folder), [IComposer](#icomposer), [IWorldClock](#iworldclock).
Canonical source: [`docs/world-simulator.md` §Contracts §Folder interface](world-simulator.md).

### IFrame

Non-generic frame base. Lets a composer or producer return heterogeneous frames (different payload types) from a single call without resorting to `Frame<object>`-with-boxing. `Frame<TPayload>` implements `IFrame`; consumers pattern-match on the concrete type (typically inside `IWorldEventBus`) to route to typed subscribers.

See also: [Frame](#frame).
Canonical source: [`docs/world-simulator.md` §Contracts §Frame model](world-simulator.md).

### IFrameProducer

Contract for [producers](#producer): `IFrameProducer<TPayload>`. Exposes `SubscribeAsync(CancellationToken) : IAsyncEnumerable<Frame<TPayload>>` (frames must be in ascending timestamp order) and `Priority : int` (tie-breaker when two producers emit identical timestamps).

See also: [Producer](#producer), [Frame](#frame), [IWorld](#iworld).
Canonical source: [`docs/world-simulator.md` §Contracts §Producer interface](world-simulator.md).

### IInventoryView

Canonical inventory surface for modules — composes Player.log's instance-id ledger ([IPlayerInventoryState](#iplayerinventorystate)) with chat's name-keyed stack-size observations ([IChatInventoryState](#ichatinventorystate)) via a stateful [PendingCorrelator](#pendingcorrelatortkeytreq) (5s simulated-time TTL). Per #602, the view's canonical surface is the **typed-frame-bus**: subscribers receive `Frame<InventoryItemAdded>` / `Frame<InventoryItemRemoved>` / `Frame<InventoryStackChanged>` on the view's bus, with synchronous resolution helpers `TryResolve(instanceId)` and `TryGetStackSize(instanceId)` preserved for point lookups.

The view also exposes a temporary shim `Subscribe(Action<InventoryEvent>)` annotated `[Obsolete]` during the migration so existing consumers (Bilbo, Arwen, Samwise) can be ported one at a time. Shim removal is tracked in #659.

The view's event types match the [naming conventions in #657](#change-event-type-convention): past-tense participles, no `Event` suffix, no world prefix (view events are above the world layer). See [InventoryItemAdded](#inventoryitemadded), [InventoryItemRemoved](#inventoryitemremoved), [InventoryStackChanged](#inventorystackchanged).

See also: [View](#view), [IPlayerInventoryState](#iplayerinventorystate), [IChatInventoryState](#ichatinventorystate), [IWordOfPowerView](#iwordofpowerview), [Tier 1 (correlation)](#tier-14-correlation), [#661 passthrough refinement](#661-passthrough-refinement).
Canonical source: [`docs/world-simulator.md` §Contracts §View interfaces, §Worked example 1](world-simulator.md); GitHub #602, #659.

### IPlayerWorld

`IWorld` specialization for Player.log. Consumes the unified classified pipe (plus future synthetic-frame producers for filesystem reconcile). Owns the large set of Player.log-derived state services ([IPlayerSkillState](#iplayerskillstate), [IPlayerInventoryState](#iplayerinventorystate) post-split, [IPlayerEffectsState](#iplayereffectsstate), etc.).

See also: [PlayerWorld](#playerworld), [IChatWorld](#ichatworld), [IWorld](#iworld).
Canonical source: [`docs/world-simulator.md` §Contracts §Concrete worlds](world-simulator.md).

### IViewClock

A view's clock surface. `Now : DateTimeOffset` is derived from observed [domain frame](#domain-frame) timestamps — typically the max of the most-recently-observed timestamps across both world buses. `Frames` is a tuple `(playerFrame, chatFrame)` of the most-recently-observed frame indices from each world's bus. View-layer TTL gates use the frame timestamps themselves for correlation; `IViewClock.Now` is used only for evicting stale pending state.

See also: [IWorldClock](#iworldclock), [View](#view).
Canonical source: [`docs/world-simulator.md` §Open questions Q5 resolution](world-simulator.md).

### IWorld

Shared contract for both worlds (`IPlayerWorld`, `IChatWorld`). Each owns its own producers, folders, composers, clock, frame merger, and output bus. Exposes `Clock`, `Bus`, `RegisterProducer<T>`, `RegisterFolder<T>` (one folder per payload type), `RegisterComposer`, and `StartAsync`.

See also: [IPlayerWorld](#iplayerworld), [IChatWorld](#ichatworld), [IWorldEventBus](#iworldeventbus), [IWorldClock](#iworldclock).
Canonical source: [`docs/world-simulator.md` §Contracts §World interface](world-simulator.md).

### IWorldClock

A [world](#world)'s simulated wall-clock. `Now` is always the timestamp of the most recently applied frame (no continuous-time abstraction, no live-mode interpolation). `Frame` is a strictly-monotonic frame index. `Mode` ∈ {`Replaying`, `Live`}. Real wall-clock is used only inside the world's merger to know when to block on the live tail; never by folders, composers, views, or modules.

See also: [Tri-property clock](#tri-property-clock), [WorldMode](#worldmode), [Now (simulated)](#now-simulated), [IViewClock](#iviewclock).
Canonical source: [`docs/world-simulator.md` principle 5 + Contracts §IWorldClock](world-simulator.md).

### IWorldEventBus

Typed pub-sub for a [world](#world)'s output surface. Carries both [domain frames](#domain-frame) (the cross-world consumption contract) AND [change events](#change-event) (first-class output for single-world consumers; subscribe via `Subscribe<TConcreteChange>(...)`). Subscribers see emissions in resolution order (deterministic over the source stream). The bus is sealed: nothing flows back into the world from above. Often called the "world bus" informally.

See also: [Change event](#change-event), [Domain frame](#domain-frame), [Sealed output boundary](#sealed-output-boundary).
Canonical source: [`docs/world-simulator.md` §Contracts §World interface, Decisions ratified post-#642](world-simulator.md).

### IWordOfPowerView

Canonical Words-of-Power surface for modules. Composes [IPlayerWordOfPowerDiscoveryState](#iplayerwordofpowerdiscoverystate) (discovery half) with [IChatWordOfPowerState](#ichatwordofpowerstate) (spent half), keyed by code. No temporal TTL — discovery and spent may be hours/days apart; the join is by code, not time-window. Emits [WordOfPowerKnowledgeChanged](#wordofpowerknowledgechanged) on its bus when a code's effective state flips between Known and Spent.

See also: [View](#view), [IInventoryView](#iinventoryview), [WordOfPowerKnowledgeChanged](#wordofpowerknowledgechanged).
Canonical source: [`docs/world-simulator.md` §Contracts §View interfaces, §Worked example 2](world-simulator.md).

### Live (WorldMode)

The mode a [world](#world) enters once it has drained its source backlog and is now blocking on the live source-stream tail for new frames. Side-effect-emitting consumers (audio alarms, window flash, OS notifications) gate on `Mode == Live`.

See also: [WorldMode](#worldmode), [Replaying](#replaying-worldmode), [Mode == Live gate](#mode--live-gate).
Canonical source: [`docs/world-simulator.md` principle 12](world-simulator.md).

### Mode == Live gate

The condition under which side-effect-emitting consumers (audio alarms, window flash, OS notifications) may fire — `if (_worldClock?.Mode == WorldMode.Replaying) return;` immediately before the side-effect. State derivation upstream of the sink is mode-agnostic; only the projection outwards gates.

Mithril takes world-derived state and projects it outwards into real-world effects that touch the user's attention; that projection is honest only when the world's state matches present reality. During `Replaying` the world is reconstructing past state, and projecting it outward would surface events the user already lived through (or already missed) in real time — blasting the user with replays of yesterday's alarms on Mithril restart. During `Live`, model-time matches real-time and the lift into the user's world is appropriate.

Applies as a sustained contract to every sink reached via a world-event subscription — not a one-time audit; new sinks are gated by construction as they migrate onto world events. Consumers that are wall-clock-driven (e.g., a `DispatcherTimer` on real time) sit structurally outside the contract; migrating one onto world events implies adding the guard at the same time.

See also: [Live (WorldMode)](#live-worldmode), [WorldMode](#worldmode), [Replaying](#replaying-worldmode).
Canonical source: [`docs/world-simulator.md` principle 12 and "Decisions ratified post-#642" Call 3](world-simulator.md).

### Now (simulated)

The `Now` property on [IWorldClock](#iworldclock) (and analogously on [IViewClock](#iviewclock)) — the timestamp of the most recently applied frame. Weakly monotonic at 1-second resolution (PG's timestamp precision). Reads during live-mode idle return the same value as immediately after the last frame applied. **Not** the real wall-clock; real wall-clock is `TimeProvider.System.GetUtcNow()` and is only used inside the world's merger.

See also: [IWorldClock](#iworldclock), [Tri-property clock](#tri-property-clock).
Canonical source: [`docs/world-simulator.md` Vocabulary, principle 5, principle 13](world-simulator.md).

### PlayerWorld

The [world](#world) reconstructed from `Player.log`. NOT "the player character" — that's a separate entity (`Character`) tracked within either world. Deterministic over `IClassifiedPlayerLogStream`. Owns the large set of Player.log-derived state services.

See also: [ChatWorld](#chatworld), [IPlayerWorld](#iplayerworld), [World](#world).
Canonical source: [`docs/world-simulator.md` §Vocabulary, principle 2](world-simulator.md).

### Producer

One of the three state-machine kinds. Sources of external-input [frames](#frame) feeding a [world](#world). Log tails (Player.log, chat log) are the canonical examples; future possibilities include filesystem reconcile for character export. Each producer declares a `Priority` used to break ties when two producers emit frames with identical timestamps.

⚠ Note: producers are **NOT** a mechanism for user-driven scheduling or wake-at-T. User-side concerns (Gandalf timers, alarm scheduling) consume world domain events (e.g., [`CalendarTimeAdvanced`](#calendartimeadvanced)) and run their own module-internal logic — they do not register producers in a world's merger. The world is sealed at its input. `world-sim-migration-audit.md` (§Migration-plan spot-checks #11) and `module-signal-map.md` (Gandalf entry) describe "wake-at-T synthetic-frame producers" predating this narrowing; the audit doc has a top-of-file staleness banner that gestures at the change. The current authoritative narrowing is `docs/world-simulator.md` principle 10.

See also: [Folder](#folder), [Composer](#composer), [Frame](#frame), [IFrameProducer](#iframeproducer), [WorldClockTickProducer](#worldclocktickproducer).
Canonical source: [`docs/world-simulator.md` principle 10 + Contracts §Producer interface](world-simulator.md).

### Replaying (WorldMode)

The mode a [world](#world) is in while draining recorded frames toward the live source-stream tail. State derivation is mode-agnostic; side-effect-emitting consumers gate on `Mode == Live`, meaning they do not fire while `Replaying`.

See also: [Live (WorldMode)](#live-worldmode), [WorldMode](#worldmode), [Mode == Live gate](#mode--live-gate).
Canonical source: [`docs/world-simulator.md` principle 12](world-simulator.md).

### Sealed output boundary

The architectural commitment that nothing flows back *into* a [world](#world) from above. Each world is closed at its [IWorldEventBus](#iworldeventbus) and at its input (only its registered [producers](#producer) emit frames; user actions, view feedback, and module mutations never re-enter the world). Cross-world consumers (views) subscribe to one or more world buses; they cannot write to a world.

See also: [World](#world), [IWorldEventBus](#iworldeventbus), [Per-frame resolution](#per-frame-resolution).
Canonical source: [`docs/world-simulator.md` principle 2, principle 11](world-simulator.md).

### Tri-property clock

The shape of [IWorldClock](#iworldclock): `(Now, Frame, Mode)`. `Now` answers "how much simulated time has passed?" (1-second resolution). `Frame` answers "are we at the same point in the trajectory?" (strictly monotonic). `Mode` answers "should side-effecting consumers fire now?" (`Replaying` or `Live`). The triple identifies a unique moment in a unique mode.

See also: [IWorldClock](#iworldclock), [Now (simulated)](#now-simulated), [WorldMode](#worldmode).
Canonical source: [`docs/world-simulator.md` principle 5](world-simulator.md).

### View

A [composer](#composer) operating *above* the worlds — subscribes to one or more world buses, maintains stateful composed model, exposes the composed state as the canonical surface for cross-world consumers. Cross-world composers ARE views by definition. Examples: `IInventoryView`, `IWordOfPowerView`. Views are deterministic over the worlds' bus emissions.

⚠ Note: do not confuse with the legacy persistence wrapper `PerCharacterView<T>` (a JSON-backed per-character state holder, unrelated to the view layer). `world-simulator.md` flags that `PerCharacterView<T>` "becomes per-session-keyed" — but the word "view" without the type suffix should be read as the world-sim sense.

See also: [Composer](#composer), [Cross-world](#cross-world), [IInventoryView](#iinventoryview), [IWordOfPowerView](#iwordofpowerview).
Canonical source: [`docs/world-simulator.md` §Vocabulary, principle 4, §Layered architecture](world-simulator.md).

### World

Short for **world runtime** (used interchangeably). One independent, deterministic pipeline (frames → folders → change events → composers → domain frames → bus) over one source stream. The two concrete instances are [PlayerWorld](#playerworld) and ChatWorld; future sources get their own worlds if needed. Worlds don't query each other and don't send messages to each other — they're sealed at the bus.

See also: [PlayerWorld](#playerworld), [ChatWorld](#chatworld), [IWorld](#iworld), [Sealed output boundary](#sealed-output-boundary), [World runtime](#world-runtime).
Canonical source: [`docs/world-simulator.md` §Vocabulary, principle 2, §Layered architecture](world-simulator.md).

### World bus

Informal name for a [world](#world)'s [IWorldEventBus](#iworldeventbus). Used in narrative prose where the typed-interface name is too noisy. Same primitive.

See also: [IWorldEventBus](#iworldeventbus).
Canonical source: [`docs/world-simulator.md` §Vocabulary, §Layered architecture](world-simulator.md).

### WorldClockTickProducer

The explicit clock-tick owner ratified post-#644: a [producer](#producer) whose payload is dedicated to clock advancement, owning a [folder](#folder) that emits [`CalendarTimeAdvanced`](#calendartimeadvanced) domain frames at the source-stream cadence. Replaces the current `ClassifiedPlayerLogProducer`'s implicit clock-advancement-via-folder-irrelevant-frames behavior. Without it, dropping `ClassifiedPlayerLogProducer` naively would stagnate the clock during folder-irrelevant log stretches and cause Gandalf's planned scheduler-collapse alarms to fire late.

⚠ Note: design ratified in PR #654 (closes #644). Implementation tracked in #655.

See also: [Producer](#producer), [CalendarTimeAdvanced](#calendartimeadvanced), [Folder](#folder).
Canonical source: [`docs/world-simulator.md` migration item #13, Decisions ratified post-#642](world-simulator.md).

### WorldMode

Enum on [IWorldClock](#iworldclock): `Replaying` (draining recorded frames toward the live tail) or `Live` (caught up, blocking on new frames). Each world tracks `WorldMode` independently; PlayerWorld may catch up before ChatWorld or vice versa. Transitions emit a `ModeChanged(from, to, at)` domain event on the bus.

See also: [Replaying](#replaying-worldmode), [Live](#live-worldmode), [Mode == Live gate](#mode--live-gate), [ModeChanged](#modechanged).
Canonical source: [`docs/world-simulator.md` Vocabulary, principle 12](world-simulator.md).

### World runtime

Long form of [World](#world). Used interchangeably; "world" is the short form. Refers to the per-source pipeline (merger + folders + composers + clock + bus) and its associated producers.

See also: [World](#world).
Canonical source: [`docs/world-simulator.md` §Vocabulary](world-simulator.md).

---

## Naming conventions

### Folder interface convention

Folder interfaces are named `I<World><Domain>State`. The `<World>` prefix is mandatory and disambiguates the source — `IPlayerSkillState` is a Player.log folder, `IChatInventoryState` is a chat-log folder. The `<Domain>` is a noun phrase describing what's tracked (Skill, Inventory, Effects, etc.). The trailing `State` is canonical; pre-convention names used `*Service`, `*StateMachine`, `*Tracker`, or `*Journal` suffixes inconsistently.

Existing folder implementations may still use historical suffixes; renames happen opportunistically — a Phase-2-or-later PR that touches a folder for substantive reasons also renames it. Standalone rename PRs are explicitly discouraged. Per-entry `Previous names:` lines in this glossary document the historical form.

See also: [Folder](#folder), [Change-event type convention](#change-event-type-convention), [World-prefix convention](#world-prefix-convention).
Canonical source: GitHub #657.

### Change-event type convention

[Change-event](#change-event) types are named as past-tense participles with no `Event` suffix. Examples: `PlayerInventoryAdded`, `ChatInventoryObserved`, `PlayerSkillProgressed`, `WordOfPowerKnowledgeChanged`. The grammatical form ("what happened") matches the semantic role on the bus — a change event is the report of a discrete world-state change.

The `Event` suffix is forbidden because it's redundant with the type's role and adds noise to every subscription site. A subscriber writes `world.Bus.Subscribe<PlayerInventoryAdded>(...)`, not `world.Bus.Subscribe<PlayerInventoryAddedEvent>(...)`.

See also: [Change event](#change-event), [Folder interface convention](#folder-interface-convention), [World-prefix convention](#world-prefix-convention).
Canonical source: GitHub #657.

### World-prefix convention

Folder change-event types carry a mandatory world prefix: `Player*` for PlayerWorld-bus events, `Chat*` for ChatWorld-bus events. The first word of the type name tells the reader (and the grep) which world's bus emits it.

View events (emitted by [views](#view) on their own bus, above the world layer) carry no world prefix — they're cross-world or world-agnostic by construction, and a prefix would imply false world-affinity. Examples: `InventoryItemAdded` (view event, no prefix), `WordOfPowerKnowledgeChanged` (view event, no prefix).

Principle citation: a name should tell you the emitter. World-scoped events emit from world buses; the prefix encodes the world's name. View-scoped events emit from view buses; the absence of prefix encodes their position above the world layer.

See also: [Change-event type convention](#change-event-type-convention), [Folder interface convention](#folder-interface-convention), [Change event](#change-event), [Domain frame](#domain-frame).
Canonical source: GitHub #657.

---

## Folder-state interfaces (post-split)

### IPlayerInventoryState

Folder for Player.log's instance-id inventory ledger. Consumes `ProcessAddItem` / `ProcessRemoveItem` frames; emits [PlayerInventoryAdded](#playerinventoryadded) and [PlayerInventoryRemoved](#playerinventoryremoved) change events on the PlayerWorld bus. Synchronous lookups: `TryResolve(instanceId)` returns the InternalName for an in-ledger instance id.

Previous names: `IInventoryService` (legacy, pre-split — also carried the chat half; the split is tracked in #602).

See also: [Folder](#folder), [IChatInventoryState](#ichatinventorystate), [IInventoryView](#iinventoryview), [PlayerInventoryAdded](#playerinventoryadded), [PlayerInventoryRemoved](#playerinventoryremoved).
Canonical source: [`docs/world-simulator.md` §Worked example 1](world-simulator.md); GitHub #602.

### IChatInventoryState

Folder for chat-log inventory stack-size observations. Consumes `[Status] X xN added` frames from chat; emits [ChatInventoryObserved](#chatinventoryobserved) change events on the ChatWorld bus. Synchronous lookups: `TryGetStackSize(name)` returns the most-recently-observed stack size for an InternalName.

Previous names: `IChatInventoryStateMachine` (legacy, pre-split); part of `IInventoryService` pre-#602.

See also: [Folder](#folder), [IPlayerInventoryState](#iplayerinventorystate), [IInventoryView](#iinventoryview), [ChatInventoryObserved](#chatinventoryobserved).
Canonical source: [`docs/world-simulator.md` §Worked example 1](world-simulator.md); GitHub #602.

### IPlayerWordOfPowerDiscoveryState

Folder for Player.log's Word-of-Power discovery state. Consumes discovery frames from Player.log; emits [PlayerWordOfPowerDiscovered](#playerwordofpowerdiscovered) change events on the PlayerWorld bus. Tracks each code's (effect, description, discovery count, first-discovery timestamp).

This entry is already convention-compliant; no historical-name suffix applies.

See also: [Folder](#folder), [IChatWordOfPowerState](#ichatwordofpowerstate), [IWordOfPowerView](#iwordofpowerview), [PlayerWordOfPowerDiscovered](#playerwordofpowerdiscovered).
Canonical source: [`docs/world-simulator.md` §Worked example 2](world-simulator.md); GitHub #603.

### IChatWordOfPowerState

Folder for chat-log Word-of-Power "spent" events. Consumes spent frames from chat; emits [ChatWordOfPowerSpent](#chatwordofpowerspent) change events on the ChatWorld bus. Tracks each code's most-recent spent timestamp.

Previous names: `SarumanCodebookService` chat half (legacy, pre-split — same service held both the player-side discovery state and the chat-side spent state; the split is tracked in #603).

See also: [Folder](#folder), [IPlayerWordOfPowerDiscoveryState](#iplayerwordofpowerdiscoverystate), [IWordOfPowerView](#iwordofpowerview), [ChatWordOfPowerSpent](#chatwordofpowerspent).
Canonical source: [`docs/world-simulator.md` §Worked example 2](world-simulator.md); GitHub #603.

### IPlayerSkillState

Example folder reference: tracks Player.log skill XP progression. Emits [PlayerSkillProgressed](#playerskillprogressed) on the PlayerWorld bus. Per-character snapshot; consumed by Elrond's planner and Mithril.Reference enrichment per #465 / #471.

Previous names: `IPlayerSkillStateService` (current code-current name; renames opportunistically per the [folder interface convention](#folder-interface-convention)).

See also: [Folder](#folder), [PlayerSkillProgressed](#playerskillprogressed).
Canonical source: [`docs/world-simulator.md` §Contracts §Concrete worlds](world-simulator.md).

### IPlayerEffectsState

Example folder reference: tracks Player.log buff/debuff effects state.

Previous names: `IPlayerEffectsStateService` (current code-current name; renames opportunistically per the [folder interface convention](#folder-interface-convention)).

See also: [Folder](#folder).
Canonical source: [`docs/world-simulator.md` §Contracts §Concrete worlds](world-simulator.md).

---

## Canonical domain events

### CalendarTimeAdvanced

Domain event emitted on each world's bus on second-resolution world-clock advancement (deduplicated within a wall-clock second). Carries `(Now, Mode)`. Consumers that care about time progression subscribe to this rather than reading `IWorldClock.Now` continuously. Module-side schedulers (Gandalf timers, Samwise ripeness alarms) compare event timestamps against internal thresholds and fire (gated on `Mode == Live`).

See also: [WorldClockTickProducer](#worldclocktickproducer), [TimeOfDayShift](#timeofdayshift), [Now (simulated)](#now-simulated).
Canonical source: [`docs/world-simulator.md` principle 13](world-simulator.md).

### ModeChanged

Domain event emitted on a world's bus when [WorldMode](#worldmode) transitions (`Replaying` → `Live` or `Live` → `Replaying`). Payload: `(from, to, at)`. Observable, so consumers can react to mode flips (e.g., flush a "drained" indicator, start delivering audio alarms).

See also: [WorldMode](#worldmode), [Live (WorldMode)](#live-worldmode).
Canonical source: [`docs/world-simulator.md` principle 12, Vocabulary](world-simulator.md).

### TimeOfDayShift

Composer-derived domain event emitted on the world's bus on PG in-game time-of-day shift transitions (the boundaries between in-game shift periods). Payload: `(from, to, at, Mode)`. Derived from `CalendarTimeAdvanced`. Consumed by Gandalf's planned scheduler collapse.

See also: [CalendarTimeAdvanced](#calendartimeadvanced), [Composer](#composer).
Canonical source: [`docs/world-simulator.md` principle 13, Vocabulary](world-simulator.md).

---

## Folder change events (single-world surface)

These events flow on world buses; subscribers read them via `world.Bus.Subscribe<TConcreteChange>(...)`. They carry the mandatory [world prefix](#world-prefix-convention).

### PlayerInventoryAdded

Emitted on the PlayerWorld bus by [IPlayerInventoryState](#iplayerinventorystate) when Player.log's `ProcessAddItem` fires. Payload: instance-id + InternalName + (event timestamp). Composed downstream into [InventoryItemAdded](#inventoryitemadded) on the [IInventoryView](#iinventoryview) bus after pairing with a [ChatInventoryObserved](#chatinventoryobserved) within the 5s TTL window.

See also: [IPlayerInventoryState](#iplayerinventorystate), [InventoryItemAdded](#inventoryitemadded), [ChatInventoryObserved](#chatinventoryobserved), [PendingCorrelator](#pendingcorrelatortkeytreq).
Canonical source: [`docs/world-simulator.md` §Worked example 1](world-simulator.md); GitHub #602.

### PlayerInventoryRemoved

Emitted on the PlayerWorld bus by [IPlayerInventoryState](#iplayerinventorystate) when Player.log's `ProcessRemoveItem` fires. Payload: instance-id + (event timestamp). Passed through unchanged to the view as [InventoryItemRemoved](#inventoryitemremoved) per the [#661 passthrough refinement](#661-passthrough-refinement) — the view's removal-surface IS this event with the unified-semantic-surface name; no multi-frame composition needed.

See also: [IPlayerInventoryState](#iplayerinventorystate), [InventoryItemRemoved](#inventoryitemremoved), [#661 passthrough refinement](#661-passthrough-refinement).
Canonical source: [`docs/world-simulator.md` §Worked example 1](world-simulator.md); GitHub #602, #661.

### ChatInventoryObserved

Emitted on the ChatWorld bus by [IChatInventoryState](#ichatinventorystate) when chat's `[Status] X xN added` line is observed. Payload: InternalName + stack size + (event timestamp). Carries the most-recently-observed stack size for the name; subsequent observations replace the prior. Cross-world correlation key = InternalName.

See also: [IChatInventoryState](#ichatinventorystate), [InventoryItemAdded](#inventoryitemadded), [InventoryStackChanged](#inventorystackchanged).
Canonical source: [`docs/world-simulator.md` §Worked example 1](world-simulator.md); GitHub #602.

### PlayerSkillProgressed

Emitted on the PlayerWorld bus by [IPlayerSkillState](#iplayerskillstate) when Player.log's skill XP changes. Payload: skill key + (XP delta or new snapshot) + (event timestamp). Player-only domain; the world prefix is still mandatory per the [world-prefix convention](#world-prefix-convention).

See also: [IPlayerSkillState](#iplayerskillstate), [World-prefix convention](#world-prefix-convention).
Canonical source: [`docs/world-simulator.md` §Contracts §Concrete worlds](world-simulator.md).

### PlayerWordOfPowerDiscovered

Emitted on the PlayerWorld bus by [IPlayerWordOfPowerDiscoveryState](#iplayerwordofpowerdiscoverystate) when Player.log reports a Word-of-Power discovery. Payload: code + (effect, description, discovery count, first-discovery timestamp).

See also: [IPlayerWordOfPowerDiscoveryState](#iplayerwordofpowerdiscoverystate), [IWordOfPowerView](#iwordofpowerview), [WordOfPowerKnowledgeChanged](#wordofpowerknowledgechanged).
Canonical source: [`docs/world-simulator.md` §Worked example 2](world-simulator.md); GitHub #603.

### ChatWordOfPowerSpent

Emitted on the ChatWorld bus by [IChatWordOfPowerState](#ichatwordofpowerstate) when chat reports a Word-of-Power being spent. Payload: code + (event timestamp).

See also: [IChatWordOfPowerState](#ichatwordofpowerstate), [IWordOfPowerView](#iwordofpowerview), [WordOfPowerKnowledgeChanged](#wordofpowerknowledgechanged).
Canonical source: [`docs/world-simulator.md` §Worked example 2](world-simulator.md); GitHub #603.

---

## View events (cross-world surface)

These events flow on view buses, above the world layer. They carry no world prefix per the [world-prefix convention](#world-prefix-convention).

### InventoryItemAdded

Emitted on the [IInventoryView](#iinventoryview) bus. Composed from a [PlayerInventoryAdded](#playerinventoryadded) and the paired [ChatInventoryObserved](#chatinventoryobserved) within the 5s simulated-time TTL window. Payload: instance-id + InternalName + stack size (null if no pairing arrived within TTL — the Add fires unpaired). The view's [PendingCorrelator](#pendingcorrelatortkeytreq) holds the Player-side Add until the chat observation arrives or the window expires.

See also: [IInventoryView](#iinventoryview), [PlayerInventoryAdded](#playerinventoryadded), [ChatInventoryObserved](#chatinventoryobserved), [InventoryStackChanged](#inventorystackchanged), [Tier 1 (correlation)](#tier-14-correlation).
Canonical source: [`docs/world-simulator.md` §Worked example 1](world-simulator.md); GitHub #602.

### InventoryItemRemoved

Emitted on the [IInventoryView](#iinventoryview) bus. Passthrough of [PlayerInventoryRemoved](#playerinventoryremoved) — same payload (instance-id + event timestamp), different type name. Permitted under the [#661 passthrough refinement](#661-passthrough-refinement) (unified-semantic-surface exception): the view's removal-surface IS the player-side event renamed for cross-world consumers; no chat correlation is meaningful for removals because chat doesn't report removals at instance-id granularity.

See also: [IInventoryView](#iinventoryview), [PlayerInventoryRemoved](#playerinventoryremoved), [#661 passthrough refinement](#661-passthrough-refinement), [Composer](#composer).
Canonical source: [`docs/world-simulator.md` §Worked example 1](world-simulator.md); GitHub #602, #661.

### InventoryStackChanged

Emitted on the [IInventoryView](#iinventoryview) bus when a [ChatInventoryObserved](#chatinventoryobserved) arrives AFTER the corresponding [InventoryItemAdded](#inventoryitemadded) already fired (TTL window expired with no chat pair, then chat eventually surfaced). Payload: instance-id + InternalName + new stack size + (event timestamp). Lets consumers update displayed stack sizes that initially showed `null`.

See also: [IInventoryView](#iinventoryview), [InventoryItemAdded](#inventoryitemadded), [ChatInventoryObserved](#chatinventoryobserved).
Canonical source: [`docs/world-simulator.md` §Worked example 1](world-simulator.md); GitHub #602.

### WordOfPowerKnowledgeChanged

Emitted on the [IWordOfPowerView](#iwordofpowerview) bus when a Word-of-Power code's effective state flips between Known and Spent. The view computes the flip by comparing the most-recent [PlayerWordOfPowerDiscovered](#playerwordofpowerdiscovered) timestamp against the most-recent [ChatWordOfPowerSpent](#chatwordofpowerspent) timestamp for that code: if discovery is newer, the code is Known; if spent is newer, the code is Spent. Payload: code + new effective state + (latest contributing timestamp).

See also: [IWordOfPowerView](#iwordofpowerview), [PlayerWordOfPowerDiscovered](#playerwordofpowerdiscovered), [ChatWordOfPowerSpent](#chatwordofpowerspent).
Canonical source: [`docs/world-simulator.md` §Worked example 2](world-simulator.md); GitHub #603.

---

## Data categories + scope

### `(Server, Character)`

Tuple notation for the per-session key. The unit of per-character-per-server partitioning. Derived from `IGameSessionService` for the Player.log side, from the chat banner for the chat side. Both streams self-scope independently; cross-world joins assert `(Server, Character)` agreement before firing.

See also: [Per-session scope tier](#per-session-scope-tier), [Self-scope](#self-scope), [Scope](#scope-referenceworldcharacter).
Canonical source: [`docs/world-simulator.md` Vocabulary; `docs/module-signal-map.md` §Scope vocabulary](module-signal-map.md).

### External shared data sources

One of the [three categories of data](#three-categories-of-data) the architecture admits. Data the worlds don't produce, that multiple consumers might want — discrete records, externally sourced (filesystem / CDN / startup log parsing), point-in-time or version-stamped. Includes `Mithril.Reference`, `Mithril.GameReports`, `ICommunityCalibrationService`, and the planned `IServerCatalogService`.

⚠ Note: `Mithril.GameReports` is the canonical case requiring this category (vault contents exist only in PG's character export, not in the worlds). The `module-signal-map.md` scope table tags GameReports `reference` for the partition value; this conflicts with the per-character nature of report files. The category-vs-partition axes are distinct: this entry covers the **category** sense; the partition sense lives under [Scope](#scope-referenceworldcharacter).

See also: [Three categories of data](#three-categories-of-data), [Module-owned adjacent state](#module-owned-adjacent-state), [World-derived state](#world-derived-state), [Mithril.GameReports](#mithrilgamereports).
Canonical source: [`docs/world-simulator.md` §Three categories of data the architecture admits](world-simulator.md).

### Mithril.GameReports

The foundation-layer assembly for PG character export snapshots — `Reports/items_X.json`, plus skills / recipes / quests / vault. Per-character, per-export-time. Different semantic from world state: GameState describes the world *as the player observes it through events*; GameReports describes *PG's authoritative snapshot at the moment of export*. They can briefly disagree, and that's fine. Reports also contain data the worlds inherently cannot see — most notably vault contents.

See also: [External shared data sources](#external-shared-data-sources), [Three categories of data](#three-categories-of-data).
Canonical source: [`docs/world-simulator.md` §Three categories of data](world-simulator.md); also `docs/module-signal-map.md` §`Mithril.GameReports` (shipped via #612).

### Module-owned adjacent state

One of the [three categories of data](#three-categories-of-data). User-driven, module-internal, persisted alongside the module. Examples: Gandalf timer definitions, Saruman codebook overrides, Samwise alarm snoozes, per-module settings. Cross-module reads go through the module's service interface, not directly into its JSON store. Mutations are **not** gated by world `Mode` — only side effects gate.

See also: [Three categories of data](#three-categories-of-data), [External shared data sources](#external-shared-data-sources), [World-derived state](#world-derived-state).
Canonical source: [`docs/world-simulator.md` §Three categories of data](world-simulator.md).

### Per-session scope tier

The `(Server, Character)` tuple derived from `IGameSessionService`. The unit of per-character-per-server partitioning used by both world-character-state and module-owned state. Replaces the legacy "per-character" framing in `PerCharacterView<T>` (which becomes per-session-keyed under world-sim).

See also: [`(Server, Character)`](#server-character), [Self-scope](#self-scope), [Scope](#scope-referenceworldcharacter).
Canonical source: [`docs/world-simulator.md` Vocabulary](world-simulator.md).

### Scope (reference / world / character)

The partition along which a state holder's state replicates. Three values: **reference** (immutable for the lifetime of a Mithril attach modulo CDN refresh — items.json, recipes.json, etc.); **world (per-server)** (properties of the simulated PG world, partitioned per-server — weather, celestial, area-existence ledger); **character (per-server-per-character)** (properties of an individual character's relationship to its server's world — skills, recipes, inventory, quests, favor).

⚠ Note: distinct from the [Three categories of data](#three-categories-of-data) axis (world-derived / external shared / module-owned). They're orthogonal: a piece of data has both a category AND a scope-partition tag. The `Mithril.GameReports` entry in `module-signal-map.md`'s scope table is tagged `reference` (partition value) for what `world-simulator.md` categorizes as "external shared data" (category value).

See also: [Per-session scope tier](#per-session-scope-tier), [Self-scope](#self-scope), [Three categories of data](#three-categories-of-data).
Canonical source: [`docs/module-signal-map.md` §Scope vocabulary](module-signal-map.md); also `docs/world-simulator.md` principle 6.

### Self-scope

The property that each log stream (Player.log, chat) identifies its `(Server, Character)` from its own intra-source signals, with no cross-source coupling for scope determination. Player.log self-scopes via `Servers:` catalog + `EVENT(Ok): connected` + `LoginBanner`; chat self-scopes via its `**** Logged In As X. Server Y.` banner. Cross-source agreement is verification, not derivation.

See also: [`(Server, Character)`](#server-character), [Scope](#scope-referenceworldcharacter).
Canonical source: [`docs/module-signal-map.md` §Scope vocabulary; `docs/world-simulator.md` principle 7](world-simulator.md).

### Three categories of data

The architecture admits exactly three places a consumer's data can come from: **world-derived state** (events from PlayerWorld / ChatWorld / views — continuous, event-driven, replay-deterministic), **external shared data sources** (`Mithril.Reference`, `Mithril.GameReports`, `ICommunityCalibrationService`, `IServerCatalogService` — discrete records, externally sourced, point-in-time / version-stamped), and **module-owned adjacent state** (user-driven, module-internal, persisted alongside the module). Views can compose across all three.

See also: [World-derived state](#world-derived-state), [External shared data sources](#external-shared-data-sources), [Module-owned adjacent state](#module-owned-adjacent-state), [Scope](#scope-referenceworldcharacter).
Canonical source: [`docs/world-simulator.md` §Three categories of data the architecture admits](world-simulator.md).

### World-derived state

One of the [three categories of data](#three-categories-of-data). Observed events from Player.log or chat, transformed through the folder/composer pipeline of [PlayerWorld](#playerworld) or [ChatWorld](#chatworld). Live, continuous, event-driven, replay-deterministic. Consumed by subscribing to world buses (or to views above them).

See also: [Three categories of data](#three-categories-of-data), [PlayerWorld](#playerworld), [ChatWorld](#chatworld), [IWorldEventBus](#iworldeventbus).
Canonical source: [`docs/world-simulator.md` §Three categories of data](world-simulator.md).

---

## Replay + determinism

### Per-frame resolution

The principle that a single [frame](#frame) dispatching within a [world](#world) is a finite, acyclic graph traversal: frame dispatches to its [folder](#folder); folder emits [change events](#change-event); intra-world [composers](#composer) subscribed to those events run and possibly emit [domain frames](#domain-frame); composers subscribed to those domain frames run; resolution continues until no new events. The dispatch graph is topologically ordered (composers declare their input event types); resolution depth is bounded by the graph's depth. No cycles. No merger re-entry.

See also: [Folder](#folder), [Composer](#composer), [Sealed output boundary](#sealed-output-boundary).
Canonical source: [`docs/world-simulator.md` principle 11](world-simulator.md).

### Replay-determinism

The architectural property that replaying a recorded log corpus produces an identical trajectory through the [world](#world) (and by extension through [views](#view)). Required because each layer's determinism derives from the layer beneath: source streams are deterministic input; each world is deterministic over its source via timestamp-ordered merger + topologically-sorted composer dispatch; each view is deterministic over the worlds' bus emissions; modules consume the view layer. The only non-determinism is user input + reference data updates (CDN refresh), both explicitly out of the worlds' input set.

⚠ Note: `world-sim-migration-audit.md` §Replay-determinism inspection is the reviewer's canonical check list (`DateTime.UtcNow` reads in state-decision paths, `Stopwatch`, unsorted iteration, etc.).

See also: [IWorldClock](#iworldclock), [Mode == Live gate](#mode--live-gate), [Sealed output boundary](#sealed-output-boundary).
Canonical source: [`docs/world-simulator.md` §Determinism properties](world-simulator.md).

### Session-replay from banner

The chat side's replay anchor (principle 9). On attach, [ChatWorld](#chatworld) seeks to the most recent chat banner matching the current Player.log session (paired by character + close-in-time), then emits forward. Symmetric with Player.log's session-start replay. Cost is bounded (~hundreds of KB of chat per day vs ~12 MB of Player.log already replayed).

See also: [ChatWorld](#chatworld), [Replay-determinism](#replay-determinism).
Canonical source: [`docs/world-simulator.md` principle 9](world-simulator.md).

---

## Cross-source correlation

### Same-game-second

The granularity at which two log-stream events are considered concurrent for tiebreak purposes. PG timestamps are second-resolution; pairs of events with identical timestamps from the two streams have no meaningful order in the data itself.

See also: [Tier 1–4 (correlation)](#tier-14-correlation), [Cross-source](#cross-source).
Canonical source: [`docs/cross-source-correlation.md`](cross-source-correlation.md).

### PendingCorrelator<TKey,TReq>

The shared primitive that implements [Tier 1](#tier-14-correlation) — keyed correlation. A bounded multi-map of keyed FIFO buckets with TTL-based eviction (lazy on `TryTake`, eager via `DrainStale`) and an explicit `onUnmatched` callback fired for each evicted entry. Lives in `src/Mithril.Shared/Correlation/`. Has three load-bearing invariants: `DrainStale` discipline (required for unmatched-key buckets to not grow unbounded), unmatched-callback exception aggregation, and monotonic-time invariant.

See also: [Tier 1 (correlation)](#tier-14-correlation), [Cross-source](#cross-source).
Canonical source: [`docs/cross-source-correlation.md` §Tier 1](cross-source-correlation.md).

### Tier 1–4 (correlation)

The decision tree for cross-source correlation patterns. **Tier 1** — keyed correlation: both payloads carry a shared join key, arrival order unbounded, bounded window (uses [PendingCorrelator](#pendingcorrelatortkeytreq)). **Tier 2** — causal protocol state machine: request/response with no shared key, paired by arrival order within a TTL window (per-consumer SM, no shared primitive). **Tier 3** — live read-order tiebreak using `L0 ReadMonotonicTicks`, gated on `L1 LogEnvelope<T>.IsReplay == false`. **Tier 4** — order-insensitive consumer (idempotent set-semantics for the irreducible case). The hierarchy survives the world-sim migration: it relocates to the [view layer](#view) for cross-world joins; the patterns themselves are unchanged.

⚠ Note: "Tier" is overloaded across three namespaces in the corpus. Tier 1–4 here are correlation patterns; the orchestration plan uses "Tier 1/2/2.5/3" for verification gates; the orchestrator's ready-set sort uses "Tier 1/2" for priority. Different namespaces, same word.

See also: [PendingCorrelator](#pendingcorrelatortkeytreq), [Cross-source](#cross-source), [Cross-world](#cross-world), [Same-game-second](#same-game-second).
Canonical source: [`docs/cross-source-correlation.md`](cross-source-correlation.md).

### #661 passthrough refinement

The clarification that a view's [composer](#composer)-for-recognition rule has an explicit unified-semantic-surface exception: a view may publish a single folder change event through to its bus, with a different type name, when that change event already IS the view's surface for the cross-world layer. The renaming is for consumers above the world layer; the underlying semantic is single-source.

Worked example: [InventoryItemRemoved](#inventoryitemremoved) is a passthrough of [PlayerInventoryRemoved](#playerinventoryremoved). No multi-frame composition is meaningful for inventory removals because chat doesn't report removals at instance-id granularity. The view publishes the player-side event under the view's surface name; consumers receive `Frame<InventoryItemRemoved>` on the view's bus without knowing the underlying source.

The exception is narrow: it applies only when the view's surface event is the unified-semantic-surface name for what's already a single-source change event. It does NOT license arbitrary world-bus → view-bus relabeling.

See also: [Composer](#composer), [View](#view), [InventoryItemRemoved](#inventoryitemremoved), [PlayerInventoryRemoved](#playerinventoryremoved).
Canonical source: GitHub #661.

---

## Phase / migration vocabulary

### Cross-FSM peek

A consumer state machine synchronously reading state from another state machine mid-handler (e.g., `Arwen.CalibrationService.OnItemDeleted` → `IInventoryService.TryResolve`). Works in live play because per-pump scheduling happens to settle into the right order; races under replay-from-session-start. Three exist in-repo: Arwen → Inventory, Legolas → PlayerAreaTracker/AreaCalibration/SurveyFlow, Samwise → GardenStateMachine. Under world-sim's declared dispatch order, intra-world peeks become coherent; cross-world peeks move through the [view layer](#view).

See also: [View](#view), [Per-frame resolution](#per-frame-resolution).
Canonical source: [`docs/world-sim-migration-audit.md`](world-sim-migration-audit.md); `docs/module-signal-map.md` §Cross-cutting observations.

### Cross-phase invariant

An assertion run between phases of the orchestration plan to check that the system is still cohesive (e.g., "after Phase 2, `IInventoryService` is now `IInventoryView`; all six consumers work against the view"). Distinct from per-task tests. A failure pauses phase advancement.

See also: [Phase (0a/0b/1/2/3/4/parallel)](#phase-0a0b1234parallel), [Verification gates](#verification-gates).
Canonical source: [`docs/world-simulator-orchestration-plan.md` §Cross-phase invariants](world-simulator-orchestration-plan.md).

### Dep graph

The dependency graph of world-sim migration issues. YAML-encoded in `docs/world-simulator-orchestration-plan.md` for the planned chain (#615 → #616 / #617 → #618 → #602 / #603 → …). The shepherd computes the live dep graph each tick as the UNION of YAML nodes + open issues with `module:world-sim` label + `Blocks: #N` / `Depends on: #N` edges parsed from issue bodies. Phase 1–4 sit on the main chain; parallel-track items have no dependency on phases.

See also: [Ready set](#ready-set), [Ready task](#ready-task), [Phase (0a/0b/1/2/3/4/parallel)](#phase-0a0b1234parallel).
Canonical source: [`docs/world-simulator-orchestration-plan.md` §Dependency graph](world-simulator-orchestration-plan.md); [`docs/world-sim-shepherd.md` §Dep graph derivation](world-sim-shepherd.md).

### Phase (0a/0b/1/2/3/4/parallel)

The taxonomy of world-sim migration work. **0a** — foundation contracts (#615, sequential). **0b** — world shells (#616, #617, parallel after 0a). **1** — validation (#618, the first folder migration). **2** — splits (#602, #603 in parallel). **3** — split-dependents (#608, #607, #606). **4** — wall-clock + scheduling (#609, #613). **parallel** — foundation-independent track (#610–#612, #604, #605) that can run anytime.

See also: [Dep graph](#dep-graph), [Cross-phase invariant](#cross-phase-invariant).
Canonical source: [`docs/world-simulator-orchestration-plan.md` §Dependency graph](world-simulator-orchestration-plan.md).

### Pure projector

A component (typically a module) that consumes inputs and emits derived state without owning any FSM mutation. Examples per `module-signal-map.md`: Pippin (modulo one fold), Elrond, Bilbo, Silmarillion, Celebrimbor — five of ten modules. Pure projectors need no behavioural migration under world-sim — they're a registration / wrapper pass once the view layer exists.

See also: [Source spanning](#source-spanning), [Cross-FSM peek](#cross-fsm-peek).
Canonical source: [`docs/module-signal-map.md` §Cross-cutting observations](module-signal-map.md).

### Ready set

The set of [dep-graph](#dep-graph) nodes the shepherd considers dispatchable on a given tick: open issues whose incoming "depends-on" edges all point to closed issues, that don't carry `orchestrator-dispatch:<N>` or `orchestrator-blocked` labels, and that are sorted into two tiers (Tier 1 = planned migration tasks by phase order; Tier 2 = follow-ons by issue number).

⚠ Note: the "Tier 1 / Tier 2" within the ready-set sort is a third "Tier" namespace, separate from [Tier 1–4 (correlation)](#tier-14-correlation) and the verification-gate tiers in the orchestration plan.

See also: [Dep graph](#dep-graph), [Ready task](#ready-task), [Shepherd](#shepherd).
Canonical source: [`docs/world-sim-shepherd.md`; `.claude/agents/world-sim-shepherd.md`](../.claude/agents/world-sim-shepherd.md).

### Ready task

A migration task whose `depends_on` issues are all closed and which isn't itself closed/in-progress. The shepherd picks the highest-priority ready task each tick.

See also: [Ready set](#ready-set), [Dep graph](#dep-graph).
Canonical source: [`docs/world-simulator-orchestration-plan.md` §How an orchestrator should use this file](world-simulator-orchestration-plan.md).

### Sleeper blocker

A migration concern that wouldn't surface in routine static review but blocks the migration once attempted. Per `world-sim-migration-audit.md`'s executive summary: three exist in the codebase (notably `_seededStackSizes` reconcile in `IInventoryService`, chat-stream source-shape choice for `IChatWorld`, view-layer clock semantics).

See also: [Source spanning](#source-spanning).
Canonical source: [`docs/world-sim-migration-audit.md` §Executive summary, §Recommendations](world-sim-migration-audit.md).

### Source spanning

A classification axis in `world-sim-migration-audit.md`. A component "spans both sources" if it subscribes to both Player.log and chat streams. Two exist in the codebase: `IInventoryService` (Player.log + chat via `PendingCorrelator`) and `SarumanCodebookService` (mutated by both `SarumanDiscoveryIngestionService` and `SarumanChatIngestionService`). Both must [split](#split-migration-action) per principle 3.

Note: this entry references the pre-split historical names by design — it describes the pre-migration state that the split-migration verb resolves. Post-split target names are [IPlayerInventoryState](#iplayerinventorystate)/[IChatInventoryState](#ichatinventorystate)/[IInventoryView](#iinventoryview) for inventory and [IPlayerWordOfPowerDiscoveryState](#iplayerwordofpowerdiscoverystate)/[IChatWordOfPowerState](#ichatwordofpowerstate)/[IWordOfPowerView](#iwordofpowerview) for WoP.

See also: [Cross-source](#cross-source), [Split (migration action)](#split-migration-action).
Canonical source: [`docs/world-sim-migration-audit.md`](world-sim-migration-audit.md); also `docs/world-simulator.md` principle 3.

### Split (migration action)

The migration verb for a service that spans both sources: split it into a Player.log half + a chat half + a [view](#view) above them. Worked examples: `IInventoryService` → [IPlayerInventoryState](#iplayerinventorystate) + [IChatInventoryState](#ichatinventorystate) + [IInventoryView](#iinventoryview); `SarumanCodebookService` → [IPlayerWordOfPowerDiscoveryState](#iplayerwordofpowerdiscoverystate) + [IChatWordOfPowerState](#ichatwordofpowerstate) + [IWordOfPowerView](#iwordofpowerview).

See also: [Source spanning](#source-spanning), [View](#view), [IInventoryView](#iinventoryview), [IWordOfPowerView](#iwordofpowerview).
Canonical source: [`docs/world-simulator.md` principle 3, §Migration path; `docs/world-sim-migration-audit.md`](world-sim-migration-audit.md).

### Transition gate (vs stamp)

The reviewer's classification of `_time.GetUtcNow()` call sites. A **transition gate** is a wall-clock read that gates a state-machine transition (e.g., `if (now - p.UpdatedAt > ttl) Prune(...)`); it leaks real wall-clock into the determinism boundary and must migrate to `IWorldClock.Now`. A **stamp** writes the current time onto a record without gating anything (e.g., `entry.LastObservedAt = TimeProvider.System.GetUtcNow()`); it's allowed. The audit enumerates 9 transition gates across 4 components.

See also: [Wall-clock TTL](#wall-clock-ttl), [Replay-determinism](#replay-determinism), [IWorldClock](#iworldclock).
Canonical source: [`docs/world-sim-migration-audit.md` §8 Wall-clock _time.GetUtcNow()](world-sim-migration-audit.md).

### Verification gates

The orchestration plan's three-tier check structure for each task PR: **Tier 1 build** (`dotnet build Mithril.slnx`), **Tier 2 test** (`dotnet test`), **Tier 2.5 shepherd review** (specialist + generic reviewers in parallel via the shepherd), and **Tier 3 system** (replay-determinism, shell smoke, perf benchmark) for medium/high-risk tasks.

⚠ Note: the "Tier" numbering here is a sub-namespace of the orchestration plan, distinct from [Tier 1–4 (correlation)](#tier-14-correlation) and from the shepherd's ready-set sort tiers.

See also: [Cross-phase invariant](#cross-phase-invariant), [Phase (0a/0b/1/2/3/4/parallel)](#phase-0a0b1234parallel).
Canonical source: [`docs/world-simulator-orchestration-plan.md` §Verification gates](world-simulator-orchestration-plan.md).

### Wall-clock TTL

A transition gated by `_time.GetUtcNow()` deltas (e.g., a 5s correlation window). Distinguished from event-time deltas (`envelope.Payload.Timestamp` arithmetic) which are part of the log stream and replay-deterministic. Under world-sim, every wall-clock TTL in a state-decision path migrates to `IWorldClock.Now`; nine such sites exist per the audit.

See also: [Transition gate (vs stamp)](#transition-gate-vs-stamp), [IWorldClock](#iworldclock), [Replay-determinism](#replay-determinism).
Canonical source: [`docs/module-signal-map.md` §Conventions; `docs/world-sim-migration-audit.md`](world-sim-migration-audit.md).

---

## Agent + orchestration vocabulary

> **Architectural note.** Through v2.1 (PR #653), the orchestrator role lived as a separate agent (`world-sim-orchestrator`) dispatching the shepherd at depth 2. v3 (PR #658) collapsed orchestrator + shepherd into a single agent at `/loop` depth. The terms below — context pack, verdict, escalation reason, ready set, dep graph — survive the collapse unchanged in semantics; their emitter is now uniformly the [shepherd](#shepherd). Historical doc cross-references to `world-sim-orchestrator.md` no longer resolve (the file was deleted in #658); they were folded into `docs/world-sim-shepherd.md` and `.claude/agents/world-sim-shepherd.md`.

### Circuit breaker

The manual kill switch on the world-sim driver: applying the `pause` label to umbrella issue #601 causes the [shepherd](#shepherd)'s step 0 to exit without scheduling a next tick, halting the /loop until the label is removed. Also covers the umbrella-closed terminal condition.

See also: [Pause label](#pause-label), [Shepherd](#shepherd).
Canonical source: [`docs/world-sim-shepherd.md` §Safety / stop conditions; `.claude/agents/world-sim-shepherd.md` §0. Circuit breaker](../.claude/agents/world-sim-shepherd.md).

### Context pack

A 5–15K-token bundle the [shepherd](#shepherd) builds once at intake (issue body + phase preconditions + tooling rules + workflow rules) and passes inline to the initial [worker](#worker) dispatch. Subsequent SendMessages add only the iteration-specific delta. Avoids cold re-reads of CLAUDE.md + docs by each subagent each iteration.

See also: [Shepherd](#shepherd), [Worker](#worker).
Canonical source: [`docs/world-sim-shepherd.md` §Building the context pack; `.claude/agents/world-sim-shepherd.md`](../.claude/agents/world-sim-shepherd.md).

### Cross-tick recovery

The shepherd's step-1 logic: process a prior shepherd's terminal-verdict marker comment left on a PR after a tick crashed mid-handler. Rare in practice (happy path is inline-handling in step 2's worker return). Skipped when the linked issue already carries `orchestrator-blocked` or the PR is merged + issue closed.

See also: [Verdict marker](#verdict-marker), [Shepherd](#shepherd).
Canonical source: [`docs/world-sim-shepherd.md`; `.claude/agents/world-sim-shepherd.md`](../.claude/agents/world-sim-shepherd.md).

### Degraded mode

The [shepherd](#shepherd)'s fallback when `TeamCreate` is unavailable in the harness. Worker is dispatched fire-and-forget without a team; reviewers are performed analytically inline by the shepherd; no SendMessage continuity, so any findings force `verdict: needs-human` with `escalation_reason: degraded_mode_cannot_iterate`. Anomalies array carries the "operated in inline mode — Teams unavailable" marker.

See also: [Shepherd](#shepherd), [Worker](#worker), [Verdict](#verdict).
Canonical source: [`docs/world-sim-shepherd.md` §Inline degraded mode; `.claude/agents/world-sim-shepherd.md` §Inline degraded mode](../.claude/agents/world-sim-shepherd.md).

### Escalation reason

Enum string accompanying a [shepherd](#shepherd) `needs-human` verdict. Values: `max_iterations`, `same_issue_class`, `worker_no_progress`, `human_review`, `merge_conflict`, `closed_without_merge`, `initial_implementation_failed`, `nothing_to_do`, `decomposed`, `needs_input`, `worker_failed`, `merge_command_failed`, `degraded_mode_cannot_iterate`, `shepherd_return_unparseable`.

See also: [Verdict](#verdict), [Shepherd](#shepherd), [Same-issue-class detection](#same-issue-class-detection).
Canonical source: [`docs/world-sim-shepherd.md` §Output contract; `.claude/agents/world-sim-shepherd.md`](../.claude/agents/world-sim-shepherd.md).

### Follow-on

An out-of-scope finding surfaced by the [shepherd](#shepherd) or reviewer during PR review — typically a bug or refactor adjacent to but not within the current PR's scope. The shepherd carries follow-ons in its return JSON's `follow_ons` array (shape: `title` / `files` / `blocks` / `body`); the shepherd itself files each as a GitHub issue with labels `module:world-sim,orchestrator-followup` on `verdict: merged` or `verdict: decomposed`.

See also: [Verdict](#verdict), [Shepherd](#shepherd).
Canonical source: [`docs/world-sim-shepherd.md` §Posting the combined review comment, §Follow-on filing](world-sim-shepherd.md).

### Orchestrator (historical)

The pre-v3 per-project autonomous driver of the world-sim migration umbrella (#601). Lived as a separate agent file `.claude/agents/world-sim-orchestrator.md` and design notebook `docs/world-sim-orchestrator.md`. Ran as a tick-based /loop dispatching the shepherd at depth 2; each tick took one action then exited with `ScheduleWakeup`.

⚠ Deprecated post-#658: the orchestrator + shepherd were collapsed into a single agent at /loop depth (the [shepherd v3](#shepherd)). The orchestrator agent file and design notebook were deleted; the per-tick logic (circuit breaker, dep graph, ready set, follow-on filing, escalation routing) was folded into the shepherd's intake. The slash command name `world-sim-orchestrate-tick` was preserved for /loop continuity. Historical doc cross-references to `world-sim-orchestrator.md` no longer resolve.

The term "orchestrator" still appears in label names (`orchestrator-dispatch:<N>`, `orchestrator-blocked`, `orchestrator-followup`) and the slash command name (`world-sim-orchestrate-tick`) — those are preserved by intent.

See also: [Shepherd](#shepherd), [Tick](#tick), [Circuit breaker](#circuit-breaker), [Cross-tick recovery](#cross-tick-recovery), [`orchestrator-blocked` label](#orchestrator-blocked-label), [`orchestrator-dispatch` label](#orchestrator-dispatchn-label), [`orchestrator-followup` label](#orchestrator-followup-label).
Canonical source: [`docs/world-sim-shepherd.md` §v3 vs v2.1 — what changed](world-sim-shepherd.md); GitHub #656, #658.

### `orchestrator-blocked` label

GitHub label applied by the [shepherd](#shepherd) to a task issue whose delivery returned `needs-human` or `conflict`. Skipped from future ready-set computation until removed by a human (signalling the escalation has been addressed). The label name retains the `orchestrator-` prefix from the v2.1 era for stability across the v3 collapse.

See also: [Shepherd](#shepherd), [Verdict](#verdict).
Canonical source: [`docs/world-sim-shepherd.md` §Per-tick decision logic](world-sim-shepherd.md).

### `orchestrator-dispatch:<N>` label

GitHub label applied per-issue by the [shepherd](#shepherd) at dispatch time (`N` is the issue number). Marks the issue as in-flight to prevent re-dispatch. Removed on terminal shepherd return. The label name retains the `orchestrator-` prefix from the v2.1 era for stability.

See also: [Shepherd](#shepherd).
Canonical source: [`docs/world-sim-shepherd.md` §Dispatch](world-sim-shepherd.md).

### `orchestrator-followup` label

GitHub label applied automatically by the [shepherd](#shepherd) when filing a [follow-on](#follow-on) issue from its return JSON. Distinguishes auto-filed follow-ons from planned phase work in the ready-set sort. The label name retains the `orchestrator-` prefix from the v2.1 era for stability.

See also: [Follow-on](#follow-on), [Shepherd](#shepherd), [Ready set](#ready-set).
Canonical source: [`docs/world-sim-shepherd.md` §Follow-on filing](world-sim-shepherd.md).

### `pause` label

Manual circuit-breaker label on umbrella issue #601. While present, the [shepherd](#shepherd)'s step 0 exits without `ScheduleWakeup`, halting the /loop until removed.

See also: [Circuit breaker](#circuit-breaker), [Shepherd](#shepherd).
Canonical source: [`docs/world-sim-shepherd.md` §Safety / stop conditions](world-sim-shepherd.md).

### Same-issue-class detection

The [shepherd](#shepherd)'s review-loop escalation signal: if two consecutive iterations' findings cite the same file:line range (within ±5 lines) or the same principle number, treat as `escalation_reason: same_issue_class` and exit `needs-human`. SendMessage-resumed reviewers naturally surface this in their text as a fallback signal.

See also: [Shepherd](#shepherd), [Escalation reason](#escalation-reason).
Canonical source: [`docs/world-sim-shepherd.md` §"Same class of issue" detection; `.claude/agents/world-sim-shepherd.md`](../.claude/agents/world-sim-shepherd.md).

### Shepherd

The per-project autonomous driver of the world-sim migration umbrella (#601). Runs as a tick-based /loop at /loop depth (v3, post-#658). Each tick takes one action then exits with `ScheduleWakeup`. Five-step priority list per tick (folded in from the prior orchestrator): (0) circuit breaker, (1) cross-tick recovery, (2) intake + dep-graph + ready-set, (3) delivery (initial implementation → review-fix → merge), (4) follow-on filing + escalation routing.

Owns the work end-to-end: picks the next ready issue from the dep graph, spawns the initial-implementation [worker](#worker), opens the PR, runs the review-fix loop with the specialist + generic [reviewers](#reviewer), merges the PR, returns a structured [verdict](#verdict) via JSON. Creates a Team at intake (`TeamCreate`) so worker + reviewers can be spawned as named teammates and resumed via `SendMessage` across iterations. Five terminal verdicts: `merged`, `needs-human`, `conflict`, `nothing-to-do`, `decomposed`.

Previous architecture: through v2.1 (PR #653), orchestrator (depth 1) + shepherd (depth 2) were two separate agents. The v3 collapse (#656 / PR #658) merged them at /loop depth because the harness only reliably exposes `Agent` at depth 1. The slash command name (`world-sim-orchestrate-tick`) and the `orchestrator-*` label names are preserved for continuity.

Tools: `Read`, `Grep`, `Glob`, `Bash`, `Agent`, `SendMessage`, `TeamCreate`, `TeamDelete`, `mcp__ccd_session__spawn_task`, `ScheduleWakeup`, `ToolSearch`.

See also: [Orchestrator (historical)](#orchestrator-historical), [Worker](#worker), [Reviewer](#reviewer), [Verdict](#verdict), [Context pack](#context-pack), [Degraded mode](#degraded-mode), [Tick](#tick), [Ready set](#ready-set), [Circuit breaker](#circuit-breaker), [Cross-tick recovery](#cross-tick-recovery).
Canonical source: [`docs/world-sim-shepherd.md`; `.claude/agents/world-sim-shepherd.md`](../.claude/agents/world-sim-shepherd.md).

### Tick

One invocation of the [shepherd](#shepherd) by `/loop`. Each tick takes exactly one action from the 5-step priority list, then exits with `ScheduleWakeup` for the next tick. Real-work ticks schedule 60s wakeups (within prompt-cache TTL); idle ticks schedule 1800s+.

See also: [Shepherd](#shepherd), [Cross-tick recovery](#cross-tick-recovery).
Canonical source: [`docs/world-sim-shepherd.md`](world-sim-shepherd.md).

### Verdict

The terminal state of a [shepherd](#shepherd) tick, reported via fenced JSON in the shepherd's final message. Five values: `merged` (PR merged successfully), `needs-human` (escalation — see [escalation reason](#escalation-reason)), `conflict` (merge conflict against base couldn't auto-resolve), `nothing-to-do` (worker concluded no work needed), `decomposed` (worker filed sub-issues). Plus the no-delivery results `idle`, `no-action`, and `circuit-breaker` reported when the tick took no delivery action.

See also: [Shepherd](#shepherd), [Escalation reason](#escalation-reason), [Verdict marker](#verdict-marker).
Canonical source: [`docs/world-sim-shepherd.md` §Output contract](world-sim-shepherd.md).

### Verdict marker

The machine-readable HTML-comment first-line contract on per-iteration PR comments. Three flavors: `<!-- shepherd-verdict: ... -->` (posted by the shepherd; values: `ready-to-merge`, `dispatching worker`, `needs-human`), `<!-- world-sim-review-verdict: ... -->` (posted within a specialist reviewer's output; values: `clean`, `findings`), `<!-- generic-review-verdict: ... -->` (within a generic reviewer's output; values: `clean`, `findings`). The shepherd's cross-tick recovery parses the `shepherd-verdict` marker; the shepherd parses the two reviewer markers.

See also: [Shepherd](#shepherd), [Reviewer](#reviewer), [Cross-tick recovery](#cross-tick-recovery).
Canonical source: [`docs/world-sim-shepherd.md` §Posting the combined review comment; `.claude/agents/world-sim-reviewer.md` §Output format](../.claude/agents/world-sim-reviewer.md).

### Worker

The [shepherd](#shepherd)'s dispatched implementation teammate (subagent type: `general-purpose`). Receives the [context pack](#context-pack) on initial dispatch, implements the issue, runs `dotnet build` + `dotnet test`, opens a PR with `Closes #<issue>`, returns a structured `outcome:` line (`success`, `nothing-to-do`, `decomposed`, `needs-input`, `failed`). Subsequent review-fix iterations reach the same worker via SendMessage by name (in normal mode) or are not possible (in degraded mode).

See also: [Shepherd](#shepherd), [Reviewer](#reviewer), [Context pack](#context-pack).
Canonical source: [`docs/world-sim-shepherd.md` §Spawning named teammates, §Building the worker fix message](world-sim-shepherd.md).

### Reviewer

A [shepherd](#shepherd)-dispatched code reviewer teammate. Two flavors: **generic-reviewer** (subagent type: `general-purpose`, prompt inlined per the shepherd doc's §Generic code review prompt — checks bugs, CLAUDE.md compliance, significant code-quality issues) and **specialist-reviewer** (subagent type: `world-sim-reviewer` — checks principle adherence 1–13, phase-aware migration preconditions, replay-determinism, audit cross-reference). Both emit a [verdict marker](#verdict-marker) on the first line of their output and are resumed via SendMessage on subsequent iterations.

See also: [Shepherd](#shepherd), [Worker](#worker), [Verdict marker](#verdict-marker), [Replay-determinism](#replay-determinism).
Canonical source: [`docs/world-sim-shepherd.md`; `.claude/agents/world-sim-reviewer.md`](../.claude/agents/world-sim-reviewer.md).

---

## Infrastructure terms

### `IChatLogStream`

The chat log file tail surface, consumed by `ChatLogStream`. Under world-sim, the [ChatWorld](#chatworld) folder is the canonical consumer. The pre-migration in-module consumers (`Legolas.LogIngestionService` per #606, the pre-#602 cross-source `IInventoryService` chat tail, and the legacy `SarumanChatIngestionService`) have all retired; their successors subscribe to ChatWorld-routed events rather than to the raw stream.

See also: [ChatWorld](#chatworld), [L0 / L1 / L2](#l0--l1--l2).
Canonical source: [`docs/module-signal-map.md`](module-signal-map.md); `src/Mithril.Shared/Logging/`.

### `IClassifiedPlayerLogStream`

The unified classified Player.log pipe, post-#556. Carries `IClassifiedPlayerLogLine` payloads (LocalPlayer, CombatActor, SystemSignal, anomaly). Consumed today by `IPlayerWeatherTracker`, `IPlayerPinTracker`, `IPlayerPositionTracker`, and (post-#556) `IPlayerAreaTracker`. Under world-sim, [PlayerWorld](#playerworld) is the canonical consumer.

See also: [PlayerWorld](#playerworld), [L0 / L1 / L2](#l0--l1--l2).
Canonical source: [`docs/module-signal-map.md` §Conventions](module-signal-map.md).

### L0 / L1 / L2

The implicit log-pipeline layer numbering used across the world-sim corpus and `cross-source-correlation.md`. **L0** = the raw tail layer (`ReadMonotonicTicks` is minted here per line). **L1** = the classified line layer (`LogEnvelope<T>.IsReplay` is carried here; `ILogStreamDriver.Subscribe<T>` is the L1 surface). **L2** = the parsed-event layer consumed by GameState services.

⚠ Note: the L0/L1/L2 hierarchy is referenced but not explicitly defined anywhere in the corpus. The most concrete description is in `cross-source-correlation.md` §Tier 3, which describes how L0 + L1 cooperate for live-only tiebreak.

See also: [Sequence](#sequence-frame-ordering), [`IChatLogStream`](#ichatlogstream), [`IClassifiedPlayerLogStream`](#iclassifiedplayerlogstream).
Canonical source: [`docs/cross-source-correlation.md` §Tier 3](cross-source-correlation.md); also `docs/module-signal-map.md` §Conventions.

### Sequence (frame ordering)

A monotonic ordering field carried on log envelopes (and through to frames) within a single source. Used as the [merger](#world) tie-breaker for native frames within a [world](#world). Distinct from [`ReadMonotonicTicks`](#l0--l1--l2) (the cross-source tiebreak signal at L0).

⚠ Note: `Sequence` is the field-level mechanism for in-source ordering; the term isn't formally defined as a glossary entry in any doc but is referenced ubiquitously (e.g., `world-simulator.md` §Determinism properties: "tie-breaking by `Sequence` for native frames").

See also: [Frame](#frame), [Per-frame resolution](#per-frame-resolution), [L0 / L1 / L2](#l0--l1--l2).
Canonical source: [`docs/world-simulator.md` §Determinism properties; `docs/module-signal-map.md`](module-signal-map.md).
