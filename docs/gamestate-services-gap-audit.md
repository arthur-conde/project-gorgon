# GameState-services gap audit — post-#587 state-holder lens

> **Snapshot.** This audit walks module-side state holders (the `*.Module/State`,
> `Domain`, and `Services` directories) and classifies each against the
> [GameState-owns-the-emulated-game principle](module-charters.md#cross-cutting-ownership-confirmed).
> Complementary to the prior **consumer-side** audit ([#579](https://github.com/moumantai-gg/mithril/issues/579)),
> which walked module-side **log-subscription / regex** patterns.
> Anchored at HEAD `6f47e88594ac6289f3a87a8e720c6f15691b375e` (branch
> `docs/charter-gamestate-emulated-game-principle`, the PR that landed the
> strategic principle).
>
> **Scope.** Read-only investigation; no code changes, no issues filed. The
> follow-up section lists candidate issues for owner consideration.
>
> **The lens.** For each non-trivial state-holding class: *is this a model of
> what the game IS (→ GameState), or what the module is DOING (→ in-charter)?*

## Classification taxonomy

| Class | Meaning | Disposition |
|---|---|---|
| **A. State-rebuild anti-pattern** | Module maintains a model of the emulated game that should live in GameState — either parallel-emulating an existing service OR a domain that needs a new one. | Gap candidate; lift to GameState. |
| **B. Temporal-anchor / correlation SM** | Module uses cross-cutting verbs to detect signatures or correlate events for its own derived signals. Detection could move to GameState as Tier-2 signature work. | Deferred Tier-2 candidate. |
| **C. Module-specific UX state** | What the module is *doing* (filter text, selection, calibration observations, advice computations) — not a model of the emulated game. | In-charter; leave alone. |
| **D. Already consuming the right service** | State derived from a GameState service's `Subscribe(...)`. | In-charter; leave alone. |
| **E. Unclear** | Doesn't cleanly fit A/B/C/D. | Needs owner call. |

## Summary

24 findings across 10 modules: **3 Class A** (state-rebuild candidates proposing
new or extended GameState services), **2 Class B** (both prior-classified — no
net-new correlation SMs from this audit), **13 Class C** (in-charter
module-specific state), **5 Class D** (already consuming the right service),
**1 Class E** (unclear / design call). The two open gap questions the
investigation flagged a priori (Saruman's WoP set, Pippin's Gourmand set)
resolve differently: WoP-discoveries fit the
inventory/skills/recipes/celestial GameState mould cleanly (Class A); the
Gourmand set's shape is the same but its source is a snapshot report rather
than an event stream, and the consumer set is closed — Class E pending an
owner call on whether the principle covers report-shaped per-character
mutable state.

The most concrete new Class A finding is **Smaug's
`VendorSellContext.EntityToNpc` map** — a 4000-entry entityId→NpcKey rolling
cache that is *exactly* what `INpcStateTracker`
([#552](https://github.com/moumantai-gg/mithril/issues/552), in flight) is
being built to own. The prior audit's
[#580](https://github.com/moumantai-gg/mithril/issues/580) covered
`CivicPrideLevel`; this finding is its uncovered sibling under the same #552
umbrella.

The biggest cluster the audit also surfaces — not as a fresh finding, but as
confirmation of what the charter's three-channel rule
([#584](https://github.com/moumantai-gg/mithril/pull/584)) already documents
— is Palantir's and Legolas's ViewModels manually building
`ObservableCollection`s from GameState `Subscribe(...)` streams. The charter
already flags these as Bind-channel candidates; recorded here for
completeness but deferred to the existing architectural commitment.

## Findings table

| # | Module / file:line | State holder | What it models | Class | Recommended disposition |
|---|---|---|---|---|---|
| 1 | [Saruman/Settings/SarumanState.cs:17](../src/Saruman.Module/Settings/SarumanState.cs) | `Codebook: Dictionary<string, KnownWord>` (per-character) | Player's set of discovered Words of Power + their lifecycle state (Known / Spent, count, timestamps) | **A** | Propose `IPlayerWordOfPowerState` in `Mithril.GameState/WordsOfPower/`. Parallels `IPlayerRecipeState`/`IPlayerSkillState` — same shape (per-character unlocked-set parsed from `Player.log`). Single-consumer today, so this is anticipatory; not load-bearing yet. |
| 2 | [Pippin/Domain/GourmandState.cs:47](../src/Pippin.Module/Domain/GourmandState.cs) | `EatenFoodsByInternalName: Dictionary<string, int>` (per-character) | Player's set of foods eaten (the Gourmand-skill input domain) | **E** | Same shape as #1, but the canonical source is an in-game *snapshot report* (dumped to `Player.log`), not an event-stream signal. Owner call needed: does the GameState principle extend to report-shaped per-character mutable state, or is "live event-stream over `Player.log`" load-bearing in the definition? If extension → `IPlayerGourmandState`. If not → leave as Class C. |
| 3 | [Smaug/State/VendorSellContext.cs:23](../src/Smaug.Module/State/VendorSellContext.cs) | `EntityToNpc: Dictionary<int, string>` (4000-entry capped) | entityId → NpcKey mapping for resolving vendor screens | **A** | Lift into `INpcStateTracker` ([#552](https://github.com/moumantai-gg/mithril/issues/552), in flight). The #552 spec explicitly names "entityId↔NpcKey binding" as in-scope — this is the second uncovered Smaug state holder under that umbrella (the first, `CivicPrideLevel`, is already filed as [#580](https://github.com/moumantai-gg/mithril/issues/580)). |
| 4 | [Smaug/State/VendorSellContext.cs:11](../src/Smaug.Module/State/VendorSellContext.cs) | `ActiveVendorEntityId / ActiveFavorTier / ActiveNpcKey` | Transient "which vendor screen am I in" context for sell attribution | **B** | Tier-2 signature candidate. Mirrors the Arwen/Gandalf prior-audit deferrals — defer to the #552 Tier-2 work. |
| 5 | [Smaug/State/VendorCatalogService.cs:38](../src/Smaug.Module/State/VendorCatalogService.cs) | `_entries: IReadOnlyList<VendorCatalogEntry>` | Joined projection of CDN data × `IFavorLookupService` × Civic Pride | **C** | Pure projection; consumes services correctly. Module-specific catalog view. |
| 6 | [Smaug/State/SellPlannerService.cs:50](../src/Smaug.Module/State/SellPlannerService.cs) / [StorageSellbackService.cs:49](../src/Smaug.Module/State/StorageSellbackService.cs) | `_ownedItems`, `_vendors` | Storage-export × NPC reference projections | **C** | In-charter; consumes `IActiveCharacterService`/`IReferenceDataService`. |
| 7 | [Smaug/Domain/PriceCalibrationService.cs:48](../src/Smaug.Module/Domain/PriceCalibrationService.cs) | `_data: PriceCalibrationData` (observations + rate aggregates) | Player's vendor-price *calibration* | **C** | Smaug calibration domain (per charter — Smaug owns "sale min/maxing", "mining store states"). Module-specific. |
| 8 | [Arwen/Domain/ArwenFavorState.cs:19](../src/Arwen.Module/Domain/ArwenFavorState.cs) | `Favor: Dictionary<string, NpcFavorSnapshot>` (per-character) | Per-NPC exact favor from `Player.log` (consumed via `IFavorLookupService` by **Smaug too**) | A (prior-classified, deferred) | Already in flight: [#582](https://github.com/moumantai-gg/mithril/issues/582) covers Arwen's correlation SM; #552's `INpcStateTracker` is the natural home. Cross-module consumption (Smaug reads it via `IFavorLookupService`) is exactly the "shared service" signal the principle predicts. No new ask — confirms prior classification. |
| 9 | [Arwen/State/FavorStateService.cs:47](../src/Arwen.Module/State/FavorStateService.cs) | `_tierByNpcKey: Dictionary<string, FavorTier>` | Joined view of CDN NPC × character export × persisted exact favor | **D** | Already consumes `IReferenceDataService.FileUpdated`, `IActiveCharacterService.CharacterExportsChanged`, `PerCharacterView`. (Will become a thin adapter once #552 absorbs the favor map.) |
| 10 | [Arwen/Domain/GiftIndex.cs:29](../src/Arwen.Module/Domain/GiftIndex.cs) | `_npcGifts`, `_items`, `_allMatchesByNpcItem` | Pre-computed CDN-derived index: NPC preferences → matching items | **C** | Pure reference-data projection. Module-specific (rebuilds on `FileUpdated`). |
| 11 | [Arwen/Domain/CalibrationService.cs:52](../src/Arwen.Module/Domain/CalibrationService.cs) | `_pending: TtlObservableCollection<PendingGiftObservation>` | TTL-aged pending gift confirmations awaiting user quantity input | **C** | Module-specific UX state. Consumes `IInventoryService.TryResolve`/`TryGetStackSize` correctly (Class D in its log consumption). |
| 12 | [Arwen/Domain/CalibrationService.cs:74](../src/Arwen.Module/Domain/CalibrationService.cs) | `_activeNpcKey`, `_pendingDeletedItem`, `_pendingDelta` | Correlation SM for `DeleteItem + DeltaFavor` gift attribution | B (prior-classified) | Prior audit: deferred to Tier-2 signature work atop #552. No change. |
| 13 | [Legolas/Services/MotherlodeMeasurementCoordinator.cs:133](../src/Legolas.Module/Services/MotherlodeMeasurementCoordinator.cs) | `_session: MotherlodeSession`, `_dugMaps`, `_open`, `_undo`, `_latestFix`, `_sessionArea` | In-flight surveying activity state (positions, fixes, slot solves) | **D** | Already consumes `IPlayerPositionTracker.Subscribe`, `IPlayerPinTracker.Subscribe`, `IInventoryService.Subscribe`, `PlayerAreaTracker`. Canonical Tier-2 reference per [docs/cross-source-correlation.md](cross-source-correlation.md). |
| 14 | [Legolas/Services/PinCalibrationCoordinator.cs:110](../src/Legolas.Module/Services/PinCalibrationCoordinator.cs) | `_pairs: List<(MapPin, PixelPoint)>`, `_skipped`, `ExistingPins` | Calibration session (clicks paired to map pins) | **D** | Consumes `IPlayerPinTracker.Subscribe` correctly. Module-specific calibration state — Class C in nature, Class D in dependency. |
| 15 | [Legolas/Domain/LegolasIngestionState.cs:46](../src/Legolas.Module/Domain/LegolasIngestionState.cs) | `PlayerLogHighWaterSequence` (per-character) | Per-character L1 driver dedup high-water | **C** | Module-side ingestion bookkeeping; legitimate per-#550 capability F shape. |
| 16 | [Legolas/Services/LogIngestionService.cs:89](../src/Legolas.Module/Services/LogIngestionService.cs) | `_pendingAdds: PendingCorrelator<string, int>` | Module-specific chat `[Status] X added` ↔ `[Status] X collected!` pairing for survey-collect attribution | **C** | Tier-1 correlator (per [docs/cross-source-correlation.md](cross-source-correlation.md)); operates on survey-specific chat signal pairs, not cross-cutting verbs. |
| 17 | [Legolas/Services/AreaCalibrationService.cs](../src/Legolas.Module/Services/AreaCalibrationService.cs) (via `LegolasSettings.AreaCalibrations`) | Per-area `AreaCalibration` map | The user's solved coordinate-projector calibrations per area | **C** | Module-specific calibration state per charter (Legolas owns "the map overlay"). |
| 18 | [Gandalf/Services/LootSource.cs](../src/Gandalf.Module/Services/LootSource.cs) (`_cache.LearnedChests` / `_cache.LearnedDefeats`) | Dictionaries of chest/defeat entities the player has interacted with + cooldown timestamps | The temporal-bookkeeping ledger Gandalf charter explicitly owns ("time: when re-attemptable") | **C** | In-charter (Gandalf owns "time"). Already consumes `PlayerAreaTracker` for area attribution. The bracket-tracker correlation piece is separately covered by [#586](https://github.com/moumantai-gg/mithril/issues/586) (prior-classified Class B). |
| 19 | [Gandalf/Services/UserTimerSource.cs:19](../src/Gandalf.Module/Services/UserTimerSource.cs) | `_catalog`, `_progressMap` | User-curated timers (definitions × progress) | **C** | Per charter — Gandalf owns user-defined timers/alarms. |
| 20 | [Bilbo/ViewModels/StorageViewModel.cs:22](../src/Bilbo.Module/ViewModels/StorageViewModel.cs) | `_allItems: IReadOnlyList<StorageItemRow>`, `CraftableRecipes` | Projections of `IActiveCharacterService.ActiveStorageContents` | **D** | In-charter: Bilbo is the *surface* over the static storage export per charter. Consumes the shared service correctly. |
| 21 | [Celebrimbor/Services/OnHandInventoryQuery.cs:24](../src/Celebrimbor.Module/Services/OnHandInventoryQuery.cs) / [Domain/CraftListEntry.cs](../src/Celebrimbor.Module/Domain/CraftListEntry.cs) | Storage projection + craft-list line items + `LevelingPlanStore.Plans` | Plan-shaped state (what the user has asked the planner to craft / level) | **C** | Per charter — Celebrimbor receives a target list and consumes it; no game-world state held. Plans are persisted via `JsonSettingsStore`. |
| 22 | [Saruman/Settings/SarumanState.cs:46](../src/Saruman.Module/Settings/SarumanState.cs) | `DiscoveryHighWaterSequence` (per-character) | Per-character L1 driver dedup high-water for the WoP discovery subscription | **C** | Ingestion bookkeeping — same shape as Legolas/Samwise high-waters. Distinct from #1 (which is the *content* of the WoP set). |
| 23 | [Samwise/State/GardenStateMachine.cs:33](../src/Samwise.Module/State/GardenStateMachine.cs) | `_plotsByChar: Dictionary<char, Dictionary<plotId, Plot>>` | Per-character garden plot lifecycle state | **C** | In-charter — Samwise is *the* authority on "what is planted and where each plot is in its lifecycle" (charter, ✅ owner-confirmed). |
| 24 | [Palantir/ViewModels/WorldStateViewModel.cs:80](../src/Palantir.Module/ViewModels/WorldStateViewModel.cs) + `LiveInventoryViewModel` (similar) | `ObservableCollection<MapPinRow> Pins`, etc. | Per-VM observable shells built from `IPlayerPinTracker.Subscribe` / `IPlayerPositionTracker.Subscribe` / `IPlayerCelestialState.Subscribe` / `IPlayerWeatherTracker.Subscribe` event streams | **D** (with charter-noted Bind-channel debt) | Already consumes the right services. The "manually building observable state from a React stream" pattern is exactly what the three-channel rule ([#584](https://github.com/moumantai-gg/mithril/pull/584), charter) calls out as the Bind-channel gap — pending service-side `IReadOnlyObservableCollection` additions. No retire-the-pattern issue needed here; the charter already owns the architectural commitment. |

## Per-finding narrative

### Finding 1 — Saruman's WoP codebook (`SarumanState.Codebook`)

`SarumanState.Codebook` is a `Dictionary<string, KnownWord>` per character
([SarumanState.cs:17](../src/Saruman.Module/Settings/SarumanState.cs))
holding every discovered Word of Power and its lifecycle state (`Known` /
`Spent`, discovery count, first/last timestamps). It is populated by
`SarumanCodebookService.RecordDiscovery` from L1 `LocalPlayerLogLine` events
([SarumanDiscoveryIngestionService.cs:91](../src/Saruman.Module/Services/SarumanDiscoveryIngestionService.cs))
and mutated to `Spent` by chat-line events
([SarumanChatIngestionService.cs:68](../src/Saruman.Module/Services/SarumanChatIngestionService.cs)).

**Why Class A:** the shape is identical to `IPlayerRecipeState`
([#475](https://github.com/moumantai-gg/mithril/pull/475)), `IPlayerSkillState`
([#465](https://github.com/moumantai-gg/mithril/pull/465)), and
`IInventoryService` — a per-character monotonically-built set of game-mechanic
identifiers the player has unlocked, derived from `Player.log`. The charter's
principle says *"GameState owns the emulated game world; modules project
subsets for UX."* Whether the player has discovered a particular Word of
Power is part of the emulated game world, parallel to whether they know
recipe `recipe_99123`.

**Recommended disposition:** propose `IPlayerWordOfPowerState`
(collection-shaped, `Subscribe<WordOfPowerDiscovered/Consumed>` React channel
+ `IReadOnlyObservableCollection<KnownWord>` Bind channel + `IsTracked(code)`
Query). Saruman becomes the UX projection: WoP browser, manual edits,
settings.

**Caveat:** today there's exactly one consumer of this state (Saruman
itself), so this lift buys mostly *anticipation* and the charter-implied
"what if a second consumer arrives" benefit. Distinguish from the
Recipe/Skill/Inventory cases where a second consumer (Elrond, Celebrimbor,
etc.) was already implicit.

### Finding 2 — Pippin's eaten-foods set (`GourmandState.EatenFoodsByInternalName`)

Per-character `Dictionary<string, int>` of `InternalName → count` for foods
the player has eaten
([GourmandState.cs:47](../src/Pippin.Module/Domain/GourmandState.cs)).
Populated exclusively from the in-game *Foods Consumed* report (`HandleReport`
snapshot-replaces the dict,
[GourmandStateMachine.cs:113](../src/Pippin.Module/State/GourmandStateMachine.cs)),
which is parsed from `Player.log`. Charter is explicit: Pippin owns "the
per-character set of foods already eaten."

**Why Class E (rather than A or C):** the *data shape* is identical to #1 —
per-character set of game-mechanic identifiers parsed from `Player.log`. But
two attributes distinguish it from the canonical GameState services:

1. **Source channel is a one-shot snapshot report**, not an event stream.
   `IPlayerSkillState` etc. consume live deltas; Gourmand consumes a "here's
   the whole set" report whose individual events (per-food-eaten) are not in
   the log. The React-channel shape would be "report-replaced" semantics
   rather than "event-stream replay."
2. **Closed consumer set.** Unlike inventory (Samwise/Arwen/Legolas/Palantir
   consume), eaten-foods has one consumer by charter (Pippin), and the
   charter's *food provenance* extension
   ([#348](https://github.com/moumantai-gg/mithril/issues/348)) is still
   inside Pippin.

The owner call is whether the GameState principle ("modules project; services
emulate") extends to report-shaped state, or whether the principle's
load-bearing context is *event-stream* state specifically. The charter's
three-channel rule is event-stream-flavoured (React = "event stream with
FromSessionStart"), but a snapshot-replace source still fits Query (current
set) + Bind (observable collection) cleanly.

### Finding 3 — Smaug's `VendorSellContext.EntityToNpc`

A 4000-entry capped `Dictionary<int, string>` keyed by entityId → NpcKey,
populated from `ProcessStartInteraction` log lines
([VendorSellContext.cs:23](../src/Smaug.Module/State/VendorSellContext.cs)).
Maintained module-side because Smaug needs entityId resolution to attribute
vendor sells to the correct NPC.

**Why Class A:** the
[#552](https://github.com/moumantai-gg/mithril/issues/552) NPC service spec
explicitly names "entityId↔NpcKey binding" as in-flight scope. Smaug's
rolling entityId cache *is* a parallel rebuild of exactly that binding — its
cap-and-trim eviction logic exists because the module doesn't have access to
the canonical lifetime semantics the NPC tracker will own.

**Recommended disposition:** retire when #552 lands. Smaug's call site
(`VendorIngestionService` `NpcInteractionStarted` handler at
[VendorIngestionService.cs:123](../src/Smaug.Module/State/VendorIngestionService.cs)
→ `_context.RememberEntity`) becomes a no-op; `_context.OnVendorScreenOpened`
switches from `EntityToNpc.TryGetValue` to an
`INpcStateTracker.TryResolveNpcKey(entityId)` query.

This is the prior audit's missing sibling:
[#580](https://github.com/moumantai-gg/mithril/issues/580) covered
`CivicPrideLevel`; this one covers the entityId map. Both ride the same
#552 retirement vehicle.

### Findings 5–7, 10, 17–19, 21, 23 — In-charter module state (Class C)

These are state holders that look like "the module is maintaining state" but
are actually **what the module is *doing***, not models of the emulated game:

- **Smaug's price calibration** (`PriceCalibrationData`, `_observationKeys`):
  the player's *vendor-pricing-rate observations* are a calibration domain,
  not a game-world fact. Charter: Smaug owns "sale min/maxing."
- **Smaug's storage/sell-back projections** (`StorageSellbackService._vendors`,
  `SellPlannerService._ownedItems`): pure projections of
  `IActiveCharacterService.ActiveStorageContents × IReferenceDataService.Npcs`.
- **Smaug's vendor catalog** (`VendorCatalogService._entries`): same — joined
  CDN × favor view.
- **Arwen's gift index** (`GiftIndex._npcGifts`): CDN-derived precomputed
  index from reference data; rebuilds on `FileUpdated`.
- **Legolas's pin calibration** (`PinCalibrationCoordinator._pairs`): a
  *calibration session* — what the user is clicking right now.
- **Legolas's area calibrations** (`LegolasSettings.AreaCalibrations`):
  solved `(WorldCoord ↔ PixelPoint)` per area — projection math state, not
  game state.
- **Gandalf's `LearnedChests`/`LearnedDefeats`**: charter ✅ — Gandalf "owns
  time." These are the temporal-bookkeeping ledger; the *entities* discovered
  ride alongside the cooldowns they anchor.
- **Gandalf's user-defined timers** (`UserTimerSource._catalog`): charter ✅
  — user-curated.
- **Celebrimbor's craft plans** (`LevelingPlanStore.Plans`, `CraftListEntry`):
  plan-shaped; the module ingests a target list per charter.
- **Samwise's plots** (`_plotsByChar`): charter ✅ — Samwise is *the*
  authority on what is planted; this is the canonical owned state.

Each of these is the module *doing* (calibrating, projecting, planning,
scheduling), not *emulating the game world*. Leave alone.

### Findings 9, 11, 13–16, 20, 24 — Already consuming the right service (Class D)

The audit's main yield in this bucket is that **most modules that consume
GameState services do so correctly**. Specifically:

- **Arwen's `FavorStateService`** consumes
  `IReferenceDataService`/`IActiveCharacterService`/`PerCharacterView`.
- **Arwen's `CalibrationService`** consumes `IInventoryService.TryResolve` +
  `TryGetStackSize` + `IGameSessionService`.
- **Legolas's `MotherlodeMeasurementCoordinator`** is the canonical Tier-2
  reference — consumes `IPlayerPositionTracker`, `IPlayerPinTracker`,
  `IInventoryService` all via `Subscribe`.
- **Legolas's `PinCalibrationCoordinator`** consumes
  `IPlayerPinTracker.Subscribe`.
- **Bilbo's `StorageViewModel`** consumes `IActiveCharacterService`.
- **Celebrimbor's `OnHandInventoryQuery`** consumes
  `IActiveCharacterService`.
- **Palantir's `WorldStateViewModel`** consumes `IPlayerPositionTracker`,
  `IPlayerPinTracker`, `IPlayerCelestialState`, `IPlayerWeatherTracker` (and
  `LiveInventoryViewModel` similarly consumes `IInventoryService`).

The Palantir/Legolas-VM "manually-building-observable-state" pattern is real
but is explicitly already named in the charter's three-channel section as the
*service-side* Bind-channel gap, not a module-side anti-pattern. No
follow-up needed from this audit.

### Finding 22 — Saruman discovery high-water (`DiscoveryHighWaterSequence`)

`SarumanState.DiscoveryHighWaterSequence` is per-character L1 dedup
bookkeeping, paralleling Legolas's `PlayerLogHighWaterSequence` and Samwise's
`GardenCharacterState.HighWaterSequence`. Not game state — module ingestion
plumbing. Stays where it is.

## Suggested follow-up issues

For owner consideration; *not filed by this audit*.

- **Class A follow-up #1**: *File issue — GameState `IPlayerWordOfPowerState`
  tracker (lift from Saruman).* Three-channel design per #584. **Caveat:**
  single-consumer today, so anticipatory. Owner call on timing.
- **Class A follow-up #2 (Pippin)**: *Owner-level discussion — does the
  "GameState owns the emulated game" principle cover report-shaped
  per-character state, or is event-stream semantics load-bearing?* The
  Gourmand-state shape resolves to A or C entirely on this point.
- **Class A follow-up #3**: *Extend
  [#552](https://github.com/moumantai-gg/mithril/issues/552)'s
  `INpcStateTracker` spec to cover Smaug's `EntityToNpc` rolling map.* The
  entityId↔NpcKey binding is already named in-scope; this finding is the
  second module-side cache (after
  [#580](https://github.com/moumantai-gg/mithril/issues/580)'s
  `CivicPrideLevel`) that retires when #552 lands. May not need a new issue
  — folding the call-site retirement into #552's existing PR may suffice.
- **Class B (deferred)**: `VendorSellContext` correlation pieces (#4) —
  defer to the Tier-2 signature umbrella that will be filed under #552. No
  new issue from this audit.

## Related work

- [Module charters](module-charters.md) — the principle this audit applies.
- [#511](https://github.com/moumantai-gg/mithril/issues/511) — layered log
  pipeline (L0/L0.5/L1/L2), the origin of the GameState services.
- [#578](https://github.com/moumantai-gg/mithril/pull/578) — consumption-side
  rule (modules `Subscribe` to services, not raw logs).
- [#584](https://github.com/moumantai-gg/mithril/pull/584) — three-channel
  service-design rule (Query / React / Bind).
- [#587](https://github.com/moumantai-gg/mithril/pull/587) — strategic
  principle articulation ("GameState owns the emulated game").
- [#579](https://github.com/moumantai-gg/mithril/issues/579) — the prior
  **consumer-side** audit (regex / log-subscription lens, complementary to
  this state-holder lens).
- [#552](https://github.com/moumantai-gg/mithril/issues/552) — in-flight
  `INpcStateTracker`, the natural home for findings #3, #4, and #8.
- [#580](https://github.com/moumantai-gg/mithril/issues/580),
  [#582](https://github.com/moumantai-gg/mithril/issues/582),
  [#586](https://github.com/moumantai-gg/mithril/issues/586) — prior-audit
  migration issues referenced as "prior-classified."
