# World simulator — design notebook

Design rationale for the three-layer world-simulator architecture: source streams → worlds → composition views → modules. This is the converged shape from the design conversation that landed [`module-signal-map.md`](module-signal-map.md); read that doc first for the current topology, then this one for where it's going.

**Status:** design notebook, not implementation spec. Captures the architectural commitments, contracts, and migration plan. Concrete contracts may iterate as implementation surfaces issues; principles are load-bearing.

**Companion docs:**
- [`module-signal-map.md`](module-signal-map.md) — the topology this design feeds against (read first).
- [`world-sim-migration-audit.md`](world-sim-migration-audit.md) — line-by-line audit of every state-holder against the principles + migration plan in this notebook. 15 components classified; 5 need behavioural changes; 3 sleeper blockers identified. Read before starting any migration item.

## Vocabulary

- **PlayerWorld** — the world reconstructed from `Player.log`. NOT "the player character" — that's a separate entity (`Character`) tracked within either world. When this doc says "Player" it always means "derived from Player.log."
- **ChatWorld** — the world reconstructed from the chat log files.
- **Folder / composer / producer** — the three state-machine kinds (principle 10). Folders apply frames; composers correlate change events; producers source external-input frames (log tails).
- **Frame** — `(timestamp, payload)`. The unifying primitive.
- **Change event** — what a folder emits when applying a frame. World-internal.
- **Domain frame** — what a composer emits when its pattern is satisfied. Cross-world consumable (published to the world's bus).
- **World** vs **world runtime** — used interchangeably. "World" is short.
- **View** — a composer operating above the worlds, subscribing to one or more world buses, exposing composed state to modules.
- **`WorldMode`** — `Replaying` (draining recorded frames) or `Live` (caught up to source stream tail). Each world tracks independently. State derivation is mode-agnostic; side-effect-emitting consumers gate on `Mode == Live`.
- **Canonical time-related domain events** (emitted on each world's bus, principle 13):
  - `CalendarTimeAdvanced(Now, Mode)` — fires on second-resolution world-clock advancement
  - `TimeOfDayShift(from, to, at, Mode)` — composer-derived; PG in-game shift boundaries
  - `ModeChanged(from, to, at)` — fires on `Replaying ↔ Live` transition
- **Three categories of data** (see Three Categories section):
  - **World-derived state** — events from worlds (PlayerWorld, ChatWorld) and views
  - **External shared data sources** — `Mithril.Reference`, `Mithril.GameReports`, `ICommunityCalibrationService`, `IServerCatalogService` (planned)
  - **Module-owned adjacent state** — user-driven, module-internal, persisted alongside the module
- **Per-session scope tier** — `(Server, Character)` derived from `IGameSessionService`. The unit of per-character-per-server partitioning. Used by both world-character-state and module-owned state. Replaces the legacy "per-character" framing in `PerCharacterView<T>` (which becomes per-session-keyed).

---

## Why this exists

Mithril today reconstructs PG world state by tailing two log streams (Player.log and chat) and exposing state via a collection of services. The current architecture has accumulated three structural problems:

1. **Services span both sources.** `IInventoryService` consumes both Player.log and chat, fuses them internally via `PendingCorrelator`. Same for Saruman's codebook. This conflates per-source determinism (Player.log is well-ordered in itself; chat is well-ordered in itself; their cross-stream order is not data-derivable per [`cross-source-correlation.md`](cross-source-correlation.md)).
2. **Wall-clock TTL gates leak determinism.** Several consumers gate transitions on `DateTime.UtcNow` deltas. Under replay, the same log produces different state because real elapsed time differs.
3. **Cross-FSM synchronous peeks** (`Arwen.CalibrationService` → `IInventoryService.TryResolve`, `Legolas.PlayerLogIngestionService` → `PlayerAreaTracker.CurrentArea`, etc.) work only because per-pump scheduling happens to settle into the right order. Under replay-from-session-start they race.

The converged answer to all three: a **clocked world simulator** that owns the canonical frame stream, dispatches handlers synchronously per frame in declared dependency order, and exposes its simulated wall-clock so wall-clock TTL gates become replay-deterministic. Plus the structural commitment that **no service spans both sources** — chat and Player.log get their own independent worlds, and cross-source composition lives in a view layer above them.

---

## Core principles

1. **Frame = `(timestamp, payload)`.** The unifying primitive. Every input that mutates simulated state is a frame; every producer stamps its output with the event time the frame represents (not the wall-clock when it was synthesized). Each world is a timestamp-ordered merger over its N producers.
2. **Two worlds with sealed output boundaries; views consume across them.** `PlayerWorld` is deterministic over `IClassifiedPlayerLogStream`. `ChatWorld` is deterministic over the chat stream. Each world has its own internal pipeline (frames → folders → change events → composers → domain frames) and its own output bus carrying its domain frames. Worlds don't query each other and don't send messages to each other — they're sealed at the bus. Cross-world consumers (views) subscribe to both world buses; nothing flows back into a world from above.
3. **If a service currently consumes both chat and Player.log, it must be split.** No cross-source services. Each service lives in exactly one world.
4. **Cross-source composition lives in a view layer above the worlds.** Views are composers operating one layer up — they subscribe to one or more world buses, maintain composed state, expose their own bus surface (or read-only API) to modules. Cross-world consumers (modules needing data from both Player.log and chat) always go through a view. Single-world consumers (modules needing only one source's state) may subscribe directly to that world's bus — no pass-through view required, since the view layer's purpose is *composition across worlds*, not API uniformity.
5. **Dual-clock: simulated wall-clock + frame index.** `Now : DateTimeOffset` advances by frame timestamps; `Frame : long` strictly monotonic per applied frame. Coarse clock answers "how much simulated time has passed?" (1-second resolution because PG's timestamps are); fine clock answers "are we at the same point in the trajectory?" Together they identify a unique moment.
6. **Scope: reference / world (per-server) / character (per-server-per-character).** Per [`module-signal-map.md`](module-signal-map.md) — PG has multiple servers; world state is per-server; character state is per-character within a server. Sims partition along these scopes.
7. **Both streams self-scope independently.** Player.log identifies `(Server, Character)` from its own intra-source signals (`Servers:` catalog + `EVENT(Ok): connected` + `LoginBanner`); chat identifies its own scope from the chat banner. No cross-source coupling for scope.
8. **Tier 1/2 correlator pattern survives, relocates to the view layer.** [`cross-source-correlation.md`](cross-source-correlation.md)'s tier hierarchy stays valid as a pattern catalog for view-layer joins, but loses its in-repo "reference implementation" pointers (Inventory and Motherlode both migrate).
9. **Chat world replays from PG-session-start, symmetric with Player world.** Chat is *not* live-only — that's a today-implementation choice we're explicitly replacing. The determinism contract views can offer their consumers is upper-bounded by the determinism of both worlds at *matching* simulated time windows. Asymmetric replay (Player.log replays session-start, chat live-only) = asymmetric view inputs = no replay-determinism claim possible. So both worlds drain from the PG-session-start chat banner; the chat-tail seeks to the most recent chat banner matching the current Player.log session (banner-by-banner pairing on `(Character, close-in-time)`), then emits forward. Cost is bounded (~hundreds of KB of chat per day vs the 12 MB of Player.log already replayed); benefit is the FileSystemWatcher reconcile workaround in `IInventoryService` retires (chat replay covers pre-attach stack sizes natively).

10. **Three state-machine kinds: folders, composers, producers.** Each has a distinct signature and dispatch position:
    - **Folders** — `Frame<TPayload>` in, change events out. One frame is dispatched to exactly one folder (per the routing rules established by frame type). Folders mutate world state; they live inside one world. Examples: `PlayerSkillStateService` (XP frame → skill snapshot mutation), `ChatInventoryStateMachine` (stack-observation frame → name-keyed observation).
    - **Composers** — change events in (one or more, possibly across event types), domain frames out, emitted when the multi-frame pattern is satisfied. Composers *recognize* multi-frame patterns in events PG already emits; they do not anticipate or synthesize PG behavior. Intra-world composers (e.g., Arwen gift detection — three same-source frames → `GiftObservation`) live inside one world. Cross-world composers are views (next principle). Composers chain via subscribe within a frame's resolution — they never re-emit frames into the world's merger.
    - **Producers** — sources of external-input frames. Log tails (Player.log, chat log) are the canonical examples; future possibilities include filesystem reconcile for character export. Producers are NOT a mechanism for user-driven scheduling — user-side concerns (Gandalf timers, alarm scheduling) consume world domain events and run their own module-internal logic against them; they do not register producers in a world's merger. The world is sealed at its input.

11. **Per-frame resolution is a finite DAG traversal; no cycles, no merger re-entry.** Within a world, a single frame dispatches to its folder; folder emits change events; intra-world composers subscribed to those change events run and possibly emit domain frames; composers subscribed to *those* domain frames run; resolution continues until no new events are emitted. The dispatch graph is topologically ordered (composers declare their input event types); resolution depth is bounded by the graph's depth. View-layer resolution is the same shape, one layer up: view-layer composers receive world domain frames, may emit higher-level domain frames consumed by other views or by modules; views never re-emit into a world.

12. **Each world tracks `Mode ∈ {Replaying, Live}`; side-effect-emitting consumers gate on `Mode == Live`.** During drain (catching up to the live tail from session-start), the world is in `Replaying`. Once drained and now blocking on the live source-stream tail for new frames, the world transitions to `Live`. State derivation is **mode-agnostic** — folders, composers, and views update internal state identically in both modes. **User-facing side effects** (audio alarms, window flash, OS notifications) gate on `Mode == Live` to avoid blasting the user with replays of yesterday's alarms when Mithril restarts. Mode flips are themselves observable: each world emits a `ModeChanged(from, to, at)` domain event on transition. Worlds transition independently — PlayerWorld may catch up at T1, ChatWorld at T2.

13. **Calendar time is a domain event, not a clock read.** The world clock itself is just "last applied frame's timestamp" — there's no continuous-time abstraction to query during idle (there's no idle anyway, because PG's logs are continuously noisy during play). Consumers that care about time progression subscribe to `CalendarTimeAdvanced(Now, Mode)` domain frames on the world's bus — emitted on second-resolution world-clock advancement (deduplicated within a wall-clock second). Calendar-time composers may derive higher-level events like `TimeOfDayShift(from, to, at, Mode)` for PG shift transitions. Module-side schedulers (Gandalf timer alarms, Samwise ripeness alarms) consume these events; compare their internal thresholds against the carried timestamp; fire (gated on `Mode == Live`). No real-wall-clock leaks into state machines or module-side scheduling — real wall-clock is used only inside the world's merger to know when to block on the live source-stream tail.

---

## Layered architecture

```
Source              World runtime                                    View layer            Modules
                    (internal: frame → change → domain)
─────               ───────────────────────────────────────────      ──────────────       ──────
                    ┌─────── PlayerWorld ───────────────────────┐
Player.log        → │ Merger ──→ Folders ──→ Composers ──→ Bus  │ ──┐
(replay from        │ ↑               (change events) (domain   │   │
 session-start)     │ │                                frames)   │   │
                    │ └──── Producers (wake-at-T, fs reconcile,         InventoryView    Samwise
                    │       eager TTL-eviction) ──────┘         │   │   (cross-world      Arwen
                    └───────────────────────────────────────────┘   │   composer,        …
                                                                    ├──→ subscribes to
                    ┌─────── ChatWorld ─────────────────────────┐   │   both world buses,
chat stream       → │ Merger ──→ Folders ──→ Composers ──→ Bus  │ ──┤   emits its own
(replay from        │ (chat-inventory mirror,                    │   │   domain frames)
 session-start)     │  chat-WoP spent)                           │   │
                    └───────────────────────────────────────────┘   │   WordOfPowerView   Saruman
                                                                    └──→ (codebook merge:
                    (other sources → other worlds if/when needed)         discovery + spent)
```

### Layer responsibilities

**Source streams** — raw, unfolded. Player.log frames carry source `Sequence` order; **chat replays from PG-session-start** (principle 9 — seeks to the matching chat banner, then emits forward).

**World runtime** — two worlds, each deterministic over its own source, each with its own internal pipeline. Each:
- Owns a single subscription to its source (plus producer registrations targeting that world's merger)
- Maintains a frame merger (priority queue keyed by timestamp; tie-breaking by `Sequence` for native frames, by declared producer priority for producer-emitted frames)
- Advances its own `IWorldClock` on each applied frame
- Dispatches frames synchronously to folders (one frame → one folder); folders emit change events
- Resolves composers within the frame (composers subscribed to change events fire; may emit domain frames; further composers consume those; until no new events)
- Publishes domain frames to its own output bus (sealed; no inter-world flow)
- Exposes folder state via `Current`/`TryGet` for synchronous reads by composers and views

**View layer** — composers operating above the worlds. Each view:
- Subscribes to one or more world buses (typically both, for cross-world composition)
- Maintains a stateful composed model (e.g., `InventoryView` maintains the fused inventory ledger with instance IDs + stack sizes)
- Exposes the composed state as the *canonical* surface for cross-world consumers
- Is deterministic over the worlds' bus emissions (which are themselves deterministic over their sources)
- Scope-checks `(Server, Character)` on cross-source joins as appropriate

**Modules** — terminal consumers. Cross-world modules subscribe to views. Single-world modules may subscribe directly to a world's bus — the view layer is for cross-world composition, not mandatory pass-through.

---

## Contracts

### Frame model

```csharp
namespace Mithril.WorldSim;

/// <summary>
/// One unit of simulated input. Producers stamp every frame with the event time
/// the frame represents (NOT the wall-clock when the producer fired).
/// </summary>
public readonly record struct Frame<TPayload>(
    DateTimeOffset Timestamp,
    TPayload Payload);
```

### `IWorldClock`

```csharp
/// <summary>
/// A world's simulated wall-clock. <see cref="Now"/> is always the timestamp of
/// the most recently applied frame — there is no live-mode interpolation, no
/// continuous-time abstraction. Consumers that care about time progression
/// subscribe to <c>CalendarTimeAdvanced</c> domain events on the world's bus.
/// Real wall-clock (<see cref="TimeProvider.System"/>) is used only inside the
/// world's merger for blocking on the live source-stream tail; never by folders,
/// composers, views, or modules.
/// </summary>
public interface IWorldClock
{
    /// <summary>
    /// Simulated wall-clock = timestamp of the most recently applied frame.
    /// Weakly monotonic at 1-second resolution (PG's timestamp precision).
    /// Multiple frames may share the same value. Reads during live-mode idle
    /// return the same value as immediately after the last frame applied.
    /// </summary>
    DateTimeOffset Now { get; }

    /// <summary>
    /// Strictly-monotonic frame index. Ticks once per applied frame.
    /// Identifies a unique point in the trajectory; tie-breaks within a
    /// wall-clock second; pairs with <see cref="Now"/> as the full identity
    /// of a simulated moment.
    /// </summary>
    long Frame { get; }

    /// <summary>
    /// Current world mode. <see cref="WorldMode.Replaying"/> while draining
    /// recorded frames toward the live tail; <see cref="WorldMode.Live"/> once
    /// caught up. Transition emits a <c>ModeChanged</c> domain event on the bus.
    /// Side-effect-emitting consumers gate on <c>Mode == Live</c>.
    /// </summary>
    WorldMode Mode { get; }
}

public enum WorldMode
{
    Replaying,
    Live,
}
```

### Producer interface

```csharp
/// <summary>
/// A source of timestamped frames feeding a world. Implementers include:
/// the L1 classified-pipe reader (Player.log frames), the chat tail (chat frames),
/// chat correlator completions (synthetic frames), filesystem reconcile (synthetic
/// frames stamped with export timestamps), wake-at-T schedulers (synthetic frames
/// for the target firing time), etc.
/// </summary>
public interface IFrameProducer<TPayload>
{
    /// <summary>
    /// Emits frames in ascending timestamp order. The world's merger is a priority
    /// queue keyed by <see cref="Frame{TPayload}.Timestamp"/>; producers must not
    /// emit out-of-order frames. Late-stamped frames (timestamp earlier than the
    /// world's clock) are clamped + warned by the world.
    /// </summary>
    IAsyncEnumerable<Frame<TPayload>> SubscribeAsync(CancellationToken ct);

    /// <summary>
    /// Used by the world's merger to break ties when two producers emit frames with
    /// identical timestamps. Lower priority dispatches first. Producer priorities
    /// must be declared at registration time; the world's tie-breaking is replay-
    /// deterministic over the producer set.
    /// </summary>
    int Priority { get; }
}
```

### Folder interface

```csharp
/// <summary>
/// One of three state-machine kinds (principle 10). Folders consume frames and emit
/// change events. One frame is dispatched to exactly one folder, determined by the
/// frame's payload type. Folders mutate world state; they live inside one world's
/// runtime.
/// </summary>
public interface IFolder<TPayload>
{
    /// <summary>
    /// Apply one frame to internal state. Returns the change events the mutation
    /// produced (empty if no observable change). Mutations must depend only on the
    /// frame's payload and the folder's own prior state. Never reads
    /// <see cref="DateTime.UtcNow"/> or any other real-time source; uses
    /// <paramref name="clock"/> for "now."
    /// </summary>
    IReadOnlyList<IChangeEvent> Apply(Frame<TPayload> frame, IWorldClock clock);
}
```

### Composer interface

```csharp
/// <summary>
/// One of three state-machine kinds (principle 10). Composers consume change events
/// (and/or domain frames from upstream composers) and emit domain frames when their
/// multi-frame pattern is satisfied. They *recognize* multi-frame patterns in events
/// PG already emits; they do not anticipate or synthesize PG behavior.
///
/// Composers chain via subscribe within a frame's resolution — they never re-emit
/// into the world's merger. Future-time emission is the producer interface's job.
/// </summary>
public interface IComposer
{
    /// <summary>
    /// Declared input event types (change events from folders, or domain frames from
    /// upstream composers). The world's resolution loop uses these to topologically
    /// order composer dispatch within a frame.
    /// </summary>
    IReadOnlyCollection<Type> Subscribes { get; }

    /// <summary>
    /// Observe one event from a declared input type. May update internal pending
    /// state; may emit zero or more domain frames if the composer's pattern is
    /// satisfied. Emitted frames carry timestamps from the event(s) they correlated,
    /// not from <paramref name="clock"/>.<see cref="IWorldClock.Now"/>.
    /// </summary>
    IReadOnlyList<Frame<object>> Observe(object eventPayload, IWorldClock clock);
}
```

### World interface

```csharp
/// <summary>
/// Shared contract for both worlds (PlayerWorld, ChatWorld). Each owns its own
/// producers, folders, composers, clock, frame merger, and output bus.
/// </summary>
public interface IWorld
{
    IWorldClock Clock { get; }

    /// <summary>
    /// Output bus for this world's domain frames. View-layer composers subscribe
    /// here; consumers outside the world never see change events directly — only
    /// the domain frames the world's composers chose to emit.
    /// </summary>
    IWorldEventBus Bus { get; }

    /// <summary>
    /// Register a producer (log tail, wake-at-T scheduler, filesystem reconcile, …).
    /// Must be called before <see cref="StartAsync"/>.
    /// </summary>
    void RegisterProducer<T>(IFrameProducer<T> producer);

    /// <summary>
    /// Register a folder for a frame payload type. The world routes frames of that
    /// type to this folder. Exactly one folder per payload type (registering a
    /// second throws).
    /// </summary>
    void RegisterFolder<T>(IFolder<T> folder);

    /// <summary>
    /// Register a composer. The world dispatches composer.Observe(…) for each input
    /// type the composer declares, in topologically-ordered fashion within each
    /// frame's resolution.
    /// </summary>
    void RegisterComposer(IComposer composer);

    /// <summary>
    /// Begin frame application. Closes the registration set; from here forward
    /// the world drains producers in timestamp order, dispatches to folders,
    /// resolves composers, publishes domain frames to <see cref="Bus"/>.
    /// </summary>
    Task StartAsync(CancellationToken ct);
}

/// <summary>
/// Typed pub-sub for a world's domain frames. Subscribers see emissions in
/// resolution order (deterministic over the source stream).
/// </summary>
public interface IWorldEventBus
{
    IDisposable Subscribe<T>(Action<Frame<T>> handler);
}
```

### Concrete worlds

```csharp
/// <summary>
/// World for Player.log. Consumes the unified classified pipe plus synthetic-
/// frame producers (filesystem reconcile, wake-at-T schedulers, etc.). Owns the
/// large set of Player.log-derived state services.
/// </summary>
public interface IPlayerWorld : IWorld
{
    IPlayerSkillStateService Skills { get; }
    IPlayerRecipeStateService Recipes { get; }
    IPlayerInventoryService Inventory { get; }       // Player.log half of split
    IPlayerEffectsStateService Effects { get; }
    IPlayerPositionTracker Position { get; }
    IPlayerPinTracker Pins { get; }
    IPlayerWeatherTracker Weather { get; }
    IPlayerAreaTracker Areas { get; }
    IPlayerCelestialStateService Celestial { get; }
    IPlayerSessionService Session { get; }
    IPlayerQuestJournalService QuestJournal { get; } // post-QuestService-split
    IPlayerWordOfPowerDiscoveryState WordOfPowerDiscovery { get; } // Saruman split
    // …
}

/// <summary>
/// World for chat. Genuinely small: two folders (inventory + WoP), each with its
/// own change-event surface; no intra-world composers in v1; session-replay from the
/// PG-session-start chat banner (principle 9). Cross-world composers (views) consume
/// this world's bus alongside PlayerWorld's bus.
/// </summary>
public interface IChatWorld : IWorld
{
    IChatInventoryStateMachine Inventory { get; }
    IChatWordOfPowerStateMachine WordsOfPower { get; }
    IChatSessionService Session { get; }
}
```

### View interfaces

```csharp
/// <summary>
/// Canonical inventory surface for modules. Composes Player.log's instance-id ledger
/// with chat's name-keyed stack-size observations. Scope-checks (Server, Character)
/// match between both sides.
/// </summary>
public interface IInventoryView
{
    /// <summary>
    /// Resolves an instance id to its InternalName via the PlayerWorld's ledger.
    /// </summary>
    bool TryResolve(long instanceId, out string internalName);

    /// <summary>
    /// Stack size for an instance id. Resolves the InternalName from the Player.log
    /// world's ledger, then looks up the most recent matching stack-size observation
    /// from the ChatWorld within a paired-window. Returns 1 if the item is non-
    /// stackable; null if stackable + no chat observation paired.
    /// </summary>
    bool TryGetStackSize(long instanceId, out int stackSize);

    IDisposable Subscribe(Action<InventoryEvent> handler);
}

/// <summary>
/// Canonical Words-of-Power surface. Composes Player.log discovery state with
/// chat spent state, keyed by code. No temporal pairing (discovery and spent
/// may be hours/days apart; the join is by code, not time-window).
/// </summary>
public interface IWordOfPowerView
{
    IReadOnlyCollection<KnownWord> Codebook { get; }
    bool TryGet(string code, out KnownWord word);
    event EventHandler? CodebookChanged;
}
```

---

## Worked example 1 — Inventory composition

**Today (one service spans both sources):**

```
Player.log: ProcessAddItem(instanceId, internalName) ─┐
                                                       ├→ IInventoryService.PendingCorrelator (Tier 1)
chat:       [Status] X xN added to inventory.        ─┘   → instance-id ledger with quantities
                                                          → exposes TryGetStackSize(instanceId)
```

**Target (split + view layer):**

```
Player.log: ProcessAddItem(instanceId, internalName) → PlayerWorld
                                                       ├ IPlayerInventoryService (instance-id ledger,
                                                       │  internalName only, no quantities)
                                                       │  Publishes: PlayerInventoryAdded events

chat:       [Status] X xN added to inventory.       → ChatWorld
                                                       ├ IChatInventoryStateMachine (name-keyed
                                                       │  time-series of stack-size observations)
                                                       │  Publishes: ChatStackObserved events

InventoryView (composition layer):
   subscribes to both worlds' events
   maintains stateful PendingCorrelator: pairs PlayerInventoryAdded with the
     most recent matching ChatStackObserved within 5s by (InternalName, Server, Character)
   exposes IInventoryView.TryGetStackSize(instanceId), TryResolve(instanceId), etc.

Modules (Samwise, Arwen, …) consume IInventoryView.
```

The `PendingCorrelator` primitive itself doesn't change — it relocates from `IInventoryService` to `InventoryView`. The cross-source TTL gate (5 simulated seconds) now reads from the view's `IViewClock` (derived from the max of the most-recently-observed Player/Chat bus frame timestamps, per Q5) instead of `_time.GetUtcNow()`, so the gate is replay-deterministic.

---

## Worked example 2 — Words of Power composition

**Today (one service mutated from two ingestions):**

```
Player.log: ProcessBook("You discovered a word of power!", …) ─┐
                                                                ├→ SarumanCodebookService
chat:       [Channel] WORDOFPOWER …                          ─┘    one codebook, Known/Spent on same record
```

**Target (split + view layer):**

```
Player.log: ProcessBook("…discovered…")              → PlayerWorld
                                                       ├ IPlayerWordOfPowerDiscoveryState
                                                       │  (code → discovery record: count, effect, description)

chat:       [Channel] WORDOFPOWER …                  → ChatWorld
                                                       ├ IChatWordOfPowerStateMachine
                                                       │  (code → spent marking + timestamp)

WordOfPowerView (composition layer):
   subscribes to both worlds' events
   merges by code: discovery record from PlayerWorld ⊕ spent marking from ChatWorld
   no temporal TTL — discovery and spent may be hours/days apart, key is the code
   exposes IWordOfPowerView.Codebook

Saruman consumes IWordOfPowerView.
```

Unlike inventory, WoP composition has no TTL — the join is purely by code. The view layer's "correlator" is a plain dictionary merge, not a `PendingCorrelator`.

---

## Determinism properties

Each layer's determinism is derived from the layer beneath:

1. **Source streams are deterministic input.** Player.log + chat corpora are the load. Replay = identical load = identical trajectory.
2. **Each world is deterministic over its source.** Frame merger is timestamp-ordered with explicit tie-breaking (`Sequence` for native frames, declared priority for producer-emitted frames). Folders fire in dispatch order per frame; composers fire in topologically-sorted order over their declared input event types within the frame's resolution. World clock derives from frame timestamps; no real-time leaks into state decisions.
3. **Each view is deterministic over the worlds' bus emissions.** Views subscribe to worlds' deterministic domain-frame streams, fold them with deterministic logic, expose the composed result. The view's own clock (`IViewClock`) derives from observed frame timestamps — typically the max of the most-recently-observed timestamps across both buses (see Q5 resolution).
4. **Modules consume the view layer.** Module behavior is therefore deterministic over (source streams + module-local state like settings / user input).

The full stack: replayable Player.log + chat → identical world trajectories → identical view trajectories → identical module-visible state. The only non-determinism is user input + reference data updates (CDN refresh), both explicitly out of the worlds' input set.

---

## Scope and the per-server / per-character partition

Both worlds partition along the scope hierarchy:

```
Server (world instance — parallel realities across PG's servers)
  └─ World-scope state (weather, celestial, area existence) — partitioned per-server
       └─ Character-scope state (skills, inventory, quests, …) — transitively per-server via its character
```

In each world:
- Frames carry their `(Server, Character)` context, derived from the world's own session ledger (Player.log's via `EVENT(Ok): connected` + `LoginBanner`; chat's via its banner).
- World-scope handlers consume all frames; route to the right per-server bucket.
- Character-scope handlers consume only their character's frames; per-character buckets within per-server scope.

The view layer scope-checks: an inventory view joining a Player.log `ProcessAddItem` with a chat `[Status] added` asserts both sides are in the same `(Server, Character)` — if they're not, the data isn't actually correlated and the view drops the pair with a diagnostic.

---

## Three categories of data the architecture admits

A consumer (module, view, or another component) needs data. There are exactly three places that data can come from:

1. **World-derived state** — observed events from Player.log or chat, transformed through the folder/composer pipeline of `PlayerWorld` or `ChatWorld`. Live, continuous, event-driven, replay-deterministic. Consumed by subscribing to world buses (or to views above them).

2. **External shared data sources** — data the world doesn't produce, that multiple consumers might want. Discrete records, externally sourced (filesystem / CDN / startup log parsing), point-in-time or version-stamped:
    - `Mithril.Reference` — CDN-fetched PG reference data (items, recipes, skills, NPCs, …)
    - `Mithril.GameReports` — PG-exported character snapshot reports (storage / vault / skill / recipe / quest snapshots; PG's `/exportchar` output). Per-character, per-export-time. **Different semantic from `Mithril.GameState`**: GameState describes the world *as the player observes it through events*; GameReports describes *PG's authoritative snapshot at the moment of export*. They can briefly disagree, and that's fine — the world is canonical for live; the report is canonical for "what PG thinks now." Reports also contain data the world *can't see* — most notably vault contents.
    - `ICommunityCalibrationService` — CDN-fetched community calibration data (Arwen gift rates, Legolas projector tweaks).
    - `IServerCatalogService` (planned) — Player.log-derived but reference-shaped (parse once at attach, immutable for the session).

3. **Module-owned adjacent state** — user-driven, module-internal, persisted alongside the module:
    - Gandalf timer definitions, alarm config
    - Saruman codebook overrides (manual mark-as-spent/known)
    - Samwise alarm snoozes, dismissals
    - Per-module settings (`ArwenSettings`, `LegolasSettings`, etc.)
    - One-off documents (Celebrimbor leveling plans)

### Composition

**Views can compose across all three categories.** A "complete character access" view (live bag from worlds + vault from GameReports + reference data for item names) reads from PlayerWorld bus, ChatWorld bus, `Mithril.GameReports`, AND `Mithril.Reference`. The view is the universal composition point. Principle 4 ("cross-source composition lives in views above the worlds") generalizes — views compose across whatever sources need composing, not just the two worlds.

### Boundaries

- **Module state is owned by its module.** Cross-module reads go through the module's service interface, not directly into its JSON store.
- **External shared data sources live in foundation-layer assemblies** (`Mithril.Reference`, `Mithril.GameReports`, etc.). Multiple modules consume the same service; no module "owns" the file.
- **`per-session` is the scope tier** for both world-character-state and module-owned state. Session = `(Server, Character)` derived from `IGameSessionService`. The legacy `PerCharacterView<T>` evolves to key on session, not character alone.
- **Module state mutations are not gated by world `Mode`.** Mode gates *side effects derived from state* (audio, notifications), not the state mutations themselves. User-initiated changes apply immediately whether the world is `Replaying` or `Live`.

### Vault items — the canonical case that requires GameReports

Player.log + chat don't see vault contents (the player isn't observing them in-bag). Only the character export report includes them. So any "what items does this character have access to" view *must* compose worlds + reports — neither alone is sufficient. This is the structural reason `Mithril.GameReports` exists as a separate assembly rather than being absorbed into the world layer: it carries data the worlds inherently cannot.

---

## Migration path (concrete to-do)

After this design lands, the following changes are needed:

### Splits (services that span both sources)

1. **`IInventoryService` → `IPlayerInventoryService` + `IChatInventoryStateMachine`.** Player.log half: instance-id-keyed ledger, no quantities. Chat half: name-keyed stack-size observations. View: `IInventoryView` with the existing `TryGetStackSize` / `TryResolve` API surface (mechanical migration for consumers).
2. **`SarumanCodebookService` → `IPlayerWordOfPowerDiscoveryState` + `IChatWordOfPowerStateMachine`.** Player.log half: discovery records. Chat half: spent markings. View: `IWordOfPowerView` exposing the merged codebook.

### Migrations (cross-source services that become single-source)

3. **`MotherlodeMeasurementCoordinator`.** Chat distance retires; reads `LocalPlayer: ProcessScreenText(ImportantInfo, "The treasure is N meters from here.")` from Player.log. Becomes a single-source PlayerWorld state machine. ([#511 deliverable 6 + #531 comment](https://github.com/moumantai-gg/mithril/issues/531#issuecomment-4499029851).)
4. **`AreaCalibrationService` chat side.** `Entering Area:` already redundant per #531; drop the chat subscription; rely on `PlayerAreaTracker.Changed`.
5. **All of Legolas's remaining chat consumption.** Per [#531 comment](https://github.com/moumantai-gg/mithril/issues/531#issuecomment-4499029851), every chat verb has a Player.log equivalent. `LogIngestionService`, `ChatLogParser`, and the `IChatLogStream` ctor argument all delete entirely. `SurveyDetected` migrates to `ProcessMapFx` trailing arg (or drops entirely per #454).

### Eliminations (current-architecture workarounds that collapse)

6. **`QuestService.OnViewCurrentChanged` synthesis.** Character-switch reload with synthesized `Abandoned`/`Accepted`/`Completed` events. Collapses under per-character world scope: character B's ledger was always character B's, so binding the UI to it fires no events on character A. Also splits the reference half (`IReferenceDataService.Quests`) from the state half (`IPlayerQuestJournalService`).
7. **Arwen's `_inventory.TryResolve` cross-FSM peek.** Reads from the post-split `IPlayerInventoryService` half — coherent within the PlayerWorld's dispatch order, no race.
8. **Wall-clock `_time.GetUtcNow()` state-decision uses.** Every one of them migrates to `IWorldClock.Now` of whichever world/view it lives in. Samwise `PruneWithered`, `AlarmService.IsLikelyGarbageCollected`, Gandalf `TimerProgressService.CheckExpirations`, etc.

### Additions (new shared services + parsers)

9. **`ServerCatalogParser`** for the `Servers: [ … ]` startup line → `IServerCatalogService` (reference-scope, exposed as a `Mithril.Reference` entry).
10. **`ConnectionEventParser`** for `EVENT(Ok): connected, url=…` → augments `IGameSessionService` with the `Server` field.
11. **Extract character report loader → `Mithril.GameReports`** (new assembly). Per-character snapshot files (`Reports/items_X.json`, plus skills / recipes / quests / vault). `FileSystemWatcher` lives here. Bilbo's storage view migrates to subscribe to this service. Elrond's character snapshot input migrates. The previously-flagged "FileSystemWatcher reconcile retires under chat replay" framing was wrong — chat replay covers pre-attach inventory adds, but vault contents and snapshot-only data require GameReports; the two concerns separate cleanly.
12. **Gandalf scheduler collapse.** `TimerExpirationScheduler` / `ShiftAlarmService` / `TimerProgressService.CheckExpirations` retire under principle 13. Gandalf subscribes to PlayerWorld's `CalendarTimeAdvanced` + `TimeOfDayShift` domain events; compares against module-side timer ledger; fires alarms gated on `Mode == Live`. Module-side timer definitions (in `Mithril.GameReports`-style? no — module-owned per category 3) stay; the wakeup machinery doesn't.

After all migrations land:
- No service spans both sources.
- No direct `IChatLogStream` consumer outside `ChatWorld`.
- No `_time.GetUtcNow()` in state-decision paths.
- No cross-FSM peeks rely on incidental scheduler ordering.
- [`cross-source-correlation.md`](cross-source-correlation.md) loses both in-repo reference implementations; pattern catalog remains for future cross-source consumers.

---

## Open questions

1. ~~**View-layer subscription contract.**~~ **Resolved: per-world `IWorldEventBus` carrying typed domain frames.** Each world has one bus (`worldSim.Bus.Subscribe<TDomain>(...)`). Folders and composers are world-internal; only domain frames cross the world boundary. Views subscribe to one or more world buses, run their own composer-shaped logic, expose their own bus surface to modules. The per-component `Subscribe(Action<TEvent>)` pattern in today's services becomes `worldSim.Bus.Subscribe<TEvent>(...)` post-migration — same ergonomics, single owner.

2. ~~**Pass-through views for single-world consumers.**~~ **Resolved: always-through-views for cross-world composition.** For single-world consumers (Samwise, Arwen, Saruman discovery-only), subscribing directly to a world's bus is fine — no view layer required for "I only need PlayerWorld events." The view layer exists specifically for cross-world composition (`InventoryView`, `WordOfPowerView`) and stays consistent shape-wise even when only one module consumes a given view today.

3. **Snapshot / rewind.** Once both worlds are clocked with frame indices, snapshot at frame N = (folder states + composer pending state + clock state) is well-defined. Rewind / branch is a natural follow-on. Out of scope for v1; the contracts shouldn't preclude it.

4. ~~**Live-mode clock interpolation.**~~ **Resolved (the original framing was wrong): no interpolation.** The world clock is just "last applied frame's timestamp." Reading `worldClock.Now` during live-mode idle returns the same value as immediately after the last frame applied — no continuous-time read. Consumers that need to react to time progression subscribe to `CalendarTimeAdvanced` domain events (principle 13). PG's logs are continuously noisy during active play (movement, asset loading, combat ticks, NPC chatter), so the world clock advances at most a few seconds behind real time during normal gameplay; module-side schedulers comparing event timestamps against thresholds fire near-real-time without anyone needing interpolation. Real wall-clock is used only inside the world's merger to know how long to block on the live source-stream tail.

5. ~~**Two clocks or one?**~~ **Resolved: each world owns its `IWorldClock`; views derive their own clock from observed domain-frame timestamps.** Each world's clock advances by its own source's frame timestamps. Views are composers above worlds; they observe domain frames flowing from world buses, each frame carrying its own timestamp (inherited from the originating change/source frame). View-layer TTL gates (like `InventoryView`'s 5s pairing window) use the *frame timestamps themselves* for correlation; for eviction-of-stale-pending-state, views derive a "now" from the max of the most-recently-observed frame timestamps across both world buses. Concrete contract: `IViewClock` exposes `Now : DateTimeOffset` (derived) and `Frames` as a tuple `(playerFrame, chatFrame)` of the most-recently-observed frame indices from each world's bus.

6. ~~**User actions.**~~ **Resolved: three categories of data — world-derived state, external shared data sources, module-owned adjacent state.** See the "Three categories of data" section above. Module state is owned by its module; cross-module reads via the module's service interface. Per-session scope tier (`Server`, `Character`) for both world-character-state and module-owned state. `Mithril.GameReports` extracted as a foundation-layer assembly for PG character export snapshots (consumed by Bilbo + Elrond + future). Module state mutations are not gated by `Mode`; only side effects gate. User actions remain explicitly non-deterministic — recording them as a parallel input stream for full session replay is a future capability, not in scope for v1.

7. **What replaces `Mithril.Roadmap Project` as the prioritisation surface?** Per earlier conversation the Project board went stale weeks ago. The migration to-do list above needs a home — likely individual issues with the relevant `module:*` labels, but the umbrella story (this design notebook) needs an issue too. Pending the broader "where things live" revisit.

---

## What this doc does NOT cover

- Complete state-machine semantics for each folder/composer. The world runtime is mostly today's GameState services with the `BackgroundService`-per-service shell replaced by folder/composer registration (per principle 10); per-service transition tables are in their source files.
- Detailed view-layer implementations. The contracts and worked examples here are starting points; concrete views write themselves once the world layer is in place.
- Live-vs-replay mode switching. The architecture supports both naturally (replay = source stream is a finite recorded log; live = source stream is a live tail). The world doesn't need to know which it's in; producers do.
- Performance characteristics under high-frame-rate scenarios (combat ticks, asset-loading bursts). The dispatch graph fans out per-frame; load behavior is empirical and won't be known until the migration is far enough along to measure.
