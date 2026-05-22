# World-sim migration audit

> **Vocabulary:** see [`docs/glossary.md`](glossary.md) for definitions of the world-sim terminology used in this doc.

Audit date: 2026-05-21
Audited against: [`docs/world-simulator.md`](world-simulator.md) and
[`docs/module-signal-map.md`](module-signal-map.md) at commit `51e39f0` (branch
`docs/world-simulator-architecture`; both docs are *not yet* on `main` or
`feat/effects-state-service` — read via `git show` for this audit).

> **Note on staleness:** This audit is a snapshot. The design notebook has
> evolved further since `51e39f0`: the folder/composer/producer taxonomy was
> narrowed (producers now restricted to external-input sources only, not
> wake-at-T), a `WorldMode` concept was added (principles 12 + 13), and
> `Mithril.GameReports` was extracted as a separate foundation-layer assembly.
> The audit's *findings* (15 components, 5 needing migration, 3 sleeper
> blockers, the per-component classifications) remain valid; only some
> terminology and a few migration-item details are stale relative to the
> latest `world-simulator.md`. Read this audit as "what the components
> looked like when audited"; read `world-simulator.md` for the current
> architectural target.
>
> **Inventory section updated 2026-05-22 post-#679.** The Inventory row, exec
> summary item #1, Samwise/Arwen/Palantir cross-references to inventory, and
> spot-check #1 were rewritten after the #602 split shipped in
> [#679](https://github.com/moumantai-gg/mithril/pull/679). The rest of the
> doc remains the 2026-05-21 snapshot — read other rows in pre-Phase-2 tense.

## Executive summary

- **15 components audited** across `Mithril.GameState`, ten modules, and
  `Mithril.Shared` (state-bearing leaves only). **5 components need behavioural
  changes** (split / migrate / restructure); **3 are sleeper blockers**.
- **Five of ten modules are pure projectors** (Pippin, Elrond, Bilbo,
  Silmarillion, Celebrimbor) — confirmed no log subscriptions, no FSM state.
  No migration owed.
- **Highest-risk migrations, ranked:**
  1. **`IInventoryService` split** — five different state surfaces fed it
     (Player.log, chat, FileSystemWatcher, reference data, `_seededStackSizes`
     reconcile); every other migration sat downstream of it.
     **Delivered ([#602](https://github.com/moumantai-gg/mithril/issues/602) via
     [#679](https://github.com/moumantai-gg/mithril/pull/679)).** Remaining work
     is the per-consumer migration off the `[Obsolete]` shim, tracked in
     [#659](https://github.com/moumantai-gg/mithril/issues/659).
  2. **`SarumanCodebookService` split + view** — different shape than Inventory
     (no temporal pairing, key-join only) so the view-layer abstraction needs
     to admit two distinct view patterns.
  3. **`Legolas.LogIngestionService` full retirement (per #531)** —
     **Delivered ([#606](https://github.com/moumantai-gg/mithril/issues/606)).**
     Last direct `IChatLogStream` module consumer retired; all five chat verbs
     migrated to Player.log equivalents. The new
     `Legolas.Services.ItemCollectionTracker` owns the intra-module Tier-1
     Add↔Collect correlator (Add side via `IInventoryView.Bus`, Collect side
     via `PlayerLogParser`). `MotherlodeMeasurementCoordinator` already
     same-source since #604.
- **Patterns to systematize:** every wall-clock `_time.GetUtcNow()` in a
  transition gate (12 instances across 7 components) is the same mechanical
  swap to `IWorldClock.Now` after the sim lands. Both planned parsers
  (`ServerCatalogParser`, `ConnectionEventParser`) and the wake-at-T producer
  are entirely greenfield — no existing code to migrate.

---

## Foundation services (Mithril.GameState)

### `IGameSessionService`

`src/Mithril.GameState/Sessions/GameSessionService.cs`,
`LoginBannerParser.cs:50-87`.

- **Source spanning**: single source (Player.log via L1 LocalPlayer pipe filtered
  to `LoginBanner`). Clean.
- **Wall-clock**: none.
- **Synthesis**: none.
- **Scope**: world (per-server target) — today's banner format carries no Server
  field (`LoginBannerParser.cs:50`), so the scope is `(_, Character)`-only until
  the planned `ConnectionEventParser` lands.
- **Migration**: minor. Add `Server` field to `GameSession` once
  `ServerCatalogService` + `ConnectionEventParser` ship. No structural change.
- **Status**: green.

### `IPlayerSkillStateService`, `IPlayerRecipeStateService`, `IPlayerEffectsStateService`, `IPlayerCelestialStateService`

`src/Mithril.GameState/{Skills,Recipes,Effects,Celestial}/`.

- **Source spanning**: single (LocalPlayer pipe). Clean.
- **Wall-clock**: none (event-time arithmetic only).
- **Synthesis**: none.
- **Pump**: all `BackgroundService`s with their own L1 `ILogStreamDriver.Subscribe<LocalPlayerLogLine>`
  subscription — under the sim model the pump moves up to `PlayerWorldSim` and
  these become `IFrameHandler<LocalPlayerLogLine>`. **Mechanical**.
- **Scope**: character (skills, recipes, effects), world (celestial).
- **Status**: green. Mechanical migration to handlers.

### `IPlayerAreaTracker`

`src/Mithril.GameState/Areas/PlayerAreaTracker.cs:47-307`.

- **Source spanning**: single (SystemSignal AreaLoading) post-#556.
- **Wall-clock at `:292`**: `Apply(line, DateTime.UtcNow)` inside `SeedFromLog`'s
  reverse-scan. **Stamping (not gating)** — the comment explicitly says "for the
  seed we only care about the AreaKey, not the timestamp". Doc-level OK.
- **Synthesis**: `Observe(line, ts)` legacy push-in surface still exists for
  pre-#556 callers (Legolas / Gandalf no longer use it post-migration — confirmed
  via Grep, no live `Observe` call sites remain in modules). Could be deleted.
- **Status**: green. Optional cleanup: drop `Observe`.

### `IPlayerPositionTracker`, `IPlayerPinTracker`, `IPlayerWeatherTracker`

`src/Mithril.GameState/{Movement,Pins,Weather}/`.

- **Source spanning**: single (unified classified pipe). Each owns its own
  intra-handler area-tracking fold to dodge the pre-#556 race.
- **Wall-clock**: none observed in state mutation.
- **Status**: green. Mechanical handler migration.

### Inventory (`IPlayerInventoryState` / `IChatInventoryState` / `IInventoryView`) — **SPLIT DELIVERED ([#602](https://github.com/moumantai-gg/mithril/issues/602) via [#679](https://github.com/moumantai-gg/mithril/pull/679))**

Pre-#602: `InventoryService.cs` — a single service spanning Player.log AND chat
with in-service `PendingCorrelator` + `FileSystemWatcher` reconcile (violating
world-sim principle 3). Post-#602: two folders + a view, with the legacy
`IInventoryService` interface retained only as an `[Obsolete]` shim that
resolves to the view (for the six pre-existing consumers; cleanup tracked in
[#659](https://github.com/moumantai-gg/mithril/issues/659)).

- **Player.log half** → `IPlayerInventoryState`
  (`src/Mithril.GameState/Inventory/PlayerInventoryStateService.cs`), fed by
  `Producers/PlayerInventoryFrameProducer.cs`. Instance-id ledger folded from
  `ProcessAddItem` / `ProcessDeleteItem`; emits `PlayerInventoryAdded` /
  `PlayerInventoryRemoved` / `PlayerInventoryStackUpdated` on
  `IPlayerWorld.Bus`. No stack-size column — that lives in the view.
- **Chat half** → `IChatInventoryState`
  (`src/Mithril.GameState/Inventory/ChatInventoryStateService.cs`), fed by
  `Producers/ChatInventoryFrameProducer.cs`. Name-keyed time-series folded
  from the `[Status] X xN added to inventory.` chat verb; emits
  `ChatInventoryObserved` on `IChatWorld.Bus`. Recorder, not ledger — chat
  carries no removal signal.
- **View** → `IInventoryView` backed by `InventoryView`
  (`src/Mithril.GameState/Inventory/InventoryView.cs`). Subscribes to the
  three Player change events on `IPlayerWorld.Bus` and the one Chat change
  event on `IChatWorld.Bus`; composes via a bidirectional
  `PendingCorrelator<ScopedKey, …>` (5s `PendingChatTtl`, two halves
  `_pendingChat` / `_pendingAdd` at `InventoryView.cs:113-114`). The view also
  emits its own typed change events on its own bus
  (`InventoryItemAdded` / `InventoryItemRemoved` / `InventoryStackChanged`) —
  the canonical post-migration consumer surface.
- **Correlator scope key**: `ScopedKey = (Server, Character, InternalName)`
  (`InventoryView.cs:157`). The Server + Character pair comes from
  `IGameSessionService` (Player.log side) and `IChatSessionService` (chat
  side); chat observations whose session disagrees with the player session
  drop with a diagnostic — cross-character correlation hazard eliminated
  (iteration-2 acceptance criterion).
- **Correlator clock**: `IViewClock` / `ViewClock`
  (`src/Mithril.GameState/Inventory/IViewClock.cs`) — `Now = max(lastPlayerFrameTs,
  lastChatFrameTs)` advancing only when the view observes a frame on either
  bus. Wired as the correlator's `TimeProvider`, so the 5s TTL gate is
  replay-deterministic: the simulated clock advances by event time, not
  wall-clock. Resolves design notebook Q5; `Frames = (Player, Chat)` tuple
  exposes per-side timestamps for tests + diagnostics.
- **Seed reconcile**: `FileSystemWatcher` retired. The sole seed-refresh
  signal is `IGameReportsService.StorageReportsChanged` (per
  [#612](https://github.com/moumantai-gg/mithril/issues/612)); the
  `_seededStackSizes` map, the non-stackable confirm pass, and the
  single-instance stackable reconcile pass moved into
  `InventoryView.LoadExportSeeds` / `OnGameReportsStorageChanged`. Export
  read goes through `IGameReportsService.GetStorageContents` — `InventoryView`
  no longer touches the filesystem directly.
- **Legacy shim**: `IInventoryService.Subscribe(Action<InventoryEvent>)` and
  the union-shaped `InventoryEvent` type survive only as `[Obsolete]` surfaces
  on `InventoryView` so the six pre-#602 consumers (Arwen, Samwise, Palantir,
  Legolas, Saruman, `MotherlodeMeasurementCoordinator`) keep working unchanged.
  Per-consumer migration to the typed bus is the cleanup obligation tracked
  in [#659](https://github.com/moumantai-gg/mithril/issues/659); follow-on
  consumer PRs are filed against that issue as each consumer's
  `[Obsolete]` warnings get addressed.
- **Status**: **split delivered**. The keystone Phase 2 work landed in
  [#679](https://github.com/moumantai-gg/mithril/pull/679). Remaining
  obligations: (1) the six consumer migrations under
  [#659](https://github.com/moumantai-gg/mithril/issues/659); (2) eventual
  deletion of `IInventoryService` / `InventoryEvent` / the `[Obsolete]`
  annotations once those six consumers are off the shim.

### `IQuestService` ⚠️ **SYNTHESIS + WALL-CLOCK STAMP**

`src/Mithril.GameState/Quests/QuestService.cs:33-364`.

- **Source spanning**: single (LocalPlayer pipe).
- **Synthesis**: `OnViewCurrentChanged` at `:262-303` triggers `ReloadFromView()`
  which **synthesizes** `Accepted` / `Abandoned` / `Completed` events on
  character switch, stamped with `_time.GetUtcNow().UtcDateTime` at `:283`. This
  is the **second event source** flagged in the module signal map.
- **Wall-clock**: the `:283` stamp is a synthesized timestamp on synthetic
  events — neither a transition gate nor a real observation. Under per-character
  sim scope, the entire synthesis path collapses: each character has its own
  ledger; switching is a binding swap, not a state mutation.
- **Other concern**: this service conflates reference (quest definitions) with
  state (am I on quest X?). Reference half is already in
  `IReferenceDataService.Quests` (the `_refData.Quests` lookup at `:182-187`).
  Splitting probably ships as `IPlayerQuestJournalService`.
- **Migration**: per design notebook item 6 — collapse `OnViewCurrentChanged`
  synthesis under per-character sim scope; extract reference half.
  `HandleJournalLoaded` log-derived synthesis (at `:178-209`, deriving
  `Abandoned` from absence in a `ProcessLoadQuests` snapshot) **stays** — that's
  a real log-driven inference, not character-switch theatre.
- **Status**: **restructure owed**.

---

## Modules

### Samwise (gardening)

`src/Samwise.Module/State/GardenStateMachine.cs`,
`src/Samwise.Module/Alarms/AlarmService.cs`.

- **Source spanning**: single (LocalPlayer via `GardenIngestionService`) + reads
  the `IInventoryService.Subscribe` `[Obsolete]` shim on `InventoryView` for
  `Added`/`Deleted` reshape. Post-#679 the underlying surface is split
  (`IPlayerInventoryState` / `IChatInventoryState` / `IInventoryView`); Samwise
  still consumes the shim until its per-consumer migration to
  `IInventoryView.Bus.Subscribe<InventoryItemAdded>(…)` lands. Tracked in
  [#659](https://github.com/moumantai-gg/mithril/issues/659).
- **Wall-clock transition gates**:
  - `GardenStateMachine.PruneWithered` at `:541-557`: `_time.GetUtcNow() - p.UpdatedAt > ttl`.
  - `GardenStateMachine.IsLikelyGarbageCollected` at `:596-600`.
  - `AlarmService.OnPlotChanged` at `:78`, `:85`, `:88-89`:
    `DateTimeOffset.UtcNow` for snooze gating + dedup-stamps. Snooze check at
    `:85` is a transition gate.
- **Migration**: every `_time.GetUtcNow()` / `DateTimeOffset.UtcNow` in the
  transition paths becomes `_worldClock.Now`. Mechanical. Mutation surface is
  small: `GardenStateMachine` already takes `TimeProvider` ctor arg.
- **Status**: **migration owed** — mechanical.

### Pippin (food)

`src/Pippin.Module/State/GourmandStateMachine.cs` + `GourmandIngestionService`.

- **Source spanning**: single (LocalPlayer `FoodsConsumedReport`).
- **Wall-clock**: none.
- **Pure projector** modulo one fold. Confirmed pure projector, no migration
  needed — mechanical handler move only.

### Arwen (NPC favor) ✅ **RESOLVED**

`src/Arwen.Module/Domain/CalibrationService.cs` + `src/Arwen.Module/State/FavorIngestionService.cs`.

- **Source spanning**: single (LocalPlayer) at the log level. The
  `IInventoryService.TryGetStackSize` read inside `RecordObservation`
  remains, but as a same-source read against the view's composed map
  (sim-coherent under the Player.log dispatch order, with the cross-source
  composition encapsulated inside the view layer per principle 4).
- **Cross-FSM TryResolve peek**: **eliminated in #608** via the Tier-2
  `IGiftSignalService` lift (the architectural payoff #594 / #596 created
  the signal service for). `FavorIngestionService` now subscribes to
  `IGiftSignalService.Subscribe` for resolved gift events;
  `GiftSignalService` owns a single L1 subscription with its own
  `ProcessAddItem`-fed `instanceId → InternalName` map, correlates the
  full verb triple (`ProcessStartInteraction` / `ProcessDeleteItem` /
  `ProcessDeltaFavor`) on its own pump, and emits a fully-resolved
  `GiftAccepted` with the `InternalName` baked in. The React-channel
  `Subscribe` contract replays the in-session event log atomically to
  late subscribers (#585 contract), so attach order vs the L1 driver is
  irrelevant — no cross-pump race on subscribe-late, no `TryResolve`
  needed. The new `CalibrationService.OnGiftAccepted(GiftAccepted)`
  entry point goes directly to `RecordObservation`.
- **Wall-clock**: `_time.GetUtcNow()` at the no-timestamp `OnStartInteraction`
  / `OnItemDeleted` / `OnDeltaFavor` overloads — all **test-only fallback
  overloads** for callers that don't plumb a real timestamp. Production
  calls go through `OnGiftAccepted` (uses the signal service's resolved
  timestamps) and the timestamp-aware L1 overloads. Not gating.
- **Migration**: Arwen does not consume the
  `Subscribe(Action<InventoryEvent>)` shim — it uses `TryGetStackSize`
  (not on the `[Obsolete]` surface) and the Tier-2
  `IGiftSignalService.Subscribe` channel, so it has no #659 cleanup
  obligation.
- **Status**: **resolved post-#608** — cross-FSM peek replaced by
  consumption of the Tier-2 signal service that owns the verb-triple
  correlation on a single L1 pump.

### Saruman (Words of Power) ⚠️ **CROSS-SOURCE**

`src/Saruman.Module/Services/SarumanCodebookService.cs:14-175`,
`SarumanChatIngestionService.cs:42-101`, `SarumanDiscoveryIngestionService.cs:53-`.

- **Source spanning**: **YES.** `SarumanCodebookService` is mutated by two
  ingestion services with separate L1 subscriptions:
  - Discovery (Player.log): `Subscribe<LocalPlayerLogLine>` →
    `RecordDiscovery(...)`.
  - Spent (chat): `Subscribe<RawLogLine>` → `MarkSpent(code, …)`.
- **Structural difference from Inventory**: no temporal pairing — join is by
  word code only, no TTL, no correlator. Discovery and Spent can be hours/days
  apart on the same record.
- **Wall-clock**: `DateTime.UtcNow` in `SarumanViewModel.cs:119` — for the
  user-driven "mark spent" UI action, not log ingestion. Outside sim scope
  (user input).
- **Migration**: matches design notebook's worked example 2. Split:
  - Player.log half → `IPlayerWordOfPowerDiscoveryState` (`Code → discovery
    record`).
  - Chat half → `IChatWordOfPowerStateMachine` (`Code → spent timestamp`).
  - View → `IWordOfPowerView` plain dictionary merge.
- **Status**: **migration owed**. Second-priority after Inventory; structurally
  different so the view-layer abstraction needs to admit both patterns.
- **Charter note**: per module-signal-map.md `:377`, Saruman is NOT in
  CLAUDE.md's module table. Pre-existing oversight.

### Legolas (surveying)

`src/Legolas.Module/Services/{PlayerLogIngestionService.cs,
ItemCollectionTracker.cs, MotherlodeMeasurementCoordinator.cs,
AreaCalibrationService.cs}`.

- **Source spanning**: NO — post-#606 Legolas is Player.log-sim-resident with
  zero direct `IChatLogStream` consumers. The previous five chat verbs all
  retired to same-source Player.log equivalents:
  - `ItemAddedToInventory` → `IInventoryView.Bus.Subscribe<InventoryItemAdded>`
    (post-#602 split — view-layer composer over the PlayerWorld + ChatWorld
    bus channels; the chat side is now folder-resident, not module-direct).
  - `ItemCollected` → `PlayerLogParser.ItemCollectedRx` parsing
    `ProcessScreenText(ImportantInfo, "<Mineral> collected!")` — new in #606.
  - `MotherlodeDistance` → `ProcessScreenText(ImportantInfo, "The treasure is N meters from here.")` (#604).
  - `SurveyDetected` → `ProcessMapFx` trailing string arg, parsed inline by
    `PlayerLogParser.TryParseMapFxRelativeOffset` and fed to
    `IAreaCalibrationService.NoteSurvey` from `PlayerLogIngestionService.HandleMapTarget` (#606).
  - `AreaEntered` → `PlayerAreaTracker.Changed` (#605).
- **Cross-FSM peeks**: `PlayerLogIngestionService` synchronously reads
  `PlayerAreaTracker.CurrentArea`, `AreaCalibrationService.CurrentCalibration`,
  `SurveyFlowController.CurrentState` mid-handler. Sim-coherent under declared
  dispatch order (intra-PlayerWorld).
- **Wall-clock**: `SurveyFlowController.cs` has `_time.GetUtcNow()` — needs
  read to determine if gates or stamps; `LegolasReportService.cs` also has it.
  Not on the critical state path; mostly UI / share-card stamping.
- **Status**: **done** — chat-tail retirement complete (#606, Phase 3 of
  #601). `LogIngestionService.cs`, `ChatLogParser.cs`, `IChatLogParser` and
  the Legolas `IChatLogStream` ctor arg deleted. `ItemCollectionTracker` owns
  the in-module Tier-1 correlator (now intra-module: Add side via
  `IInventoryView.Bus`, Collect side via `PlayerLogParser`).

### Elrond, Bilbo, Silmarillion, Celebrimbor, Pippin

Confirmed pure projectors — no log subscriptions, no FSM mutation, no
GameState subscriptions for state mutation. No migration needed beyond the
mechanical "consume views instead of services" wrapper pass once the layer
exists. One-line note per design directive.

### Gandalf (timers + alarms) ⚠️ **WALL-CLOCK HEAVY**

`src/Gandalf.Module/Services/{TimerProgressService.cs, TimerExpirationScheduler.cs,
ShiftAlarmService.cs, TimerAlarmService.cs, LootSource.cs, DerivedTimerProgressService.cs,
QuestSource.cs}`.

- **Source spanning**: single (LocalPlayer via `LootIngestionService`) +
  `IQuestService.Subscribe` consumer.
- **Wall-clock transition gates**: 12+ `_time.GetUtcNow()` call sites:
  - `TimerProgressService.cs:93, :113, :228` — `Start`, `Restart`,
    `CheckExpirations` (the canonical gate — `if (now < firingAt) continue;` at
    `:236`).
  - `TimerExpirationScheduler.cs:98` — `Reschedule` floor.
  - `ShiftAlarmService.cs:68, :129` — `NextScheduledAt`, `Reschedule` floor.
  - `TimerAlarmService.cs:46, :69` — snooze + dismiss-tracking.
  - `LootSource.cs:199, :258, :575` — cooldown anchoring + expiration check at
    `:258` is a transition gate.
  - `DashboardAggregator.cs:54, :86, :117` — dashboard projections (display,
    not state).
  - `QuestSource.cs:119` — cooldown ready check (gate).
- **Wake-at-T producer**: `TimerExpirationScheduler` (DispatcherTimer) +
  `ShiftAlarmService` (DispatcherTimer) — two independent wake-at-T schedulers
  driving `CheckExpirations`. Both poll the same wall-clock; under the sim
  they become **synthetic-frame producers** that mint a wake frame at the
  target firing time (design notebook §11).
- **Synthesis**: user-created timer definitions are persisted, user-action state
  (outside sim scope per design notebook open question §6).
- **Migration**: mechanical clock swap (`_time.GetUtcNow()` → `_worldClock.Now`)
  + synthetic-frame producer for wake-at-T. The TimerProgressService's persistence
  is already per-character (`PerCharacterView<GandalfState>`); scope already
  correct.
- **Status**: **migration owed** — by far the largest clock-swap surface.

### Smaug (vendor calibration)

`src/Smaug.Module/State/VendorIngestionService.cs:53-`.

- **Source spanning**: single (LocalPlayer pipe via L1).
- **Wall-clock**: none in mutation path.
- **Status**: green. Mechanical handler migration.

### Palantir (dev/debug shell)

Read-only consumer of `IInventoryService` (`LiveInventoryViewModel.cs:19-42`).
No FSM, no log subscription. Reactor only — subscribes via
`_inventory.Subscribe(OnEvent)` at `:55`, which post-#679 lands on the
`[Obsolete]` shim that `InventoryView` implements. Typed-bus migration to
`view.Bus.Subscribe<InventoryItemAdded>(…)` is one of the six follow-ons
tracked in [#659](https://github.com/moumantai-gg/mithril/issues/659).

---

## Migration-plan spot-checks

### 1. `IInventoryService` split — **DELIVERED (#602 via [#679](https://github.com/moumantai-gg/mithril/pull/679))**

Pre-#602 the cross-source span was confirmed at `InventoryService.cs:250`
(Player.log L1) + `:261` (chat L1) — a five-input service (Player.log, chat,
FileSystemWatcher, reference data, `_seededStackSizes` reconcile). PR #679
retired the pre-split file entirely and shipped the worked-example-1 shape:
`IPlayerInventoryState` folder (`PlayerInventoryStateService.cs` + producer),
`IChatInventoryState` folder (`ChatInventoryStateService.cs` + producer), and
`IInventoryView` (`InventoryView.cs`) as the cross-source composer. See the
rewritten Inventory row above for the post-#679 file/line cites. Remaining
work is the per-consumer migration off the `[Obsolete]`
`Subscribe(Action<InventoryEvent>)` shim, tracked in
[#659](https://github.com/moumantai-gg/mithril/issues/659).

### 2. `SarumanCodebookService` split — **CONFIRMED**

Cross-source via two ingestion services:
`SarumanDiscoveryIngestionService.cs:62-74` + `SarumanChatIngestionService.cs:42-101`.
Both mutate `SarumanCodebookService` (`:64 RecordDiscovery`, `:120 MarkSpent`).
Different shape than Inventory: key-only join, no TTL. View needs different
abstraction.

### 3. `MotherlodeMeasurementCoordinator` migration — **DONE (#604)**

The coordinator's distance side migrated to
`ProcessScreenText(ImportantInfo, "The treasure is N meters from here.")` on
Player.log; the use side was already `ProcessDoDelayLoop` on Player.log.
Single-source PlayerWorld SM today; the cross-source Tier-2 reference in
`docs/cross-source-correlation.md` retired with it.

### 4. `AreaCalibrationService` chat redundancy — **DONE (#605)**

`PlayerAreaTracker.Changed` (fed by Player.log's `LOADING LEVEL` line) is the
authoritative source. The chat `Entering Area:` path was removed; the
`PlayerLogIngestionService.ApplyAreaIfChanged` bridge drives
`IAreaCalibrationService.SelectArea` on every change.

### 5. Legolas full chat retirement — **DONE (#606)**

All five chat verbs migrated to Player.log equivalents:
- `ItemAddedToInventory` → `IInventoryView.Bus.Subscribe<InventoryItemAdded>` (#602 split)
- `ItemCollected` → `PlayerLogParser.ItemCollectedRx` (`ProcessScreenText(ImportantInfo, "<Mineral> collected!")`)
- `MotherlodeDistance` → same-source via #604
- `SurveyDetected` → `ProcessMapFx` trailing-arg `TryParseMapFxRelativeOffset`
- `AreaEntered` → `PlayerAreaTracker.Changed` (#605)

`LogIngestionService.cs`, `ChatLogParser.cs`, `IChatLogParser`, and the
Legolas `IChatLogStream` ctor argument all deleted. The Tier-1 Add↔Collect
correlator now lives intra-module in `ItemCollectionTracker.cs`.

### 6. `QuestService.OnViewCurrentChanged` synthesis — **CONFIRMED**

`QuestService.cs:73` wires `_view.CurrentChanged += OnViewCurrentChanged` →
`:262 OnViewCurrentChanged` → `:276 ReloadFromView` → diff against `Current`,
synthesize `Accepted` / `Abandoned` / `Completed` events stamped at `:283 var
nowStamp = _time.GetUtcNow().UtcDateTime`. Under per-character sim scope this
whole path collapses — each character has its own quest journal handler,
character-switch is a UI binding swap not a state mutation. The log-derived
`HandleJournalLoaded` Abandoned synthesis at `:178-209` is unrelated and
**stays** (real inference from `ProcessLoadQuests`).

### 7. Arwen's `_inventory.TryResolve` peek — **RESOLVED in #608**

`TryResolve` no longer called on the gift-detection path.
`FavorIngestionService` subscribes to `IGiftSignalService.Subscribe` for
fully-resolved `GiftAccepted` events; the Tier-2 signal service owns a
single L1 subscription with its own `ProcessAddItem`-fed
`instanceId → InternalName` map and correlates the verb triple on that
one pump. The signal service's React-channel `Subscribe` contract
replays the in-session resolved-gift log atomically to late subscribers
(#585), so the iteration-1 "bus has no replay" gap is closed as well.
`TryGetStackSize` (inside `RecordObservation`) remains as a same-source
read against the view's composed map — sim-coherent under the Player.log
sim's dispatch order with the cross-source composition encapsulated inside
the view layer per principle 4.

### 8. Wall-clock `_time.GetUtcNow()` state-decision uses — **12 instances, classified**

Found via Grep (`src/`, all `_time.GetUtcNow()` and `DateTime.UtcNow`):

**Transition gates (migrate to `IWorldClock.Now`):**
- `InventoryService.cs:182-183` — `PendingCorrelator` TTL (only foundation gate).
- `GardenStateMachine.cs:543` (`PruneWithered`), `:598` (`IsLikelyGarbageCollected`).
- `AlarmService.cs:85` (snooze check).
- `TimerProgressService.cs:228` (`CheckExpirations`).
- `TimerExpirationScheduler.cs:98` (reschedule floor).
- `ShiftAlarmService.cs:129` (reschedule floor).
- `LootSource.cs:258` (cooldown expiration check).
- `LootSource.cs:575` (`FireReady` eager-fire gate — reclassified from stamp
  during the #609 migration; the comparison `atUtc <= _time.GetUtcNow()`
  decides whether to fire `TimerReady`, so it's structurally a gate).
- `QuestSource.cs:119` (cooldown ready check).
- `TimerAlarmService.cs:69` (dismiss-tracking — borderline; check at use site).
- `PendingCorrelator.cs:131, :216` (drain + take TTL — primitive itself).
- `TtlList.cs:51, :119` (primitive itself).

**Stamps (OK to leave or trivially swap):**
- `InventoryService.cs:610, :648` — reconcile event timestamps.
- `QuestService.cs:283` — synthesized character-switch event stamp (path
  collapses entirely per item 6).
- `CalibrationService.cs:167, :181, :204` (Arwen) — test-only fallback overloads.
- `TimerProgressService.cs:93, :113` — `StartedAt` record stamps.
- `TimerAlarmService.cs:46` — snooze-until calculation (stamp + add).
- `LootSource.cs:199` — observation stamp (no-timestamp overload's
  default; the public-overload path is stamping the rejection time, not
  gating).
- `DerivedTimerProgressService.cs:104` — `DismissedAt` stamp.
- `DashboardAggregator.cs:54, :86, :117` — UI display projections.
- `LegolasReportService.cs` — share-card report stamping.
- `PerfTracerHostedService.cs`, `BindingErrorTraceListener.cs`,
  `DiagnosticsSink.cs` — infrastructure observation timestamps (outside sim
  scope).

**Module signal map claim that "wall-clock-gated transitions live in five
places"** is a slight undercount — counting consumer call sites (not primitive
sites), Gandalf alone has 6 gates (`TimerProgressService.CheckExpirations`,
`TimerExpirationScheduler.Reschedule`, `ShiftAlarmService.Reschedule`,
`LootSource.cs:258`, `LootSource.cs:575`, `QuestSource.cs:119`), plus Samwise
(2), Inventory (1), AlarmService (1) = 10 transition-gate sites across 4
components (the `LootSource.cs:575` `FireReady` site was reclassified from
stamp during the #609 migration). Same migration mechanism, different count.

### 9. `ServerCatalogParser` — **NOT IMPLEMENTED**

Grep `src/` for `ServerCatalog`, `IServerCatalogService`, `Servers:` parser
yields zero matches outside docs / classifier comments. Greenfield. The
classifier (`PlayerLogLineClassifier.cs:104`) routes `EVENT(Ok): connected`
to `LineKind.Anomaly` today.

### 10. `ConnectionEventParser` — **NOT IMPLEMENTED**

No parser for `EVENT(Ok): connected, url=…` exists. Grep confirms. The
`SystemSignalKind.SessionLifecycle` enum at `PlayerLogLineClassifier.cs:102`
only routes `loginCharacter | playing | sessionUpdate`. The `connected` phase
is explicitly excluded (`:104 EVENT(Ok): connected … falls to anomaly`).
Greenfield.

### 11. Wake-at-T synthetic-frame producer — **CONFIRMED Gandalf shape**

Gandalf today has two `DispatcherTimer`-driven wake-at-T schedulers:
- `TimerExpirationScheduler.cs:37-141` — schedules at the soonest `FiringAt`
  across all running user-timer definitions; on tick, runs
  `_progress.CheckExpirations()` and reschedules. Owns a `DispatcherTimer`,
  reads `_clock.GetUtcNow()` for the delay calculation.
- `ShiftAlarmService.cs` — second `DispatcherTimer` for PG in-game time-of-day
  shift transitions; reads `_time.GetUtcNow()` to compute the next interval.

Both become synthetic-frame producers: schedule a frame at the target firing
time; the sim merges into the frame queue; the existing `CheckExpirations`
becomes the handler's apply logic, gated on the sim's `Now` instead of
`_clock.GetUtcNow()`.

---

## Patterns and themes

- **Foundation-layer cross-source services are exactly two**: `IInventoryService`
  and `SarumanCodebookService`. Module-signal-map's claim is accurate. Both
  split + view-ify; no other foundation cross-source consumer hides anywhere.
- **`IChatLogStream` is consumed by exactly one place outside the L1 driver
  itself**: `Legolas.LogIngestionService.cs:21,31,61`. The retirement is a
  single-file delete (plus `ChatLogParser`), not a sweep.
- **Self-pump assumption is universal**: every state-bearing GameState service
  is a `BackgroundService` with its own `ILogStreamDriver.Subscribe<T>` call in
  `ExecuteAsync`. None of them know about handler dispatch. Under the sim, the
  pump moves up and every `ExecuteAsync` body collapses to `IFrameHandler.Apply`.
  **Mechanical migration, but it's every service.**
- **`DeliveryContext.Inline` is the dominant choice** — only Smaug
  (`VendorIngestionService.cs`) uses `Marshaled`. Under the sim, dispatch
  context becomes a sim concern; modules that need UI marshalling do it on the
  view-subscribe side. Smaug's `Marshaled` becomes a per-view configuration.
- **Replay determinism is already protected at the high-water level**: Legolas,
  Saruman discovery, and Samwise ingestion all persist a per-character
  `*HighWaterSequence` so a Mithril restart doesn't re-inflate monotonic
  counters. The world-sim's clock-based determinism is orthogonal to and
  complements this — sequence high-water guards against double-apply,
  `IWorldClock` guards against wall-clock-leaks in transition gates.
- **Five of ten modules need no behavioural change** (Pippin, Elrond, Bilbo,
  Silmarillion, Celebrimbor). The "consume views" wrapper is a one-line
  registration change once the view layer exists.
- **No Tier-3 consumer exists in-repo** (per
  [`cross-source-correlation.md`](cross-source-correlation.md:241-246)). The
  mechanism (`LogEnvelope<T>.IsReplay` post-#554) is available but unused —
  good news for the migration, one less in-flight pattern to relocate.

---

## Recommendations

### Safe to ship first

1. **Wall-clock transition-gate audit + mechanical `IWorldClock.Now` swap**
   (after the clock exists). 9 sites across 4 components. Low coupling, no
   structural change. Could ship as a parallel-prep PR even before the sim
   itself exists, given an `IWorldClock` interface that today shims to
   `TimeProvider`.
2. **Drop `AreaCalibrationService.OnAreaEntered` chat path** (#4 above). Single
   verb, redundant with `PlayerAreaTracker.Changed`. Cheapest of the chat-retirement
   verbs; can ship ahead of the rest.
3. **`ServerCatalogParser` + `ConnectionEventParser` greenfield adds**
   (#9, #10). No existing code to migrate. Add `Server` field to `GameSession`.
   Doesn't depend on any other migration.

### Hidden dependencies

1. **`IInventoryService` split unblocks Arwen, Samwise, Palantir, Legolas
   chat-side `ItemAddedToInventory` migration, Saruman split (architectural
   blueprint), and `MotherlodeMeasurementCoordinator`'s same-source migration.**
   Six downstream consumers wait on this one. It's also the most architecturally
   intricate split because of the `_seededStackSizes` FileSystemWatcher
   reconcile path (`InventoryService.cs:541-671`).
2. **Saruman split is structurally different** from Inventory (key-only join,
   no TTL) — implementing `IInventoryView` first risks baking
   TTL/correlator-shape into the view abstraction. The Saruman split should
   inform the abstraction, not consume it.
3. **Legolas chat retirement (#5) depends on**: (a) `ProcessScreenText(ImportantInfo,
   …)` parser landing for collect + motherlode distance; (b)
   `ProcessMapFx` trailing-arg parser for survey detection; (c) Inventory
   split for `ItemAddedToInventory` correlation. Three Player.log parser adds
   before the retirement is mechanical.

### Design clarifications needed before implementation

1. **View-layer clock semantics** — design notebook open question §5. Two sims
   each have their own `IWorldClock`; an `InventoryView` joining both needs a
   third clock. Concrete choice (max-of-both, view-owned, or per-view derived)
   needs to land before `IInventoryView` is implemented.
2. **Live-mode clock interpolation formula** — design notebook open question §4.
   Affects whether TTL-gated consumers (Inventory 5s, AlarmService snooze,
   Gandalf cooldowns) read identical values in live vs replay. A test corpus
   should be cut early.
3. **Snapshot/replay scope of `_seededStackSizes` and FileSystemWatcher**
   inputs — the design notebook's "filesystem reconcile = synthetic-frame
   producer" gesture (design notebook §11; `module-signal-map.md` "Storage-export
   as synthetic-frame producer") is the right shape but the existing
   InventoryService reconcile is currently a side-channel write, not a frame.
   Migrating it cleanly needs a concrete frame payload spec for storage exports.
4. **What replaces `IChatLogStream` for the `ChatWorldSim`** — does it get its
   own L1-style classified pipe, or does `ChatWorldSim` consume `RawLogLine`
   envelopes directly? Today only `IInventoryService` (Player.log + chat
   `RawLogLine`) and `SarumanChatIngestionService` (chat `RawLogLine`) use this
   surface, both via L1. The chat sim's source-stream shape is the only
   ambiguity in the design notebook's "Source streams" layer.

### Recommendation

**Design clarification needed first** on view-layer clock semantics (1) and
chat-sim source-stream shape (4). Both block writing the `IInventoryView`
abstraction, which is the highest-risk first migration. The Server/Connection
parsers (greenfield) and the wall-clock gate-swap pass are safe to ship in
parallel without that clarification — both are mechanical and don't bake in
view-layer abstractions.
