# Gandalf timer model redesign — eliminate 1 Hz refresh storm + coarse change events

**Tracked in:** [#154](https://github.com/arthur-conde/project-gorgon/issues/154)
**Sibling issue:** [#153](https://github.com/arthur-conde/project-gorgon/issues/153) (chest-cache write-only — separate localized fix)

## Context

A user opened the Gandalf → Loot tab while otherwise idle and reported lag. Tracing through the source/VM stack surfaced two structural model flaws:

1. **Time isn't first-class in the model.** [`TimerRow`](../src/Gandalf.Module/Domain/TimerRow.cs)'s `Remaining` / `Fraction` / `State` project `(StartedAt, Duration, now)`, but the model has no notion of *"this row's display next changes at T."* So every consumer ticks every row every second whether anything visible would change or not. With ~50 rows × 11 properties × 1 Hz, the WPF binding pipeline + `ICollectionView.Refresh()` (with grouping + 3-key sort) burns measurable UI-thread time on rows that haven't moved.

2. **Source change events are coarse and snapshot-shaped.** [`ITimerSource`](../src/Gandalf.Module/Domain/ITimerSource.cs)'s `CatalogChanged` and `ProgressChanged` are bare `EventHandler` (no payload), and `Catalog`/`Progress` are exposed as wholesale snapshots. Consumers either clear-and-rebuild their `ObservableCollection` ([`LootTimersViewModel.Sync`](../src/Gandalf.Module/ViewModels/LootTimersViewModel.cs), [`TimerListViewModel.SyncFromState`](../src/Gandalf.Module/ViewModels/TimerListViewModel.cs)) or poll on a `DispatcherTimer`. The CollectionView with grouping/sorting then thrashes. [`DashboardAggregator`](../src/Gandalf.Module/Services/DashboardAggregator.cs) is the worst offender — full cross-source rebuild on any event *plus* a 1 Hz `Recompute()` to surface state flips the coarse model can't.

**Intended outcome:**
- Idle catalog rows generate zero per-tick work.
- A row whose visible display next changes at 14:32:00 isn't touched until 14:32:00.
- Source mutations propagate as per-key deltas; consumers update only the affected `TimerItemViewModel` and only call `TimersView.Refresh()` when filter/sort/group keys actually changed.
- One bundled PR; ~7 internal commits per the implementation order below; bisectable.
- All four Gandalf timer surfaces (Loot, Quest, User-defined, Dashboard) use the same model.

## Design

### 1. Domain: per-row "next visible change at"

Add to [`TimerRow`](../src/Gandalf.Module/Domain/TimerRow.cs):

```csharp
public DateTimeOffset? NextDisplayChangeAt { get; }
```

Pure projection from `Clock`, `Progress.StartedAt`, `Catalog.Duration`. Semantics:

| Row state | NextDisplayChangeAt |
|---|---|
| `Idle` (no progress, or `DismissedAt` set) | `null` — never changes by time alone |
| `Running` | `min(StartedAt + Duration, NextMinuteBoundaryAfter(Clock.GetUtcNow()))` — soonest of the state-flip moment or the "Xh Ym remaining" minute roll |
| `Done` and `(now - CompletedAt) < 60s` | `CompletedAt + 60s` — when "done!" flips to "done 1m ago" |
| `Done` and `(now - CompletedAt) >= 60s` | `NextMinuteBoundaryAfter(Clock.GetUtcNow())` — when "done Xm ago" rolls |

The `Fraction` progress bar is **not** driven by `NextDisplayChangeAt`. The display scheduler keeps a separate fast-tick (1 Hz) gated on "any visible row is `Running` AND has its progress bar visible," which goes idle when the gate predicate is false.

### 2. Pre-existing bug surfaced by the redesign — fix in this PR

[`TimerItemViewModel.TimeDisplay`](../src/Gandalf.Module/ViewModels/TimerItemViewModel.cs) reads `DateTimeOffset.UtcNow` directly instead of `_row.Clock.GetUtcNow()`. Under tests using `ManualTime : TimeProvider`, the "done X ago" string drifts from the rest of the row. The new scheduler computes its wakeups from the injected clock; if the consumer reads wall-clock, the scheduler fires too early/late under fakes. **Route every time-derived read in `TimerItemViewModel` through `_row.Clock`.**

### 3. Source contract: per-key batched deltas

Replace [`ITimerSource`](../src/Gandalf.Module/Domain/ITimerSource.cs)'s coarse events:

```csharp
public interface ITimerSource
{
    string SourceId { get; }
    IReadOnlyList<TimerCatalogEntry> Catalog { get; }
    IReadOnlyDictionary<string, TimerProgressEntry> Progress { get; }
    bool TryGetProgress(string key, out TimerProgressEntry progress);   // NEW

    event EventHandler<TimerRowsChangedEventArgs>? RowsChanged;          // NEW
    event EventHandler<TimerReadyEventArgs>? TimerReady;                 // unchanged
    // CatalogChanged / ProgressChanged — REMOVED
}

public sealed record TimerRowDelta(
    string Key,
    TimerRowChangeKind Kind,
    TimerCatalogEntry? Catalog,    // null on Removed
    TimerProgressEntry? Progress); // null when row is idle / never started

public enum TimerRowChangeKind { Added, CatalogChanged, ProgressChanged, Removed }

public sealed class TimerRowsChangedEventArgs : EventArgs
{
    public required IReadOnlyList<TimerRowDelta> Deltas { get; init; }
}
```

**Batched.** A calibration-overlay refresh in [`LootSource.OverlayDefeatCalibration`](../src/Gandalf.Module/Services/LootSource.cs) re-projects ~hundreds of rows; that must fire **one** `RowsChanged` with N deltas, not N events.

**No compat shims.** Sources are all in-tree; the four VM consumers + `DashboardAggregator` migrate in the same PR. The old events go away in the final commit. `TimerReady` stays — already per-key, already consumed correctly by [`TimerAlarmService`](../src/Gandalf.Module/Services/TimerAlarmService.cs).

**Per-key event in the underlying services.** [`DerivedTimerProgressService`](../src/Gandalf.Module/Services/DerivedTimerProgressService.cs) and [`TimerProgressService`](../src/Gandalf.Module/Services/TimerProgressService.cs) get a per-key change event so sources project deltas without re-snapshotting.

### 4. New shared infrastructure (two small classes, ~150 lines each)

**`TimerSourceBinder`** — new file `src/Gandalf.Module/Services/TimerSourceBinder.cs`:
- Constructor: `(ITimerSource source, ObservableCollection<TimerItemViewModel> target, Dictionary<string, TimerItemViewModel> byKey, TimeProvider clock, Func<TimerCatalogEntry, TimerProgressEntry?, bool> isRelevant)`
- On construction: enumerates `source.Catalog` once, populates `target` for relevant rows.
- Subscribes to `source.RowsChanged`. For each delta:
  - `Added` → if relevant, materialize VM, add to collection, register with scheduler.
  - `ProgressChanged` → call `vm.UpdateRow(...)`. If relevance flipped (Quest's "no longer in journal" case), remove. **Track GroupKey before/after; if changed, signal `OnGroupKeyChanged`** so the VM can call `TimersView.Refresh()`.
  - `CatalogChanged` → call `vm.UpdateRow(...)`. Same GroupKey-change handling — calibration overlay can flip a defeat's `Region` from `"Defeats"` to `"Tagamogi"`.
  - `Removed` → remove from collection, unregister from scheduler.
- Exposes `event EventHandler? GroupKeyChanged` for the host VM to bind a single `TimersView.Refresh()` call to.
- Generalizes the existing diff-in-place pattern in [`QuestTimersViewModel.Sync`](../src/Gandalf.Module/ViewModels/QuestTimersViewModel.cs) so all three tab VMs share it.

**`TimerDisplayScheduler`** — new file `src/Gandalf.Module/Services/TimerDisplayScheduler.cs`:
- Owns **one** `DispatcherTimer`.
- Per registered VM, tracks `vm.Row.NextDisplayChangeAt`.
- Reschedules `_timer.Interval` to `min(NextDisplayChangeAt) - now` whenever the heap top changes.
- On tick: refreshes only the rows whose `NextDisplayChangeAt <= now`, recomputes their next-change, rebalances heap.
- **Fast tick (separate `DispatcherTimer` at 1 Hz)** runs only while the predicate `_anyRunningWithVisibleProgressBar` is true. Drives `Fraction` updates for visible progress bars. Stops when no Running rows are visible.
- When the heap is empty AND fast-tick gate is false, both timers are stopped — zero per-second work on an all-Idle tab.

### 5. Per-property diff in `TimerItemViewModel.UpdateRow`

Replace the unconditional 11-fire `Refresh()` with per-property comparison against last-known values. Many properties co-vary on `State` change (`IsIdle`, `IsRunning`, `IsDone`, `ShowStartButton`, `ShowRestartButton`, `ShowProgressBar`, `StatusColor`, `StatusLabel`); cache `_lastState` and fire the cluster only when state changed. `TimeDisplay` and `Fraction` get their own fields. Keep `Refresh()` as a "force-fire-all" escape hatch but stop calling it from `UpdateRow`.

### 6. CheckExpirations relocation — preserves user-timer alarms

[`TimerListViewModel.Tick`](../src/Gandalf.Module/ViewModels/TimerListViewModel.cs) calls `_progress.CheckExpirations()` — that's the path that stamps `CompletedAt` and fires `TimerExpired` → `UserTimerSource.TimerReady` → [`TimerAlarmService`](../src/Gandalf.Module/Services/TimerAlarmService.cs). Removing the 1 Hz tick without re-routing kills user-tab alarms.

Move the responsibility into [`TimerProgressService`](../src/Gandalf.Module/Services/TimerProgressService.cs) itself: schedule a one-shot timer for the soonest known expiration; on fire, run `CheckExpirations` and reschedule. Mirrors `NextDisplayChangeAt` at the data layer — same idea, separate concern (it's about firing `TimerReady`, not about visible display).

### 7. DashboardAggregator migration

[`DashboardAggregator`](../src/Gandalf.Module/Services/DashboardAggregator.cs):
- Subscribe to `RowsChanged` from each source instead of two coarse events.
- Maintain summaries incrementally on each delta — no full-list rebuild.
- Add `NextDisplayChangeAt` to `TimerSummary` (which has its own ad-hoc time formatter — same minute-boundary logic, share helper with `TimerRow`).
- Expose `NextDisplayChangeAt { get; }` as the min across summaries, drives [`DashboardViewModel`](../src/Gandalf.Module/ViewModels/DashboardViewModel.cs)'s scheduler subscription.
- **Remove the manual 1 Hz `Recompute()` tick** — `NextDisplayChangeAt`-driven scheduling replaces it.

## Implementation order (one bundled PR, ~7 commits)

| # | Commit | Files |
|---|---|---|
| 1 | Add `NextDisplayChangeAt` to `TimerRow` + `TimerRowDelta` / `TimerRowChangeKind` / `TimerRowsChangedEventArgs` records. Pure unit tests. | `Domain/TimerRow.cs`, `Domain/ITimerSource.cs` (types only) |
| 2 | Add `RowsChanged` + `TryGetProgress` to `ITimerSource`. Implement in `LootSource`, `QuestSource`, `UserTimerSource` by subscribing to underlying services. Keep old `CatalogChanged`/`ProgressChanged` firing for now. | `Services/LootSource.cs`, `Services/QuestSource.cs`, `Services/UserTimerSource.cs`, `Services/DerivedTimerProgressService.cs`, `Services/TimerProgressService.cs` |
| 3 | `TimerSourceBinder` + tests. | New `Services/TimerSourceBinder.cs`, new test file |
| 4 | `TimerDisplayScheduler` + tests. | New `Services/TimerDisplayScheduler.cs`, new test file |
| 5 | Migrate `LootTimersViewModel` → `TimerListViewModel` → `QuestTimersViewModel`. Each removes its `DispatcherTimer`, replaces `Sync` with binder, opts into scheduler. Move `CheckExpirations` into `TimerProgressService`. | All three VMs, `Services/TimerProgressService.cs` |
| 6 | `TimerItemViewModel` per-property diff + clock-routing fix (`TimeDisplay` reads `_row.Clock`). | `ViewModels/TimerItemViewModel.cs` |
| 7 | Migrate `DashboardAggregator` + `DashboardViewModel`. Remove `CatalogChanged`/`ProgressChanged` from `ITimerSource` and all sources. Final cleanup. | `Services/DashboardAggregator.cs`, `ViewModels/DashboardViewModel.cs`, `Domain/ITimerSource.cs`, all three sources |

## Reuse / patterns to lean on

- [`QuestTimersViewModel.Sync`](../src/Gandalf.Module/ViewModels/QuestTimersViewModel.cs) already does diff-in-place on a `_byKey` dictionary against an `ObservableCollection`. `TimerSourceBinder` is this generalized.
- [`QuestTimersViewModel.Tick`](../src/Gandalf.Module/ViewModels/QuestTimersViewModel.cs) already gates `TimersView.Refresh()` on state changes. Generalize via the binder's `GroupKeyChanged` event.
- The existing `QuestTimersViewModelTests.Tick_does_not_refresh_view_when_no_state_changed` test pattern (uses `ManualTime : TimeProvider`) generalizes cleanly to the new scheduler tests.
- Project-wide `TimeProvider` injection is already universal; no new abstraction.

## Tests

**New unit tests:**
- `TimerRowTests`: `NextDisplayChangeAt` correctness for Idle / Running / just-Done (<60s) / old-Done — under `ManualTime`. Include the boundary case where Running's "minute-roll" time is *before* the state-flip time.
- `LootSourceTests` / `QuestSourceTests` / `UserTimerSourceTests`: assert `RowsChanged` fires exactly once per logical mutation, with correct `Kind`. Calibration-overlay test asserts **one** event with N deltas, not N events.
- `TimerSourceBinderTests`: relevance predicate, GroupKey-change-mid-stream (defeat row's Region flips from `"Defeats"` to `"Tagamogi"` — must signal `GroupKeyChanged`), Add/Remove correctness, character-switch full-diff.
- `TimerDisplaySchedulerTests`: heap ordering, re-prioritization when a row's `NextDisplayChangeAt` becomes earlier (e.g., post-Restart), no leaks when a row is removed mid-flight, fast-tick gate predicate flips correctly when last Running row finishes.
- `TimerItemViewModelTests`: per-property diff — assert PropertyChanged fires for the right subset given a row state transition.

**Generalized existing tests:**
- Replicate `Tick_does_not_refresh_view_when_no_state_changed` from `QuestTimersViewModelTests` into `LootTimersViewModelTests` and `TimerListViewModelTests`.
- `DashboardAggregatorTests`: assert delta-driven incremental updates produce the same `Summaries` as the old full-rebuild path on a fixed sequence of mutations.
- `TimerAlarmService` tests: confirm user-timer alarms still fire after `CheckExpirations` moves into `TimerProgressService`.

## End-to-end verification

```bash
dotnet build Mithril.slnx
dotnet test Mithril.slnx
dotnet run --project src/Mithril.Shell
```

Manual UI checks (from a clean run, with a character logged in):

1. **Idle Loot tab.** Open Loot tab. Open Task Manager / VS Profiler. CPU should be ~0% on the Mithril process when no rows are Running. Compared with current behavior (steady ~few-percent burn for all-Idle tab from 1 Hz `Tick`), this is the load-bearing perf assertion.
2. **Running rows present.** Loot a chest (or simulate via tail). Progress bars animate smoothly. No visible jank when other tabs / unrelated sources mutate.
3. **State flip moment.** A chest cooldown crosses ready while the Loot tab is open — the row flips Running → Done at the exact moment, not on the next 1 Hz boundary.
4. **Calibration overlay.** Trigger a calibration refresh. Hundreds of defeat rows mutate; the UI updates without visible jank or duplicate refresh.
5. **Character switch.** Switch active character. All rows from prior character are removed; new character's rows appear. No leaked VMs or scheduler entries.
6. **User-timer alarm.** Create a 5-second user timer; lock the screen. Alarm fires when timer expires (proves `CheckExpirations` relocation works).
7. **Quest tab.** "In-journal" filter still works; quest dismiss-all still works.
8. **Dashboard tab.** Ready-Now and Coming-Up sections update at the correct moments without 1 Hz polling.

## Out of scope (explicit, with rationale)

- **`ListBox` + `WrapPanel` virtualization.** Once the binder + scheduler stop the 1 Hz catalog rebuild, virtualization is a no-op for current row counts. Revisit only if Loot still lags at >200 calibrated bosses post-PR.
- **Migrating `DashboardAggregator` to use `TimerRow` directly** instead of its own `TimerSummary`. Tempting, but grows the PR. Keep `TimerSummary`; just give it `NextDisplayChangeAt`.
- **Replacing `EventHandler<T>` with `IObservable<T>` / channels.** The delta-+-binder pattern *is* lightweight Rx; the dependency cost isn't justified.
- **`Storyboard`-driven `Fraction` animation.** Hours-scale progress bars don't need 60 Hz interpolation; the gated 1 Hz fast-tick is enough.
- **Per-character debouncing in `DerivedTimerProgressService`** (the 500 ms `_debounce`) — unrelated to this redesign; do not touch.
- **Issue [#153](https://github.com/arthur-conde/project-gorgon/issues/153)** (chest-cache write-only) — separate localized cache-schema migration, not a model redesign.
