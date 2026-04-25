# Plan: Replay-on-subscribe for `IInventoryService`

Tracking issue: [#7](https://github.com/arthur-conde/project-gorgon/issues/7)

## Goal

Eliminate the late-subscriber race that causes Samwise to render planted crops as "Unknown". Make `IInventoryService` deliver its full known history to a handler at attach time, atomically with going live — matching the snapshot-under-the-gate-then-live pattern that `PlayerLogStream` already uses (commit c636158).

## Root cause being fixed

`IInventoryService.ItemAdded` is a non-replaying `event`. `GardenIngestionService` attaches it after `_stateService.LoadAllAsync(...)` (slow disk I/O), by which time `InventoryService` has already fired `ItemAdded` for every item in the session-replay flush. Samwise's `_itemIdToCrop` map ends up empty for those instance ids, so when the user later plants a seed and `HandleItemIdentified` looks up the seed's crop, it finds nothing and silently returns. `Plot.CropType` stays null → UI shows "Unknown".

The 5b25301 refactor (Samwise sourcing AddItem/DeleteItem from `IInventoryService`) intended to fix this race but only moved it: `PlayerLogStream`'s session-replay buffer is correct, but the events on top of it aren't replay-aware.

## Design choice: `Subscribe` method, not `event`

C# `event` semantics can't atomically "fire-for-existing-then-attach". Replace the two events with a single `Subscribe` method that returns `IDisposable`:

```csharp
public interface IInventoryService
{
    bool TryResolve(long instanceId, out string internalName);

    /// <summary>
    /// Attach a handler that receives every item currently in the map (as
    /// synthesized <see cref="InventoryEventKind.Added"/> events) followed by
    /// every live add/delete. Replay and live-attach are atomic — no event is
    /// lost, duplicated, or reordered relative to the canonical map.
    /// </summary>
    IDisposable Subscribe(Action<InventoryEvent> handler);
}

public readonly record struct InventoryEvent(
    InventoryEventKind Kind,
    long InstanceId,
    string InternalName,
    DateTime Timestamp);

public enum InventoryEventKind { Added, Deleted }
```

A single discriminated payload simplifies consumers: one `switch`, one subscription, one disposable.

## Atomicity

Inside `InventoryService`:

```csharp
private readonly object _subLock = new();
private readonly Dictionary<long, MapEntry> _map = new();
private readonly List<Action<InventoryEvent>> _handlers = new();

private readonly record struct MapEntry(string InternalName, DateTime Timestamp, bool Deleted);

public IDisposable Subscribe(Action<InventoryEvent> handler)
{
    lock (_subLock)
    {
        // Replay current map state to this handler only. Skip entries that
        // have been marked Deleted so the handler never sees a stale Added
        // for an item that has already been removed.
        foreach (var (id, entry) in _map)
        {
            if (entry.Deleted) continue;
            handler(new InventoryEvent(InventoryEventKind.Added, id, entry.InternalName, entry.Timestamp));
        }
        _handlers.Add(handler);
        return new Subscription(this, handler);
    }
}

private void Fire(InventoryEvent evt)
{
    Action<InventoryEvent>[] snapshot;
    lock (_subLock) { snapshot = _handlers.ToArray(); }
    foreach (var h in snapshot)
    {
        try { h(evt); }
        catch (Exception ex) { _diag?.Warn("Inventory", $"Subscriber threw: {ex.Message}"); }
    }
}
```

The ingestion loop must update `_map` and call `Fire` **inside `_subLock`** so a concurrent `Subscribe` either sees the entry in its snapshot or receives it via `Fire` — never both, never neither:

```csharp
// Inside the await foreach loop
lock (_subLock)
{
    _map[addId] = new MapEntry(name, raw.Timestamp, Deleted: false);
    Fire(new InventoryEvent(InventoryEventKind.Added, addId, name, raw.Timestamp));
}
```

This is the only correctness-critical part. The lock is held only while updating the map and dispatching to handlers; subscribers are expected to bounce off-thread (Samwise already does via `Application.Current.Dispatcher.InvokeAsync`).

`MapEntry` adds a `Timestamp` so replay carries the source line's timestamp, not a synthetic one — preserves the in-game timeline for consumers like Samwise's 500 ms `PlantCropResolveWindow`.

## Map-entry retention

Keep the existing "retain on delete" behaviour: `TryResolve` callers (e.g. Arwen's gift flow) still want to look up names after a delete fires. The replay path must NOT replay deleted items as `Added` — only items currently live in the map.

Solution: mark entries `Deleted = true` instead of removing the key.

- `TryResolve` returns the name regardless of `Deleted` (preserves Arwen's behavior).
- `Subscribe`'s replay skips entries where `Deleted == true`.
- Future re-Add of the same id overwrites the entry with `Deleted: false` and a fresh timestamp.

## Concrete steps

### 1. Update the interface and event shape

[src/Mithril.Shared/Inventory/IInventoryService.cs](../../src/Mithril.Shared/Inventory/IInventoryService.cs)

- Drop `event EventHandler<InventoryItem>? ItemAdded` and `ItemDeleted`.
- Add `IDisposable Subscribe(Action<InventoryEvent> handler)`.
- Add `InventoryEvent` record struct + `InventoryEventKind` enum.
- Delete `InventoryItem` if no remaining references after migration.

### 2. Rework `InventoryService`

[src/Mithril.Shared/Inventory/InventoryService.cs](../../src/Mithril.Shared/Inventory/InventoryService.cs)

- Replace events with `_handlers` list + `_subLock`.
- Change `_map` value type from `string` to `MapEntry { InternalName, Timestamp, Deleted }`. Switch from `ConcurrentDictionary` to plain `Dictionary` since access is now serialized by `_subLock`.
- Implement `Subscribe` per design above.
- Implement private `Subscription : IDisposable` that removes from `_handlers` under the lock. Idempotent dispose.
- In `ExecuteAsync`, replace `ItemAdded?.Invoke` / `ItemDeleted?.Invoke` with `Fire(...)` calls, all inside `_subLock` together with the `_map` mutation.
- For `ProcessDeleteItem`, set `entry = entry with { Deleted = true }` (don't remove the key — keeps `TryResolve` semantics).

### 3. Migrate Samwise

[src/Samwise.Module/State/GardenIngestionService.cs](../../src/Samwise.Module/State/GardenIngestionService.cs)

- Replace the `_inventory.ItemAdded += onAdd` / `ItemDeleted += onDelete` pair with a single `_inventory.Subscribe(OnInventory)` call.
- Hold the returned `IDisposable` in a local; dispose in the `finally` (replaces the existing `-=` pair).
- `OnInventory(InventoryEvent e)` switches on `e.Kind` and dispatches `AddItem` / `DeleteItem` via the same `Dispatch(...)` helper.
- Replay events arrive on the calling thread (the gate-opened `ExecuteAsync` thread) BEFORE `Subscribe` returns, so they're guaranteed to dispatch onto the UI thread before any subsequent live `SetPetOwner` from `_stream.SubscribeAsync`.

### 4. Migrate Arwen — _not needed_

Verified: `CalibrationService` uses only `_inventory.TryResolve(...)`, never subscribes to `ItemAdded` / `ItemDeleted`. The `ItemDeleted` event Arwen consumes comes from its own `FavorLogParser`, not from `IInventoryService`. `TryResolve` is already replay-safe (the map is populated eagerly by `InventoryService`'s own ungated `_stream.SubscribeAsync` consumption), so Arwen's gift attribution path is unaffected by this race and needs no change.

### 5. Tests

**`InventoryServiceTests`** (new or extended)

- Subscribe-after-add: feed N AddItem lines through a fake `IPlayerLogStream`, then `Subscribe`. Assert handler receives N replayed `Added` events with correct timestamps.
- Subscribe-after-delete: feed AddItem then DeleteItem, then Subscribe. Assert handler receives **nothing** for that id (not replayed as `Added`).
- Atomicity stress: subscribe in a tight loop while the ingestion loop is firing live events; assert no event is delivered twice and no live event is missed (use a fake stream that yields on demand).
- `TryResolve` after delete still returns true (regression guard for Arwen).
- Subscriber exception doesn't propagate or stop other subscribers from receiving.
- Disposing a subscription removes it from future `Fire` calls; double-dispose is a no-op.

**`TwoBarleyRegressionTest`** ([tests/Samwise.Tests](../../tests/Samwise.Tests/TwoBarleyRegressionTest.cs))

- Update the `Feed` helper to drive `Subscribe` on the real `InventoryService` instead of synthesizing `AddItem`/`DeleteItem` directly into the state machine. This is the end-to-end regression that should now pass even when `Subscribe` happens after the AddItem.
- Add a variant: subscribe AFTER the seed AddItem has been ingested, then plant. Asserts the seed resolves to the correct crop. This is the direct repro for issue #7.

**`GardenIngestionService` integration test (new)**

- Boot the service with `LoadAllAsync` returning a delayed task (simulating slow disk). Inject seed AddItem lines via the fake stream BEFORE the load completes. Plant after the gate fully opens. Assert the resulting plot has `CropType` set, not null.

### 6. Verification before merging

- `dotnet test Mithril.slnx` — full suite green, including the new replay tests.
- Smoke run: launch Mithril against a real Player.log session, plant a seed, confirm the card shows the crop name. Compare diag log to ensure `Samwise.Plant resolved plot=…` info lines fire (they were absent in the broken session).
- Confirm Arwen gift attribution still works for a multi-NPC scenario.

## Risks

- **Lock granularity**: holding `_subLock` across `Fire` means a slow synchronous subscriber blocks the ingestion loop. Samwise and Arwen both bounce off-thread, so this is fine in practice — but document the contract clearly in the interface XML doc.
- **Subscription ordering**: replay happens on the *subscribing* thread, not the ingestion thread. If a subscriber assumes all callbacks come on the ingestion thread, the migration breaks them. Both current consumers immediately dispatch to the UI thread, so neither cares — call this out in the doc.
- **Migration churn**: every existing `+= ItemAdded` / `+= ItemDeleted` site (currently 2: Samwise, Arwen) has to change at the same time. Single PR, both modules updated together.

## Ship order

One PR, in this order:

1. New types + `InventoryService` rework + new `InventoryServiceTests`.
2. Samwise migration + `TwoBarleyRegressionTest` update + new ingestion-service test.
3. Arwen migration + verification.
4. Drop the old `event`s and `InventoryItem` if no remaining references.

## Out of scope, but adjacent

`SerilogDiagnosticsSink` hits its default 1 GB file-size cap and silently stops writing — we lost ~1 h of the affected session's logs while diagnosing this. Worth a separate issue to set `rollOnFileSizeLimit: true` and a sane `fileSizeLimitBytes`. Not part of this plan.
