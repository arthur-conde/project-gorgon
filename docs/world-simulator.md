# World simulator — design notebook

Design rationale for the three-layer world-simulator architecture: source streams → worlds → composition views → modules. This is the converged shape from the design conversation that landed [`module-signal-map.md`](module-signal-map.md); read that doc first for the current topology, then this one for where it's going.

> **Vocabulary:** see [`docs/glossary.md`](glossary.md) for definitions of the world-sim terminology used in this doc.

**Status:** design notebook, not implementation spec. Captures the architectural commitments, contracts, and migration plan. Concrete contracts may iterate as implementation surfaces issues; principles are load-bearing.

**Companion docs:**
- [`module-signal-map.md`](module-signal-map.md) — the topology this design feeds against (read first).
- [`world-sim-migration-audit.md`](world-sim-migration-audit.md) — line-by-line audit of every state-holder against the principles + migration plan in this notebook. 15 components classified; 5 need behavioural changes; 3 sleeper blockers identified. Read before starting any migration item.

## Vocabulary

- **PlayerWorld** — the world reconstructed from `Player.log`. NOT "the player character" — that's a separate entity (`Character`) tracked within either world. When this doc says "Player" it always means "derived from Player.log."
- **ChatWorld** — the world reconstructed from the chat log files.
- **Folder / composer / producer** — the three state-machine kinds (principle 10). Folders apply frames; composers correlate change events; producers source external-input frames (log tails).
- **Frame** — `(timestamp, payload)`. The unifying primitive.
- **Change event** — what a folder emits when applying a frame. Flows to intra-world composers during a frame's resolution AND is published on the world's typed bus as a first-class single-world surface (see "Decisions ratified post-#642").
- **Domain frame** — what a composer emits when its multi-frame pattern is satisfied. The cross-world consumption contract — views join PlayerWorld and ChatWorld on domain frames.
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
2. **Two worlds with sealed output boundaries; views consume across them.** `PlayerWorld` is deterministic over `IClassifiedPlayerLogStream`. `ChatWorld` is deterministic over the chat stream. Each world has its own internal pipeline (frames → folders → change events → composers → domain frames) and its own typed output bus carrying both change events (single-world surface) and domain frames (cross-world surface). Worlds don't query each other and don't send messages to each other — they're sealed at the bus. Cross-world consumers (views) subscribe to both world buses (typically joining on domain frames); nothing flows back into a world from above.
3. **If a service currently consumes both chat and Player.log, it must be split.** No cross-source services. Each service lives in exactly one world.
4. **Cross-source composition lives in a view layer above the worlds.** Views are composers operating one layer up — they subscribe to one or more world buses, maintain composed state, expose their own bus surface (or read-only API) to modules. Cross-world consumers (modules needing data from both Player.log and chat) always go through a view. Single-world consumers (modules needing only one source's state) may subscribe directly to that world's bus — no pass-through view required, since the view layer's purpose is *composition across worlds*, not API uniformity.
5. **Tri-property clock: simulated wall-clock + frame index + mode.** `Now : DateTimeOffset` advances by frame timestamps (the timestamp of the most recently applied frame); `Frame : long` strictly monotonic per applied frame; `Mode ∈ {Replaying, Live}` tracks whether the world is draining backlog or caught up (full detail in principle 12). `Now` answers "how much simulated time has passed?" (1-second resolution because PG's timestamps are); `Frame` answers "are we at the same point in the trajectory?"; `Mode` answers "should side-effecting consumers fire now?" The triple identifies a unique moment in a unique mode.
6. **Scope: reference / world (per-server) / character (per-server-per-character).** Per [`module-signal-map.md`](module-signal-map.md) — PG has multiple servers; world state is per-server; character state is per-character within a server. Worlds partition along these scopes.
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
(replay from        │ ↑          (change events also published   │   │
 session-start)     │ │           on bus as single-world surface) │   │
                    │ └──── Producers (Player.log tail; future:        InventoryView    Samwise
                    │       fs reconcile via GameReports) ────┘ │   │   (cross-world      Arwen
                    └───────────────────────────────────────────┘   │   composer,        …
                                                                    ├──→ subscribes to
                    ┌─────── ChatWorld ─────────────────────────┐   │   both world buses,
chat stream       → │ Merger ──→ Folders ──→ Composers ──→ Bus  │ ──┤   joins on domain
(replay from        │ (chat-inventory mirror,                    │   │   frames)
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
- Publishes both change events and domain frames to its own typed output bus. Change events are the single-world surface (modules / views subscribed to one world subscribe to concrete change-event types directly); domain frames are the cross-world surface (views composing across worlds join here). The bus is sealed: nothing flows back into the world from above. See "Decisions ratified post-#642".
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
/// Non-generic frame base. Lets a composer or producer return heterogeneous
/// frames (different payload types) from a single call without resorting to
/// <c>Frame&lt;object&gt;</c>-with-boxing. Concrete <see cref="Frame{T}"/>
/// implements this; consumers downcast / pattern-match on the concrete type
/// (typically inside <see cref="IWorldEventBus"/>) to route to typed
/// subscribers.
/// </summary>
public interface IFrame
{
    DateTimeOffset Timestamp { get; }
    object Payload { get; }
    Type PayloadType { get; }
}

/// <summary>
/// One unit of simulated input. Producers stamp every frame with the event time
/// the frame represents (NOT the wall-clock when the producer fired).
/// </summary>
public readonly record struct Frame<TPayload>(
    DateTimeOffset Timestamp,
    TPayload Payload) : IFrame
{
    object IFrame.Payload => Payload!;
    Type IFrame.PayloadType => typeof(TPayload);
}
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
/// A source of external-input frames feeding a world (principle 10). Implementers
/// are sources of real-world inputs only: the L1 classified-pipe reader (Player.log
/// frames), the chat tail (chat frames). Future possibilities: filesystem reconcile
/// emitting frames stamped with export payload timestamps. Producers are NOT a
/// mechanism for user-driven scheduling — user-side wake-at-T concerns consume
/// world domain events and run module-internal logic against them; they do not
/// register producers in a world's merger.
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
    ///
    /// <para>Return type is <see cref="IFrame"/> (non-generic) rather than
    /// <c>Frame&lt;object&gt;</c> so a composer can emit heterogeneous domain
    /// frame types in a single call without boxing the typed payload at the
    /// composer boundary. A composer that emits a <c>GiftObservation</c>
    /// returns <c>new Frame&lt;GiftObservation&gt;(eventTs, observation)</c>
    /// upcast to <see cref="IFrame"/>; the bus's typed
    /// <see cref="IWorldEventBus.Subscribe{T}"/> pattern-matches on the concrete
    /// <see cref="Frame{T}"/> to route to subscribers of that payload type.</para>
    /// </summary>
    IReadOnlyList<IFrame> Observe(object eventPayload, IWorldClock clock);
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
    /// Output bus for this world's typed event surface. Carries both domain frames
    /// (the cross-world consumption contract — view-layer composers join PlayerWorld
    /// and ChatWorld here) AND change events (first-class output for single-world
    /// consumers; subscribe via <c>Subscribe&lt;TConcreteChange&gt;(...)</c>).
    /// Composers exist to recognize multi-frame patterns in change events and emit
    /// semantically-new domain frames — not to re-label a folder's output into a
    /// same-shape domain frame. See "Decisions ratified post-#642" for the rationale
    /// (the original framing conflated resolution-graph topology with bus
    /// consumability).
    /// </summary>
    IWorldEventBus Bus { get; }

    /// <summary>
    /// Register a producer (log tail, wake-at-T scheduler, filesystem reconcile, …).
    /// Must be called before <see cref="StartMerger"/>.
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
    /// Close the registration set and begin merger drain. Returns a long-running
    /// task that completes when every producer is drained or <paramref name="ct"/>
    /// is cancelled. Two-phase contract: registration acceptance (closes on entry)
    /// and merger running (the returned task). Called by the trailing
    /// <c>WorldMergerStartHostedService</c> registered LAST in the composition
    /// (#696 Call 2) so the merger drain starts only after every other hosted
    /// service's <c>StartAsync</c> has completed.
    /// </summary>
    Task StartMerger(CancellationToken ct);
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
    IPlayerQuestJournalState QuestJournal { get; } // post-QuestService-split
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

## Naming conventions

The conventions below cover folder interfaces and change-event types under the world-sim architecture. They apply to types born under or after Phase 2 (#602, #603) and to existing types only when an in-flight migration PR is already touching them for behavioural reasons — there is no dedicated rename sweep.

### Folder interface suffix

Folder interface names take the form `I<World><Domain>State`. The world prefix (`Player` or `Chat`) is mandatory, the domain follows, `State` is the suffix. The historical suffix variants (`StateService` on Skills/Recipes/Effects/Celestial, `Service` on Inventory/Position, `StateMachine` on the chat folders, `Tracker` on Area/Position/Pin/Weather, `Journal` on QuestJournal) are dropped.

The architectural primitive is "folder" (principle 10); the interface name should signal that uniformly across all folders, regardless of which historical service shape the type was carved out of.

```
IPlayerSkillState
IPlayerRecipeState
IPlayerInventoryState
IPlayerEffectsState
IPlayerPositionState
IPlayerPinState
IPlayerWeatherState
IPlayerAreaState
IPlayerCelestialState
IPlayerQuestJournalState
IPlayerWordOfPowerDiscoveryState
IChatInventoryState
IChatWordOfPowerState
```

### Change-event type suffix

Change-event types are named in past-tense participle form, with no `Event` suffix. The delta-noun form (`SkillChange`, `RecipeChange`) and the `Event`-suffixed form (`EffectEvent`, `QuestEvent`, `InventoryEvent`) are both dropped.

Bus consumers subscribe via `Frame<TConcreteChange>` — the type parameter already telegraphs "event"; the `Event` suffix is redundant. The participle reads naturally at the call site (`bus.Subscribe<InventoryItemAdded>(…)`).

```
InventoryItemAdded
InventoryStackChanged
SkillProgressed
RecipeLearned
WordOfPowerDiscovered
WordOfPowerSpent
PinSetChanged
```

### World prefix on event names

Mandatory world prefix on folder-emitted events; never on view-emitted events. The first word of an event name tells the reader who emitted it: `Player…` means a PlayerWorld folder, `Chat…` means a ChatWorld folder, anything else means the view layer (cross-world composition).

The prior "prefix only when a sibling exists in the other world" rule failed the at-a-glance test — readers couldn't tell whether `InventoryAdded` referred to the unified view event or to a prefix-elided folder event. The simpler invariant (world events always prefixed; view events never) is the legible one. The 4–6-character cost on Player-only events is worth the legibility win for cold readers.

```
Folder-emitted (world bus):
  PlayerInventoryAdded
  PlayerSkillProgressed
  PlayerWordOfPowerDiscovered
  ChatInventoryObserved
  ChatWordOfPowerSpent

View-emitted (view bus):
  InventoryItemAdded
  InventoryStackChanged
  WordOfPowerKnowledgeChanged
```

### Migration policy

The sweep is opportunistic, not big-bang. New folders born under Phase 2 (#602 Inventory split, #603 Saruman codebook split) and later phases ship under the convention; existing folders rename only when a migration PR is already touching them for behavioural reasons (e.g., #618 already touches `IPlayerSkillStateService`; #607 touches `IPlayerQuestJournalService`; #602 retires `IInventoryService`). Apply the rename inside those PRs.

Existing folders not being migrated keep their historical names until they are. No dedicated rename-sweep PR is in scope.

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

The view's published surface is three events: `InventoryItemAdded` (composed — pairs `PlayerInventoryAdded` with a `ChatStackObserved` within the TTL window to carry stack size), `InventoryStackChanged` (composed — fires when a chat observation arrives after the Add already fired), and `InventoryItemRemoved` (a passthrough of `PlayerInventoryRemoved`; chat has no removal signal). The Removed channel is the passthrough case admitted by the refined #643 principle (see [Decisions ratified post-#642 §#643](#643--change-events-flow-on-the-worlds-typed-bus)): dropping it would force consumers to subscribe to the view for Added/StackChanged but to PlayerWorld's bus for Removed for the same logical concept, a leaky abstraction. The view's job is to be the canonical inventory event stream, so coherence of that surface is the value-add — not per-event-type novelty.

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
5. **All of Legolas's remaining chat consumption.** *Done — [#606](https://github.com/moumantai-gg/mithril/issues/606) (Phase 3 of [#601](https://github.com/moumantai-gg/mithril/issues/601)).* Per [#531 comment](https://github.com/moumantai-gg/mithril/issues/531#issuecomment-4499029851), every chat verb has a Player.log equivalent: `ItemAddedToInventory` → `IInventoryView.Bus.Subscribe<InventoryItemAdded>` (post-#602 typed-frame surface); `ItemCollected` → new `PlayerLogParser.ItemCollectedRx` parsing `ProcessScreenText(ImportantInfo, "<Mineral> collected!")`; `SurveyDetected` → `ProcessMapFx` trailing-arg relative offset, parsed inline by `PlayerLogParser.TryParseMapFxRelativeOffset` and fed to `IAreaCalibrationService.NoteSurvey` from `PlayerLogIngestionService.HandleMapTarget`; `MotherlodeDistance` already same-source post-#604; `AreaEntered` already redundant per #605. `LogIngestionService`, `ChatLogParser`, `IChatLogParser`, and the Legolas `IChatLogStream` ctor argument all deleted; the Tier-1 Add↔Collect correlator now lives in the new `Legolas.Services.ItemCollectionTracker`, intra-module rather than cross-source.

### Eliminations (current-architecture workarounds that collapse)

6. **`QuestService.OnViewCurrentChanged` synthesis.** Character-switch reload with synthesized `Abandoned`/`Accepted`/`Completed` events. Collapses under per-character world scope: character B's ledger was always character B's, so binding the UI to it fires no events on character A. Also splits the reference half (`IReferenceDataService.Quests`) from the state half (`IPlayerQuestJournalState`). Persistence retires entirely under #718; cross-session completion anchors move to module-side ledgers (Gandalf's `DerivedTimerProgressService`).
7. **Arwen's `_inventory.TryResolve` cross-FSM peek.** Reads from the post-split `IPlayerInventoryService` half — coherent within the PlayerWorld's dispatch order, no race.
8. **Wall-clock `_time.GetUtcNow()` state-decision uses.** Every one of them migrates to `IWorldClock.Now` of whichever world/view it lives in. Samwise `PruneWithered`, `AlarmService.IsLikelyGarbageCollected`, Gandalf `TimerProgressService.CheckExpirations`, etc.

### Additions (new shared services + parsers)

9. **`ServerCatalogParser`** for the `Servers: [ … ]` startup line → `IServerCatalogService` (reference-scope, exposed as a `Mithril.Reference` entry).
10. **`ConnectionEventParser`** for `EVENT(Ok): connected, url=…` → augments `IGameSessionService` with the `Server` field.
11. **Extract character report loader → `Mithril.GameReports`** (new assembly). Per-character snapshot files (`Reports/items_X.json`, plus skills / recipes / quests / vault). `FileSystemWatcher` lives here. Bilbo's storage view migrates to subscribe to this service. Elrond's character snapshot input migrates. The previously-flagged "FileSystemWatcher reconcile retires under chat replay" framing was wrong — chat replay covers pre-attach inventory adds, but vault contents and snapshot-only data require GameReports; the two concerns separate cleanly.
12. **Gandalf scheduler collapse.** `TimerExpirationScheduler` / `ShiftAlarmService` / `TimerProgressService.CheckExpirations` retire under principle 13. Gandalf subscribes to PlayerWorld's `CalendarTimeAdvanced` + `TimeOfDayShift` domain events; compares against module-side timer ledger; fires alarms gated on `Mode == Live`. The module-side timer definitions are module-owned adjacent state (category 3); the wakeup machinery doesn't survive.
13. **`ClassifiedPlayerLogProducer` → `WorldClockTickProducer` reshape.** Today `ClassifiedPlayerLogProducer` emits `Frame<IClassifiedPlayerLogLine>` with no folder consumer, advancing the world clock as an invisible side effect of the merger applying the frame. Reshape into an explicit `WorldClockTickProducer` whose owned folder emits `CalendarTimeAdvanced` domain frames at the cadence of the source stream — i.e., the clock-tick owner is named and the `CalendarTimeAdvanced` emission has an explicit producer→folder→bus path. Without this, simply dropping `ClassifiedPlayerLogProducer` (the naive Option 1 of #644) would silently stagnate the clock during folder-irrelevant log stretches and cause Gandalf's planned scheduler-collapse alarms (item #12 above, principle 13) to fire late. **Follow-on to the #644 ratification**; the design choice is locked, the implementation lands separately.

After all migrations land:
- No service spans both sources.
- No direct `IChatLogStream` consumer outside `ChatWorld`.
- No `_time.GetUtcNow()` in state-decision paths.
- No cross-FSM peeks rely on incidental scheduler ordering.
- [`cross-source-correlation.md`](cross-source-correlation.md) loses both in-repo reference implementations; pattern catalog remains for future cross-source consumers.

---

## Open questions

1. ~~**View-layer subscription contract.**~~ **Resolved: per-world `IWorldEventBus` carrying a typed event surface (both change events and domain frames).** Each world has one bus (`worldSim.Bus.Subscribe<TEvent>(...)`). Folders and composers run inside the world's per-frame dispatch; the bus surfaces their output. Change events are first-class for single-world consumers; domain frames are the cross-world composition contract for views. Views subscribe to one or more world buses (typically joining on domain frames), run their own composer-shaped logic, expose their own bus surface to modules. The per-component `Subscribe(Action<TEvent>)` pattern in today's services becomes `worldSim.Bus.Subscribe<TEvent>(...)` post-migration — same ergonomics, single owner. **Note:** the original wording said "only domain frames cross the world boundary" — that was refined post-#642; see "Decisions ratified post-#642".

2. ~~**Pass-through views for single-world consumers.**~~ **Resolved: always-through-views for cross-world composition.** For single-world consumers (Samwise, Arwen, Saruman discovery-only), subscribing directly to a world's bus is fine — no view layer required for "I only need PlayerWorld events." The view layer exists specifically for cross-world composition (`InventoryView`, `WordOfPowerView`) and stays consistent shape-wise even when only one module consumes a given view today.

3. **Snapshot / rewind** — *deferred, not blocking.* Once both worlds are clocked with frame indices, snapshot at frame N = (folder states + composer pending state + clock state) is well-defined. Rewind / branch is a natural follow-on. Explicitly out of v1 scope; the contracts as written don't preclude it. Re-open if a real consumer asks.

4. ~~**Live-mode clock interpolation.**~~ **Resolved (the original framing was wrong): no interpolation.** The world clock is just "last applied frame's timestamp." Reading `worldClock.Now` during live-mode idle returns the same value as immediately after the last frame applied — no continuous-time read. Consumers that need to react to time progression subscribe to `CalendarTimeAdvanced` domain events (principle 13). PG's logs are continuously noisy during active play (movement, asset loading, combat ticks, NPC chatter), so the world clock advances at most a few seconds behind real time during normal gameplay; module-side schedulers comparing event timestamps against thresholds fire near-real-time without anyone needing interpolation. Real wall-clock is used only inside the world's merger to know how long to block on the live source-stream tail.

5. ~~**Two clocks or one?**~~ **Resolved: each world owns its `IWorldClock`; views derive their own clock from observed domain-frame timestamps.** Each world's clock advances by its own source's frame timestamps. Views are composers above worlds; they observe domain frames flowing from world buses, each frame carrying its own timestamp (inherited from the originating change/source frame). View-layer TTL gates (like `InventoryView`'s 5s pairing window) use the *frame timestamps themselves* for correlation; for eviction-of-stale-pending-state, views derive a "now" from the max of the most-recently-observed frame timestamps across both world buses. Concrete contract: `IViewClock` exposes `Now : DateTimeOffset` (derived) and `Frames` as a tuple `(playerFrame, chatFrame)` of the most-recently-observed frame indices from each world's bus.

6. ~~**User actions.**~~ **Resolved: three categories of data — world-derived state, external shared data sources, module-owned adjacent state.** See the "Three categories of data" section above. Module state is owned by its module; cross-module reads via the module's service interface. Per-session scope tier (`Server`, `Character`) for both world-character-state and module-owned state. `Mithril.GameReports` extracted as a foundation-layer assembly for PG character export snapshots (consumed by Bilbo + Elrond + future). Module state mutations are not gated by `Mode`; only side effects gate. User actions remain explicitly non-deterministic — recording them as a parallel input stream for full session replay is a future capability, not in scope for v1.

7. ~~**What replaces `Mithril.Roadmap Project` as the prioritisation surface?**~~ **Resolved 2026-05-21: new org-level Mithril Roadmap Project at <https://github.com/orgs/moumantai-gg/projects/1>.** The legacy user-level board (`https://github.com/users/arthur-conde/projects/3`) didn't migrate when the `moumantai-gg` org was created, so it went stale; the new org-level board replaces it. Lean field scheme: `Status`, `Priority`, `Module` only (the prior `Effort` + `Target Version` fields were dropped to reduce maintenance friction — re-add if a real need surfaces). Migration items from this design notebook land as individual GitHub Issues with the relevant `module:*` labels and get added to the Project; this PR (#600) is the umbrella, already on the board.

---

## Decisions ratified post-#642 (2026-05-22)

Two architectural questions surfaced from the shepherd review of PR #642 (Phase 1 of the world-sim migration umbrella, [#601](https://github.com/moumantai-gg/mithril/issues/601)). Both were decided after the original design landed and are recorded here for cold-session continuity. Doc-only ratification; no production code behaviour changed when these decisions were taken (PR #642's existing `PublishChangeEvent` calls are correct under the new framing).

### #643 — change events flow on the world's typed bus

**Decision:** change events are first-class output on each world's `IWorldEventBus`, alongside domain frames.

**Rationale:** the original "world-internal; never cross the world boundary" framing conflated two distinct properties — the *resolution-graph topology* (folders feed composers within a frame's dispatch resolution; this remains TRUE and is unchanged) and the *consumability of the typed channel on the bus* (this was wrong — change events ARE first-class bus output for single-world consumers). Domain frames remain the cross-world consumption contract: a view joining PlayerWorld and ChatWorld joins on domain frames, not change events. Composers exist for recognition of multi-frame patterns producing semantically-new domain frames (e.g., three same-source change events → one `GiftObservation`); a composer that re-labels a folder's change event into a same-shape domain frame is a pass-through and shouldn't exist — **unless** the emission is one channel of a unified semantic surface whose other channels carry real composition, in which case the surface's coherence justifies the passthrough. Compact form: passthrough is allowed when it's a member of a multi-event composed surface; not allowed when it's the entire surface. Worked example: `IInventoryView` publishes Added (composed: pairs `PlayerInventoryAdded` with a chat stack-size observation), StackChanged (composed: chat observation arriving after the Add), and Removed (passthrough of `PlayerInventoryRemoved`; chat has no removal signal). The view ships all three because the surface's coherence requires it; a hypothetical view that *only* re-emitted a single folder's events would be pure relabeling and is still forbidden.

See: [issue #643](https://github.com/moumantai-gg/mithril/issues/643).

### #644 — per-folder producers + explicit clock-tick owner

**Decision:** adopt the per-folder producer pattern (Option 1). Reshape `ClassifiedPlayerLogProducer` into an explicit `WorldClockTickProducer` whose owned folder emits `CalendarTimeAdvanced` domain frames at the cadence of the source stream.

**Rationale:** the current `IFolder<TPayload>` contract ("exactly one folder per payload type; registering a second throws") forbids Option 2 (shared-stream routing) without relaxing the contract to multi-folder fanout. Per-folder producers preserve the contract and give each folder an independently testable owned parser. The "N L1 subscriptions, N parsers per line" cost is bounded because parsers fast-fail on line discrimination — parse work scales as O(folders + relevant-lines), not O(folders × lines). The clock-tick owner must be made explicit because today `ClassifiedPlayerLogProducer` emits frames with no folder consumer, advancing the world clock as an invisible side effect; dropping it naively would silently stagnate the clock during folder-irrelevant log stretches and cause Gandalf's planned scheduler-collapse alarms (principle 13) to fire late. The reshape is captured as migration item #13 above. **Implementation of the reshape is a follow-on issue; this section ratifies the design choice only.**

See: [issue #644](https://github.com/moumantai-gg/mithril/issues/644).

### Naming conventions for folder interfaces and change events

**Decision (2026-05-22):** folder interfaces take the form `I<World><Domain>State`; change-event types are named in past-tense participle form (no `Event` suffix); folder-emitted events carry a mandatory world prefix (`Player…` / `Chat…`), view-emitted events never do. Migration is opportunistic — applied within in-flight migration PRs as they touch each type — and no dedicated rename-sweep PR is in scope.

**Rationale:** Phase 2 (#602, #603) is about to land at least five new folders and a dozen new change events; without convention, the keystone PR sets precedent by accident. The "first word identifies the emitter" invariant gives cold readers an at-a-glance signal that the prior "prefix-when-sibling-exists" rule did not. Full spec lives in the "Naming conventions" section above; no GitHub issue tracks this ratification (this PR is the canonical record).

### Call 1 — eager-always state services / lazy-only UI VMs

**Decision:** all folders, views, and module-side state machines that consume views/worlds subscribe **eagerly** during the `IHostedService` chain at startup. They run from session-start replay forward regardless of `IMithrilModule.DefaultActivation`. `DefaultActivation` (Eager/Lazy) survives but narrows in scope: it gates **UI hydration only** — XAML parsing, control instantiation, binding wiring, and UI-tier work that genuinely depends on the module's view being constructed (overlay-window installation, Win32 dispatcher hooks, dispatcher-bound bindings). It does NOT gate state subscription. UI VMs that hydrate lazily read canonical snapshots on construction (`view.TryGet*`, `view.Current`, …) and subscribe to bus events for forward changes; they do not "catch up" on missed frames because no frames are missed at the state layer.

**Rationale:** under the world-sim model the world is the canonical source of truth for game state derived from logs. It doesn't know which modules the user will visit, and it shouldn't — pre-computing all derivable state is the safe default. The previous "lazy modules' hosted services wait on `ModuleGate.WaitAsync`" pattern conflated state-subscription gating (which has no architectural justification under world-sim) with UI-hydration gating (which is a legitimate cost optimization). Resolves #695 point 2 and deprecates the prior "modules subscribe lazily" framing.

**Implementation status:** delivered via [PR #705](https://github.com/moumantai-gg/mithril/pull/705) (closed #695 + #676 coupled, 2026-05-23). All six audit-enumerated state-subscription gates retired — `Samwise.GardenIngestionService`, `Arwen.FavorIngestionService`, `Smaug.VendorIngestionService`, `Pippin.GourmandIngestionService`, `Legolas.Services.PlayerLogIngestionService`, `Legolas.Services.ItemCollectionTracker` — each now attaches its L1 / view-bus / skill-state subscriptions inside an explicit `StartAsync` override (synchronous subscribe before `await base.StartAsync`), so the trailing `WorldMergerStartHostedService` from #702 / Call 2 guarantees every subscriber is live before the merger drain begins. The `ModuleGates` ctor parameter was removed from each of the six services (no other use). The three Legolas UI-hydration gates (`AutoOverlayCoordinator`, `ForegroundFocusGate`, `OverlayController`) survive under the narrowed scope — they install overlay windows and Win32 dispatcher hooks that genuinely depend on the view being constructed. The `ModuleGate` primitive itself stays; its retirement is deferred to Phase 5 (see #700).

**Bootstrap ordering constraint (load-bearing for any Call-1-style migration):** the L1 ingestion services bind `DeliveryContext.Marshaled(Application.Current?.Dispatcher)` during their `StartAsync`. Under Call 2's `host.StartAsync().GetAwaiter().GetResult()` chain, `Application.Current` must be non-null when the chain runs — so `new App()` is constructed in `Program.cs` BEFORE `host.StartAsync()` (PR #705 commit `70eb7ca` moved it from after-StartAsync). Without that ordering, every Marshaled-vs-Inline fallback silently selects Inline and L1 envelopes deliver on the pump thread, violating the cross-thread `ObservableCollection` guarantee that #550 capability E provides. A future `Program.cs` refactor that swaps the ordering back would silently re-introduce the regression; any new migration that does Marshaled subscription in `StartAsync` must keep the App-before-host.StartAsync invariant intact OR add a deferred-dispatcher pattern.

See: [issue #695](https://github.com/moumantai-gg/mithril/issues/695).

### Three-surface contract for views

**Decision:** views expose the same three coherent consumption surfaces — **Query**, **React**, **Bind** — that [`docs/module-charters.md`](module-charters.md) canonizes for GameState services, each shaped for a structurally different question. A consumer asking *"what is true now?"* uses the synchronous **Query** snapshot read API (`view.TryGet*`, `view.Current`, …; for `IInventoryView` today: `TryResolve` / `TryGetStackSize`). A consumer asking *"tell me when something changes"* subscribes to the typed **React** `IWorldEventBus` channel. A consumer asking *"how does my UI stay in sync?"* binds to the **Bind** surface — `IReadOnlyObservableCollection<T>` (from `Mithril.Shared.Collections`) for collection-shaped views, `INotifyPropertyChanged` properties for single-value views. The worked example is `IInventoryView.Items` (shipped in [PR #739](https://github.com/moumantai-gg/mithril/pull/739)): the same canonical state the Query/React surfaces serve, exposed as a Dispatcher-marshaled observable collection so XAML binds directly without consumer-side dispatching.

The typed bus is intentionally fire-and-forget — no `SinceSubscribe` / replay-on-subscribe overload, no view-side event log buffering post-Start frames for late attachers. Call 2's trailing-merger ordering invariant guarantees no frames are missed for any subscriber attached during the `IHostedService.StartAsync` chain, and under Call 1 that population is every module-side state subscriber (folders, views, module-side state machines). Lazy WPF VMs that need point-in-time state hydrate from the Query snapshot on construction and bind to view properties via INPC for forward updates; lazy WPF VMs that need a live-bound collection bind directly to the Bind surface and skip the Query+React pairing entirely. Neither runs a local snapshot+delta mirror.

**Rationale:** the question that surfaced this entry was whether `ViewEventBus.Subscribe` should grow a `SinceSubscribe` overload for "late-attaching" consumers (#707, residual of #695 point 3). Under Call 1 there are no late attachers at the state layer — every state subscriber attaches during `StartAsync`, before Call 2's `WorldMergerStartHostedService` begins the merger drain. The only "late attachers" that remain are lazy WPF VMs at first-tab-activation, and those are served by the Query snapshot read surface (or by the Bind surface for collection-shaped views), not by replay-on-subscribe. A replay primitive on the typed bus would solve a problem the Call 1 + Call 2 pairing has already eliminated, at the cost of per-channel buffering memory and the unanswered question of how far back a "since" cursor should reach. The three-surface contract is therefore the canonical answer for every future cross-world view: eager `StartAsync` subscribe + Query snapshot (or Bind-surface binding) for hydration, no replay primitive.

**Bind-surface mechanics.** Mirroring the GameState services' Bind channel (per `module-charters.md` lines 188–196): collection-shaped views expose `IReadOnlyObservableCollection<T>` and own the dispatcher marshalling internally — mutations are scheduled on `Application.Current?.Dispatcher` before the underlying `ObservableCollection<T>` is touched, so UI consumers bind directly in XAML without consumer-side `Dispatcher.Invoke` calls. Headless / test contexts where `Application.Current?.Dispatcher` is null fall back to inline mutation (mirroring the existing [`VendorIngestionService` pattern](../src/Smaug.Module/State/VendorIngestionService.cs)). The Bind surface is read-only to consumers; the view is the sole writer. A Bind surface is not mandatory on every view — it is added when a consumer asks the *"how does my UI stay in sync?"* question. `IInventoryView.Items` was added because the inventory grid needed it; other views (`IPlayerSkillState`, `IPlayerRecipeState`, `IPlayerEffectsStateService`) add their own Bind surfaces under the same shape as their UI consumers materialize.

**Corollary for lazy WPF VMs.** A VM that re-applies bus frames to a locally cached copy of the view's state — reproducing the view's `_map` inside the VM and mutating it on every change event — is an antipattern, not a deferred sharp edge. The view's `_map` is the canonical state; a local mirror invites divergence with no recoverable upside, because any inconsistency becomes a bug whose root cause is split across two places. VMs that need point-in-time reads call `view.TryGet*` / `view.Current` per render or on demand; VMs that need a live-bound collection bind directly to the view's `IReadOnlyObservableCollection<T>` Bind surface; React bus events are change-notification only, never ledger inputs.

**`_eventLog` is shim-era infrastructure.** `InventoryView._eventLog` (and its `EventLogSoftCap` / `EventLogTrimChunk` / `AppendToEventLog` / `s_sinceSubscribeDiagFired` machinery) exists solely to preserve the pre-#602 union-shaped `Subscribe(Action<InventoryEvent>, ReplayMode)` surface for the six legacy consumers tracked under #659. It is not a typed-bus feature and is not a template for future views. When the last shim consumer migrates and #659 lands, the event log retires with it; no future view ships its own event log and no future bus ships a `SinceSubscribe` overload.

See: [issue #707](https://github.com/moumantai-gg/mithril/issues/707) (closed wontfix — this section is the design-rationale residual of [#695](https://github.com/moumantai-gg/mithril/issues/695) point 3); [issue #740](https://github.com/moumantai-gg/mithril/issues/740) (Bind-surface harmonization with `module-charters.md`'s three-channel pattern).

### Call 2 — `IHostedService` ordering invariant + `AddMithrilApp` composition

**Decision:** the world's merger does NOT start in the world's own `StartAsync`. A separate trailing `IHostedService` (e.g., `WorldMergerStartHostedService`) registered LAST in DI is responsible for calling each registered world's explicit-start method. By the time its `StartAsync` runs, every other hosted service's `StartAsync` has completed — every bus subscription, every folder/producer registration, every subscriber attachment. The "registered LAST" invariant is enforced structurally, not by discipline: a shared composition extension method (`AddMithrilApp` or similar) owns the full registration chain and appends `AddWorldMergerStart()` internally. The composition root calls just `services.AddMithrilApp()`. `AddWorldMergerStart` is `internal static`; test access goes through `[assembly: InternalsVisibleTo(...)]` for friend test assemblies that need partial-stack-plus-live-merger composition. Lower-level extensions (`AddPlayerWorld`, `AddChatWorld`, `AddMithrilGameState`, `AddMithrilModules`, `AddMithrilShell`) stay public for partial-stack tests that don't need a live merger. `BackgroundService` is explicitly rejected for the world's merger entry point — its `ExecuteAsync` runs at a non-deterministic moment relative to other services' `StartAsync`, which is the entire failure mode this decision corrects.

**Rationale:** today's `PlayerWorldHostedService` and `ChatWorldHostedService` are `BackgroundService` subclasses; their `StartAsync` schedules `ExecuteAsync` on the thread pool and returns immediately, racing with subsequent services' `StartAsync` work. Today's behaviour is benign-in-practice only because `RunMergerAsync` yields back to the host pump immediately on first `MoveNextAsync` — timing-fragile and load-bearing on assumptions about framework internals. Compounding this is a stale comment at `src/Mithril.Shell/DependencyInjection/ShellComposition.cs:65-76` claiming "the registration hosted service inside `AddMithrilGameState` therefore runs strictly before `PlayerWorldHostedService`"; that's directionally wrong (the world hosted service is registered first) and will mislead future reviewers regardless of this ratification.

**Implementation status:** delivered via [PR #702](https://github.com/moumantai-gg/mithril/pull/702) (closed #696, 2026-05-22). Renamed `IWorld.StartAsync` → `IWorld.StartMerger` with the explicit two-phase contract; deleted `PlayerWorldHostedService` + `ChatWorldHostedService`; added trailing `Mithril.Shell.DependencyInjection.WorldMergerStartHostedService` (internal `IHostedService`) appended LAST by the new public `ShellComposition.AddMithrilApp` umbrella; friend-assembly access to the internal `AddWorldMergerStart` via `[InternalsVisibleTo("Mithril.Shell.Tests")]`. The `ShellComposition.cs:65-76` stale comment was deleted alongside the chain it misdescribed.

See: [issue #696](https://github.com/moumantai-gg/mithril/issues/696).

### Call 3 — mode-gating sinks; projection-outwards framing

**Decision:** Mithril takes world-derived state and projects it outwards into real-world effects (audio, OS notifications, window flash, visual cues that touch the user's attention). That projection is only meaningful when the world's state matches present reality. During `Mode == Live`, projection is appropriate — events are happening now. During `Mode == Replaying`, the world is still reconstructing past state; projecting it outward would surface events the user already lived through (or already missed) in real time.

Therefore: any side-effect call site reached via a world-event subscription guards immediately before the side-effect with `if (_worldClock?.Mode == WorldMode.Replaying) return;`. State derivation upstream of the projection remains mode-agnostic — the world settles deterministically regardless of mode. A consumer that's wall-clock-driven (e.g., a `DispatcherTimer` on real time) is already operating in real-world time and sits structurally outside this contract; migrating it onto world events implies adding the guard at the same time.

**Rationale:** principle 12 of this document says "state derivation is mode-agnostic; side-effect-emitting consumers gate on `Mode == Live`." This ratification gives that principle its operational form — a sustained contract that applies to every sink whose firing is driven by world events. It is **not** a one-time audit of today's sinks; new sinks are gated by construction as they migrate onto world events. The projection-outwards framing is the load-bearing rationale: state is a model of the game; a sink lifts that model into the user's real world, and that lift is only honest when model-time matches real-time. Resolves #676.

**Implementation status:** delivered via [PR #705](https://github.com/moumantai-gg/mithril/pull/705) (closed #695 + #676 coupled, 2026-05-23). Both initial sinks gated: `Samwise.AlarmService.Fire` guards at top (audio + window flash + `AlarmTriggered` event all suppressed under Replaying); `Gandalf.TimerAlarmService.OnTimerReady` guards immediately before `Dispatch(...)`. State derivation upstream (the `_firedAt` dedup ledger writes in both services) stays mode-agnostic so a Replaying → Live transition preserves a coherent suppression contract. `TimerAlarmService` was refactored to consume `IAudioPlaybackSink` (matching `AlarmService`'s DI seam) instead of the static `AudioPlayer.Play` — necessary scaffolding so the Mode-gating is observable in tests. `Gandalf.ShiftAlarmService` is currently wall-clock-driven (`DispatcherTimer` against `IGameClock` reading `TimeProvider.System`) and was NOT on Call 3's initial sink list; when #613 (Gandalf scheduler collapse) migrates `ShiftAlarmService` to consume `CalendarTimeAdvanced` events, the mode-gating ships as part of that migration — by the principle, not as a follow-on.

See: [issue #676](https://github.com/moumantai-gg/mithril/issues/676).

### Call 4 — view-clock-backed `TimeProvider` for `PendingCorrelator`

**Decision:** the shared `PendingCorrelator<TKey,TReq>` primitive in `Mithril.Shared.Correlation` stays world-sim-unaware. Its constructor continues to accept a `TimeProvider`. The two in-tree consumers — both inside `IInventoryView` (`_pendingChat` and `_pendingAdd`, the bidirectional Tier-1 correlator that joins Player.log `ProcessAddItem` instance-id observations with chat `[Status] X xN added to inventory` count observations) — share a thin `TimeProvider` subclass whose `GetUtcNow()` returns the view-layer `IViewClock.Now` — `max(lastPlayerFrame, lastChatFrame)` per the view-clock semantics resolution (design notebook Q5). `DrainStale` calls happen inline from bus-subscribe handlers, never on a wall-clock timer; this is load-bearing for replay determinism — eviction must depend only on observed frame timestamps, not on real elapsed time.

**Rationale:** today's `IInventoryService.PendingCorrelator`s (now `IInventoryView`'s post-#602) use the view-clock-derived `TimeProvider` for TTL eviction. Under replay-from-session-start the same log + chat must produce the same eviction sequence regardless of how long Mithril has been attached — a determinism property the pre-#602 `TimeProvider.System` wiring violated. Routing TTL through the view clock anchors eviction to the frame trajectory rather than to real elapsed time. Resolves #675.

**Implementation status:** delivered for `IInventoryView`'s two correlators via #602's split, and made canonical by #699's retirement of the prior single-world `PendingCorrelator` in `Legolas.ItemCollectionTracker._pendingAdds`. The two `IInventoryView` correlators are now the sole `PendingCorrelator` consumers in the codebase; both are view-clock-backed and replay-deterministic by the construction above. Legolas's prior single-world correlator at `Legolas.Services.ItemCollectionTracker._pendingAdds` retired by #699 — under PlayerWorld-direct subscription the cross-source race the 5 s TTL was guarding against cannot happen by construction, so the FIFO that replaced it is bounded by `SurveyFlowController`'s session lifecycle rather than a wall-clock window. The pattern this entry documents — `TimeProvider`-subclass-over-view-clock for cross-world TTL gates — is therefore the canonical answer for any future cross-world correlator, not a one-time fix.

See: [issue #675](https://github.com/moumantai-gg/mithril/issues/675).

### User-action stamp clock — wall-clock default + paired-clock idiom

**Decision:** principle 13's "no real-wall-clock leaks into state machines or module-side scheduling" applies to **world-event-driven paths only**. User-action handlers — relay commands, hotkey invocations, pixel-clicks, user-initiated dismissals — that write module-owned adjacent state (per §Three categories of data the architecture admits) stamp with `TimeProvider.System.GetUtcNow()`, anchored in the user's perceived "now." This is consistent with the existing §Three categories rule that user-initiated changes apply immediately whether the world is Replaying or Live.

Within that carve-out there are two tiers (audited via #713's 9-site sweep):

- **Tier A — default.** User-action stamps whose downstream reach includes UI display (timer elapsed/remaining text, "checked X ago" age strings, "measured X ago" anchor freshness) use wall-clock. The cross-clock pair (wall-clock stamp + world-clock comparison gate downstream) is the normal shape, not an inconsistency to repair: world-clock asymptotically equals wall-clock in Live mode where the user actually lives, so the gate still fires at the user's expected wall-clock moment, and the displayed elapsed/remaining doesn't jump weirdly across a Replaying→Live transition. Seven sites land here: `Gandalf.TimerProgressService.Start/Restart` (timer click sites), `Gandalf.DerivedTimerProgressService.Dismiss` (loot/quest cooldown dismissal), `Samwise.GardenViewModel.MarkHarvested` and `Samwise.SamwiseHotkeys.MarkOldestRipeHarvestedCommand` (harvest click + hotkey), `Legolas.MapOverlayViewModel.RecordManualPosition` (manual anchor pixel-click), and `Mithril.Shell.UpdateStatusService.ReportResult/ReportError/ReportNotApplicable` (update-check completion stamps).

- **Tier B — paired-clock idiom.** When the stamp's *sole* downstream reader is a same-clock comparison gate *inside* a world-event handler — no UI display path — both ends share the world clock to keep the comparison internally consistent. The choice isn't "what's the user's now" but "what makes the gate internally coherent." The in-repo canonical references are `Gandalf.TimerAlarmService.SnoozeAll` (inline comment at `:55-57`) and `Samwise.AlarmService.SnoozeAll` (`:50-52`), both citing PR #609; the snooze stamp's only reader is the snooze gate that fires from `OnTimerReady` / `OnPlotChanged`, both reached via world events. The idiom is the carve-out's exception, not its rule — invoke it only when the stamp has no UI display reader.

The deciding question for any new user-action stamp is *where does this stamp get read from?* UI display → Tier A (wall-clock). Sole reader is an internal world-event-driven gate → Tier B (paired-clock, world). Mixed readers (display *and* gate, e.g. `TimerProgressService.StartedAt`, which feeds both `TimerView.State` elapsed display and `CheckExpirations` gating) → default to Tier A, because the display reader is the one the user notices.

**Why no typed `IUserActionClock` seam:** considered and rejected. The two-tier choice depends on per-site downstream-reader semantics (does this stamp reach UI display?), which a typed seam can't capture cleanly without either lifting downstream-reader inspection into the type system or proliferating one wrapper per stamp pattern. The canonical `TimeProvider` injection is sufficient; per-site inline comments at the Tier-B carve-out sites already articulate the paired-clock rationale next to the code that does it. Adding a typed seam would impose its own constraint (every stamp site picks a clock type, even ones where the answer is "obviously wall-clock") in exchange for documentation a comment can carry more readably.

**Rationale:** principle 13 is about replay determinism in state derivation. A user-action stamp is not part of the world's state-derivation graph — it's adjacent module-owned state written in direct response to a user input that itself has no world frame. Forcing such stamps onto the world clock during Replaying would create a user-expectation regression in the opposite direction: a timer started during drain would have `StartedAt` anchored to a historical instant the user is not at, and the displayed remaining time would jump forward when world-clock catches up to wall-clock. The two-tier pattern preserves the principle where it applies (world-event-driven paths) and the user's mental model where it doesn't (direct-input paths).

**Implementation status:** ratified by the [#713](https://github.com/moumantai-gg/mithril/issues/713) audit (closed wontfix-by-design alongside this paragraph; this PR adds inline micro-comments at `TimerProgressService.cs:93` and `:113` pointing here). The two Tier-B sites pre-existed this ratification with their inline doc-comments; no code changes required by this paragraph beyond those micro-comments. Adjacent event-driven wall-clock stamp leaks — not user-action paths, real principle 13 violations — are tracked separately in [#715](https://github.com/moumantai-gg/mithril/issues/715).

See: [issue #713](https://github.com/moumantai-gg/mithril/issues/713), [issue #715](https://github.com/moumantai-gg/mithril/issues/715).

---

## What this doc does NOT cover

- Complete state-machine semantics for each folder/composer. The world runtime is mostly today's GameState services with the `BackgroundService`-per-service shell replaced by folder/composer registration (per principle 10); per-service transition tables are in their source files.
- Detailed view-layer implementations. The contracts and worked examples here are starting points; concrete views write themselves once the world layer is in place.
- Live-vs-replay mode switching at the implementation level. Each world tracks `Mode` (principle 12) and consumers gate side effects accordingly; the *mechanism* a world uses to detect "drained, now on the live tail" is producer-implementation-specific (e.g., the Player.log tail's notion of "I'm now `await`ing the next live append"). The world doesn't dictate how producers reach catch-up; it just observes the result.
- Performance characteristics under high-frame-rate scenarios (combat ticks, asset-loading bursts). The dispatch graph fans out per-frame; load behavior is empirical and won't be known until the migration is far enough along to measure.
