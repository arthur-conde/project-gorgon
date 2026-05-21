# World-sim migration audit

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

## Executive summary

- **15 components audited** across `Mithril.GameState`, ten modules, and
  `Mithril.Shared` (state-bearing leaves only). **5 components need behavioural
  changes** (split / migrate / restructure); **3 are sleeper blockers**.
- **Five of ten modules are pure projectors** (Pippin, Elrond, Bilbo,
  Silmarillion, Celebrimbor) — confirmed no log subscriptions, no FSM state.
  No migration owed.
- **Highest-risk migrations, ranked:**
  1. **`IInventoryService` split** — five different state surfaces feed it
     (Player.log, chat, FileSystemWatcher, reference data, `_seededStackSizes`
     reconcile). Every other migration sits downstream of this one.
  2. **`SarumanCodebookService` split + view** — different shape than Inventory
     (no temporal pairing, key-join only) so the view-layer abstraction needs
     to admit two distinct view patterns.
  3. **`Legolas.LogIngestionService` full retirement (per #531)** — the only
     remaining direct `IChatLogStream` consumer in-repo. Five chat verbs to
     migrate to Player.log equivalents; `MotherlodeMeasurementCoordinator`
     becomes same-source as a side-effect.
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

### `IInventoryService` ⚠️ **CROSS-SOURCE**

`src/Mithril.GameState/Inventory/InventoryService.cs:57-813`.

- **Source spanning**: **YES.** Two parallel L1 subscriptions:
  - `Subscribe<LocalPlayerLogLine>` at `:250` (Player.log: `ProcessAddItem`,
    `ProcessDeleteItem`, `ProcessUpdateItemCode`, `ProcessRemoveFromStorageVault`).
  - `Subscribe<RawLogLine>` at `:261` (chat: `[Status] X xN added`).
  - Plus `FileSystemWatcher` at `:681-708` for character-export reconcile.
- **Cross-source correlator**: `PendingCorrelator<string, int> _pendingChat` +
  `PendingCorrelator<string, long> _pendingAdd` at `:135-136`, both 5s
  `PendingChatTtl = TimeSpan.FromSeconds(5)` at `:77`.
- **Wall-clock at `:610, :648`**: `_time.GetUtcNow().UtcDateTime` — both
  **stamps** for reconcile events (not transition gates). The PendingCorrelator's
  TTL itself reads via `TimeProvider` injected at `:182-183` — that one IS a
  transition gate.
- **Migration**: matches the design notebook's worked example 1 exactly. Split:
  - **Player.log half** → `IPlayerInventoryService` (instance-id ledger,
    `_map: Dictionary<long, MapEntry>`, no quantities).
  - **Chat half** → `IChatInventoryStateMachine` (name-keyed observations).
  - **View** → `IInventoryView` houses the `PendingCorrelator`, the
    FileSystemWatcher reconcile, `_seededStackSizes`, and the existing
    `TryResolve` / `TryGetStackSize` / `Subscribe` API surface.
  - 5s TTL reads from `IViewClock` instead of `_time`.
- **Blocker**: `_seededStackSizes` reconcile pass at `:541-671` consumes
  `IReferenceDataService` + `StorageReport` JSON. It's not chat — it's a third
  side-input to the view. The view-layer migration owns this.
- **Status**: **migration owed**. Highest-risk first migration; everything else
  downstream of it.

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
  `IInventoryService.Subscribe` for `Added`/`Deleted` reshape. Once Inventory
  splits, Samwise just consumes `IInventoryView`.
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

### Arwen (NPC favor) ⚠️ **CROSS-FSM PEEK (CROSS-SOURCE)**

`src/Arwen.Module/Domain/CalibrationService.cs:114-265`.

- **Source spanning**: single (LocalPlayer via `FavorIngestionService`) at the
  log level — but synchronously reads `IInventoryService.TryResolve` at `:186`
  and `IInventoryService.TryGetStackSize` at `:247`.
- **Cross-source crossing**: under today's `IInventoryService`, those reads
  cross both Player.log AND chat (the chat half back-fills stack sizes). After
  the Inventory split, Arwen reads only `IPlayerInventoryService.TryResolve` —
  same-sim, declared-dependency-coherent.
- **Wall-clock**: `_time.GetUtcNow()` at `:167`, `:181`, `:204` — all
  **test-only fallback overloads** for callers that don't plumb a real timestamp.
  Production calls go through `OnStartInteraction(npcKey, DateTimeOffset)` etc.
  Not gating.
- **Migration**: trivial post-Inventory-split. The peek becomes coherent under
  the Player.log sim's dispatch order.
- **Status**: **gated on Inventory split**.

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

`src/Legolas.Module/Services/{LogIngestionService.cs, PlayerLogIngestionService.cs,
MotherlodeMeasurementCoordinator.cs, AreaCalibrationService.cs}`.

- **Source spanning**: YES today. **Only remaining direct `IChatLogStream`
  consumer in the codebase** (confirmed via Grep — see "Migration-plan
  spot-checks §5 below"). `LogIngestionService.cs:21` holds `IChatLogStream
  _stream`; `:61` does `await foreach (var raw in _stream.SubscribeAsync(...))`.
- **Five chat verbs in `LogIngestionService.Dispatch` at `:91-122`**:
  - `SurveyDetected` → `HandleSurveyDetected` (calls
    `_areaCalibration.NoteSurvey`)
  - `ItemAddedToInventory` → `HandleItemAddedToInventory` (enqueue in
    `_pendingAdds`)
  - `ItemCollected` → `HandleItemCollected` (Tier-1 correlator dequeue)
  - `MotherlodeDistance` → `_motherlode.OnDistance(...)` (Tier-2)
  - `AreaEntered` → `_areaCalibration.OnAreaEntered(...)`
- **Cross-FSM peeks**: `PlayerLogIngestionService` synchronously reads
  `PlayerAreaTracker.CurrentArea`, `AreaCalibrationService.CurrentCalibration`,
  `SurveyFlowController.CurrentState` mid-handler (per module-signal-map.md
  `:395`). Sim-coherent under declared dispatch order.
- **Wall-clock**: `SurveyFlowController.cs` has `_time.GetUtcNow()` — needs
  read to determine if gates or stamps; `LegolasReportService.cs` also has it.
  Not on the critical state path; mostly UI / share-card stamping.
- **Migration**: per [#531 comment](https://github.com/moumantai-gg/mithril/issues/531#issuecomment-4499029851)
  cited in design notebook item 5 — every chat verb has a same-source Player.log
  equivalent:
  - `ItemAddedToInventory` → `IInventoryView.Added` (post-split)
  - `ItemCollected` → `ProcessScreenText(ImportantInfo, "<Mineral> collected!…")`
  - `MotherlodeDistance` → `ProcessScreenText(ImportantInfo, "The treasure is N meters from here.")`
  - `SurveyDetected` → `ProcessMapFx` trailing arg (or drop per #454)
  - `AreaEntered` → already redundant with `PlayerAreaTracker.Changed`
- **Status**: **full chat-tail retirement owed**. After retirement, Legolas is
  Player.log-sim-resident; `LogIngestionService.cs` deletes; `ChatLogParser.cs`
  deletes; `IChatLogStream` ctor arg deletes; `MotherlodeMeasurementCoordinator`
  becomes same-source.

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
No FSM, no log subscription. Reactor only. Migrates with Inventory automatically.

---

## Migration-plan spot-checks

### 1. `IInventoryService` split — **CONFIRMED**

Cross-source confirmed at `InventoryService.cs:250` (Player.log) + `:261`
(chat). Five-input service (Player.log, chat, FileSystemWatcher,
reference data, `_seededStackSizes` reconcile). Split is the highest-risk
first migration; everything downstream waits on it.

### 2. `SarumanCodebookService` split — **CONFIRMED**

Cross-source via two ingestion services:
`SarumanDiscoveryIngestionService.cs:62-74` + `SarumanChatIngestionService.cs:42-101`.
Both mutate `SarumanCodebookService` (`:64 RecordDiscovery`, `:120 MarkSpent`).
Different shape than Inventory: key-only join, no TTL. View needs different
abstraction.

### 3. `MotherlodeMeasurementCoordinator` migration — **CONFIRMED currently chat**

`MotherlodeMeasurementCoordinator.cs:376` exposes `OnDistance(int metres,
DateTimeOffset at)`. Sole call site is `LogIngestionService.cs:116` —
`MotherlodeDistance md` case in the chat dispatcher. The use side
(`OnUse` via `ProcessDoDelayLoop`) is on `PlayerLogIngestionService.cs`
(Player.log). Becomes same-source once the chat tail retires and
`ProcessScreenText(ImportantInfo, "The treasure is N meters from here.")` is
read from Player.log instead.

### 4. `AreaCalibrationService` chat redundancy — **CONFIRMED redundant**

`AreaCalibrationService.OnAreaEntered` (`AreaCalibrationService.cs:131-137`)
called from chat path: `LogIngestionService.cs:119`. `AreaCalibrationService.SelectArea`
called from Player.log path: `PlayerLogIngestionService.cs:383`. Same SetArea
call inside (line `:148`). The chat call adds no information beyond what
`PlayerAreaTracker.Changed` (which feeds the Player.log call) provides. **Drop
the chat path**.

### 5. Legolas full chat retirement — **CONFIRMED, 5 verbs**

`LogIngestionService.cs:61` is the only `IChatLogStream.SubscribeAsync` call in
non-driver code (confirmed by Grep across `src/`). Five verbs in `Dispatch` at
`:91-122`: `SurveyDetected`, `ItemAddedToInventory`, `ItemCollected`,
`MotherlodeDistance`, `AreaEntered`. Each has a same-source Player.log
equivalent per the linked #531 comment. After retirement: `LogIngestionService.cs`,
`ChatLogParser.cs`, and the `IChatLogStream` ctor arg all delete.

### 6. `QuestService.OnViewCurrentChanged` synthesis — **CONFIRMED**

`QuestService.cs:73` wires `_view.CurrentChanged += OnViewCurrentChanged` →
`:262 OnViewCurrentChanged` → `:276 ReloadFromView` → diff against `Current`,
synthesize `Accepted` / `Abandoned` / `Completed` events stamped at `:283 var
nowStamp = _time.GetUtcNow().UtcDateTime`. Under per-character sim scope this
whole path collapses — each character has its own quest journal handler,
character-switch is a UI binding swap not a state mutation. The log-derived
`HandleJournalLoaded` Abandoned synthesis at `:178-209` is unrelated and
**stays** (real inference from `ProcessLoadQuests`).

### 7. Arwen's `_inventory.TryResolve` peek — **CONFIRMED**

`Arwen.Module/Domain/CalibrationService.cs:186` (`TryResolve`) + `:247`
(`TryGetStackSize`). Both inside `OnItemDeleted` / `RecordObservation`. Becomes
sim-coherent after Inventory split — reads from the Player.log half within the
same Player.log sim's dispatch order.

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
- `LootSource.cs:199, :575` — observation stamps.
- `DerivedTimerProgressService.cs:104` — `DismissedAt` stamp.
- `DashboardAggregator.cs:54, :86, :117` — UI display projections.
- `LegolasReportService.cs` — share-card report stamping.
- `PerfTracerHostedService.cs`, `BindingErrorTraceListener.cs`,
  `DiagnosticsSink.cs` — infrastructure observation timestamps (outside sim
  scope).

**Module signal map claim that "wall-clock-gated transitions live in five
places"** is a slight undercount — counting consumer call sites (not primitive
sites), Gandalf alone has 5 gates (`TimerProgressService.CheckExpirations`,
`TimerExpirationScheduler.Reschedule`, `ShiftAlarmService.Reschedule`,
`LootSource.cs:258`, `QuestSource.cs:119`), plus Samwise (2), Inventory (1),
AlarmService (1) = 9 transition-gate sites across 4 components. Same migration
mechanism, different count.

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
