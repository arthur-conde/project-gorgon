**Title (suggested):** `GameState: live player-effects tracker (Mithril.GameState.Effects)`

---

## Summary

Add a GameState service that owns the live set of effects currently active on the local player, parsed from `Player.log`. No GameState service owns this domain today, despite four prospective consumers (Pippin Gourmand, Vampirism sun-damage, Saruman Words-of-Power consumption side, future buff trackers) all needing it.

Surfaced by the [state-holder gap audit](https://github.com/moumantai-gg/mithril/blob/main/docs/gamestate-services-gap-audit.md) ([#588](https://github.com/moumantai-gg/mithril/pull/588)) as the adjacent dependency for the Pippin Gourmand lift (audit finding #2): live food-eaten detection needs `ProcessAddEffects` parsing alongside `IInventoryService.Deleted`. The audit explicitly recommends shipping the effects service **before** the Gourmand state lift.

Per the [GameState-owns-the-emulated-game principle](https://github.com/moumantai-gg/mithril/blob/main/docs/module-charters.md#cross-cutting-ownership-confirmed) ([#587](https://github.com/moumantai-gg/mithril/pull/587)), and the three-channel service-design rule ([#584](https://github.com/moumantai-gg/mithril/pull/584)). Anchored at HEAD `eb5bd7c5110b72432ff7cd9d10bd889813813dd7`.

## Scope note — naming

Namespace is **`Mithril.GameState.Effects`** (file path `src/Mithril.GameState/Effects/`). Mirrors `Mithril.GameState.Weather`, `…/Pins`, `…/Movement`, etc. Service name **`IPlayerEffectsStateService`** follows the canonical `I<Domain>StateService` convention for new GameState services — combines the `…State` data-shape suffix (per `IPlayerSkillState`, `IPlayerRecipeState`) with the `…Service` DI-registration suffix (per `IInventoryService`). Existing services predate this convergence and keep their historical names; new services land under the unified form.

**Player-only for v1.** PG emits `ProcessAddEffects`/`ProcessRemoveEffects` for arbitrary characters (other players, NPCs), but the v1 service filters to the local-player char-id. NPC-side effects are out of scope and would be a separate service if ever needed.

## Live grammar (captured)

Three verbs cover the live effect lifecycle (samples from `mithril-logs/sessions/2026-05/Player-2026-05-20-0002.log`):

```text
[20:01:17] LocalPlayer: ProcessAddEffects(25042203, 0, "[26015, 39006008, 53122008, 39532004, ...]", False)
[20:01:19] LocalPlayer: ProcessAddEffects(25042203, 25042203, "[13303, ]", True)
[20:08:06] LocalPlayer: ProcessAddEffects(25042203, 25026486, "[15361, ]", True)
[20:08:17] LocalPlayer: ProcessRemoveEffects(25042203, [259278,])
[21:39:35] LocalPlayer: ProcessAddEffects(25098977, 25098977, "[302, ]", True)
[21:39:35] LocalPlayer: ProcessUpdateEffectName(25098977, 259320, "Performance Appreciation, Level 0")
[21:39:49] LocalPlayer: ProcessRemoveEffects(25098977, [259320,])
```

**`ProcessAddEffects(<targetCharId>, <sourceCharId>, "[<effectCatalogId1>, <effectCatalogId2>, ...]", <bool>)`** — adds effects to a character.
- `<targetCharId>` — character receiving the effects.
- `<sourceCharId>` — character that applied them; `0` for system / login-replay; equal to `<targetCharId>` for self-applied.
- The bracketed list contains **catalog ids** (small ints from `effects.json`, e.g. `302` = `Performance Appreciation` base, `13303` = `Metal Armor Suit Bonus`).
- `<bool>` is the **fresh-vs-re-emit discriminator**, loosely paralleling `ProcessAddItem` arg3 (see [Inventory mutations §The trailing `bool`](https://github.com/moumantai-gg/mithril/wiki/Player-Log-Signals#the-trailing-bool-arg3--fresh-allocation-vs-re-emission)):
  - `True` = active application — covers genuine live applications (user actions) AND passive equipment-bonus re-applies fired by the gear-equip system at zone-load. Same shape; the service cannot distinguish them from the log alone.
  - `False` = login / zone-replay snapshot of *persistent* effects (food buffs, status conditions). `<sourceCharId>` is `0`. **The `False` list is NOT a complete snapshot of currently-active effects** — equipment-derived effects re-emit immediately afterwards as separate `True` adds (e.g. `13303 Metal Armor Suit Bonus` at `20:01:19` after the `False` burst at `20:01:17`).

**`ProcessRemoveEffects(<targetCharId>, [<effectInstanceId1>, <effectInstanceId2>, ...])`** — removes effects by **instance id** (large ints, e.g. `259278`). The instance-id space is distinct from the catalog-id space. The bracketed list is unquoted (not a string).

**`ProcessUpdateEffectName(<targetCharId>, <effectInstanceId>, "<displayName>")`** — assigns a runtime-generated display name to a specific effect instance. Typically follows the same-timestamp `ProcessAddEffects` for effects that encode dynamic state in their label (`"Performance Appreciation, Level 0"`). NOT emitted for every Add — only for effects PG decides to name dynamically.

Sample density across 4 captures: 252 `ProcessAddEffects`, 478 combined `ProcessRemoveEffects`/`ProcessUpdateEffectName`. Grammar promotion to the [`Player-Log-Signals` wiki](https://github.com/moumantai-gg/mithril/wiki/Player-Log-Signals) belongs in the same change.

## Service surface (proposed)

```csharp
public interface IPlayerEffectsStateService
{
    // Query — synchronous current-state lookup keyed by effect-catalog id.
    bool TryGet(int effectCatalogId, out EffectState state);

    // React — event stream; default replay synthesizes Added events for the
    // current set (mirrors IInventoryService.Subscribe).
    IDisposable Subscribe(Action<EffectEvent> handler);

    // Bind — current set, keyed by catalog id (single-instance per id).
    // Mirrors #552's IReadOnlyDictionary<NpcKey, NpcStateSnapshot> Npcs shape:
    // the dict is the snapshot surface; the React channel above is what
    // notifies consumers of mutations.
    IReadOnlyDictionary<int, EffectState> ActiveEffects { get; }
}

public readonly record struct EffectState(
    int CatalogId,                // small int from effects.json, the Add-side identifier.
    long? InstanceId,             // present when ProcessUpdateEffectName or ProcessRemoveEffects has tied this entry to an instance id; null otherwise.
    string? DisplayName,          // last value from ProcessUpdateEffectName, if any.
    long SourceCharId,            // applier (0 = system / login-replay).
    DateTimeOffset AppliedAt);

public readonly record struct EffectEvent(
    EffectEventKind Kind,
    EffectState State,
    DateTimeOffset Timestamp);

public enum EffectEventKind { Added, Removed, DisplayNameChanged }
```

## Behaviour

- Subscribes to the L1 driver's `LocalPlayerLogLine` pipe (`ReplayMode.FromSessionStart`), the same shape `InventoryService` uses (`InventoryService.cs:228-247`).
- **`ProcessAddEffects` (`arg4 == True`):** for each catalog id, add an `EffectState` and fire `Added`. **Single-instance per catalog id** — if an entry for the same id already exists, treat as idempotent timestamp-refresh (update `AppliedAt`, no event fires); same shape as `InventoryService`'s add-reemit handling at [`InventoryService.cs:320-326`](https://github.com/moumantai-gg/mithril/blob/main/src/Mithril.GameState/Inventory/InventoryService.cs#L320-L326).
- **`ProcessAddEffects` (`arg4 == False`):** additive re-emit of persistent effects from the previous session. For each catalog id, add an `EffectState` (with `SourceCharId == 0`) if no entry exists for that id; fire `Added`. **Does not drop entries absent from the list** — the `False` list is incomplete by design (equipment-derived effects re-arrive via subsequent `True` adds, see grammar). Same conservative shape `ProcessAddItem` `arg3 == False` gets: re-emit-only, never authoritative for current-set membership.
- **`ProcessUpdateEffectName`:** ties the named `<effectInstanceId>` to the entry that was most recently added and still lacks an `InstanceId`. Updates `DisplayName` + `InstanceId` and fires `DisplayNameChanged`. Captures show Adds and Updates interleave 1:1 by emission order (e.g. `[302]` Add → Update `259328` → `[303]` Add → Update `259329` at the same instant), so under single-instance semantics this is unambiguous. If no candidate exists, drop with a Trace diagnostic — best-effort, no synthetic Add.
- **`ProcessRemoveEffects`:** for each instance id in the list, find the matching entry by `InstanceId` and fire `Removed`. Entries that were never named (the catalog-id-only majority) cannot be removed by id; relies on the next snapshot replay to reconcile, same way `InventoryService` retains deleted entries for late lookup.
- **Scope.** Subscribed via `LocalPlayerLogLine` (the L1 typed pipe used by `InventoryService`), which is already prefix-scoped to local-player `Player.log` lines. No service-side char-id filtering required — `<targetCharId>` in the args is informational only (its main use is `sourceCharId == targetCharId` as the "self-applied" tell on `True` adds).
- **Diagnostics:** Info on subscription start + on each snapshot apply (count delta); Trace on each Add/Remove/Update; Warn on un-correlated Update or Remove.
- In-memory only; replay populates from `IPlayerLogStream`'s backlog drain on subscribe ([#511](https://github.com/moumantai-gg/mithril/issues/511) replay-drain contract).

## Consumers

- **Pippin Gourmand (audit finding #2, downstream issue):** consumes `Added` for catalog ids classified as food-effects via `effects.json` lookup, fused with `IInventoryService.Deleted` on the same timestamp to detect "the player ate item X." Snapshot report continues to feed the historical baseline.
- **Vampirism sun-damage:** the [`IPlayerWeatherTracker` doc-comment](https://github.com/moumantai-gg/mithril/blob/main/src/Mithril.GameState/Weather/IPlayerWeatherTracker.cs#L1-L20) names this consumer. It reads weather + the player's current Vampire-family effects to decide whether sun damage is happening; the effects service is the second input.
- **Saruman Words-of-Power (consumption side):** today's `SarumanChatIngestionService` parses the chat-side "spoken aloud" message; effect appearance on the player is the game-state truth that an effect actually landed. A future migration would consume `Added` for WoP effect catalog ids in place of the chat parser.
- **Future buff trackers:** anticipated, not load-bearing for v1 scope — any module that wants to know "is effect X active right now."

## Out of scope

- **Effect classification / source attribution** (gear vs consumable vs ability). Consumers do this via `effects.json` lookups.
- **Effect duration / expiration math.** v1 surfaces whatever PG emits; no timer-based expiry.
- **Reference-data cross-referencing** (`effects.json` metadata). Consumers fetch via `IReferenceDataService`.
- **Cross-session persistence.** Session-fresh per the GameState session-fresh discipline.
- **Other characters' effects** (party members, NPCs). Not in `LocalPlayer:` lines by construction — would be a separate pipe/service if ever needed.
- **Pippin's Gourmand migration.** Tracked separately as audit finding #2 follow-up; this issue ships the service only.

## Acceptance

- [ ] `src/Mithril.GameState/Effects/` exists with `IPlayerEffectsStateService`, `EffectState`, `EffectEvent`, `EffectEventKind`, and a `BackgroundService` implementation registered in `Mithril.Shared/DependencyInjection/ServiceCollectionExtensions.cs`.
- [ ] Three-channel API per #584: `TryGet(int catalogId, …)` (Query), `Subscribe` (React, replay-then-live atomic per `IInventoryService.Subscribe` precedent), `ActiveEffects` (Bind, `IReadOnlyDictionary<int, EffectState>` keyed by catalog id; matches #552 shape).
- [ ] Tests cover: (a) live `Add` (`arg4 == True`) → `Added` event; (b) re-emit `Add` (`arg4 == False`) adds missing catalog ids without dropping entries absent from the list (zone-load equipment-bonus shape); (c) re-apply of an already-present catalog id is idempotent (timestamp refresh, no duplicate `Added`); (d) `UpdateEffectName` correlates to the most recent un-named entry; (e) `RemoveEffects` by instance id fires `Removed`; (f) late `Subscribe` receives current set as `Added` replay events atomically before live dispatch.
- [ ] Wiki: new `## Effects` section in `Player-Log-Signals.md` documenting the three verbs (this issue's *Live grammar* section, expanded to wiki-page format alongside Inventory mutations).
- [ ] Diagnostics emit per the lifecycle points above.

## References

- Audit: [`docs/gamestate-services-gap-audit.md`](https://github.com/moumantai-gg/mithril/blob/main/docs/gamestate-services-gap-audit.md) ([#588](https://github.com/moumantai-gg/mithril/pull/588)) — findings #2 (Gourmand) names this as the adjacent dependency.
- Principle: [`docs/module-charters.md#cross-cutting-ownership-confirmed`](https://github.com/moumantai-gg/mithril/blob/main/docs/module-charters.md#cross-cutting-ownership-confirmed) ([#587](https://github.com/moumantai-gg/mithril/pull/587)).
- Three-channel rule: [#584](https://github.com/moumantai-gg/mithril/pull/584).
- Consumption-side rule: [#578](https://github.com/moumantai-gg/mithril/pull/578).
- Closest in-code service precedent: [`IInventoryService.cs`](https://github.com/moumantai-gg/mithril/blob/main/src/Mithril.GameState/Inventory/IInventoryService.cs) + [`InventoryService.cs`](https://github.com/moumantai-gg/mithril/blob/main/src/Mithril.GameState/Inventory/InventoryService.cs).
- Closest issue-shape precedent: [#552 (`INpcStateTracker`)](https://github.com/moumantai-gg/mithril/issues/552).
- Vampirism consumer framing: [`IPlayerWeatherTracker.cs`](https://github.com/moumantai-gg/mithril/blob/main/src/Mithril.GameState/Weather/IPlayerWeatherTracker.cs) + [wiki Weather section](https://github.com/moumantai-gg/mithril/wiki/Player-Log-Signals#weather-vampirism-sun-damage).
- Replay-drain contract: [#511](https://github.com/moumantai-gg/mithril/issues/511).
- L1 driver subscribe shape: [`InventoryService.cs:228-247`](https://github.com/moumantai-gg/mithril/blob/main/src/Mithril.GameState/Inventory/InventoryService.cs#L228-L247).

## Known unknowns

- **Single-instance-per-catalog-id assumption.** Captures don't show two concurrent entries of the same catalog id on the player at the same time, but the game's actual constraint is unconfirmed. v1 designs for single-instance (re-apply = timestamp refresh, no duplicate Added); if duplicates are ever observed, the store grows to `Dictionary<int, List<EffectState>>` and `TryGet` returns the most recent. Cheap to revisit.
- **Catalog-id ↔ instance-id bridge.** `ProcessAddEffects` lists catalog ids; `ProcessRemoveEffects` keys by instance id; `ProcessUpdateEffectName` is the only verb that exposes both — and it fires only for effects PG dynamically names. For the catalog-id-only majority, we cannot correlate a `Remove` back to a specific `Add` without best-effort timing heuristics. v1 accepts this gap: such effects "disappear" from `ActiveEffects` on the next snapshot rather than on the live `Remove`.
- **`sourceCharId == 0` semantics.** Observed only on `arg4 == False` snapshot bursts. Hypothesis: "system / no applier on replay." Live applications always carry a non-zero applier in captures. Worth corroborating with a larger capture before treating `0` as a stable sentinel.
- **Effect expiration.** No `ProcessExpireEffects` verb observed. Timed effects must expire silently; whether PG re-emits a snapshot after expiration or simply omits expired entries on the next zone-replay is uncaptured.
- **Bool arg4 on `ProcessAddEffects`.** The active-vs-replay framing parallels `ProcessAddItem` arg3 and matches every captured sample, but the `True` bucket conflates genuine user-action applications with passive equipment-bonus re-applies fired at zone-load. Consumers that need to distinguish (e.g. Pippin Gourmand) must rely on a co-signal (`IInventoryService.Deleted` on the same timestamp) rather than the bool alone.
- **`ProcessUpdateEffectName` ordering.** The correlation heuristic ("most recently added un-named entry at the same char-id") assumes Update follows Add within the same instant. Verified in every captured pair but not formally guaranteed by PG.

— drafted by Claude (Opus 4.7), posted by @arthur-conde
