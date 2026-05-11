# Perf-trace data schema

Reference for the JSON-lines files written by [`IPerfTracer`](../src/Mithril.Shared/Diagnostics/Performance/IPerfTracer.cs) — what each event means, what its properties carry, and how to read a trace.

Shipped in PR [#196](https://github.com/arthur-conde/project-gorgon/pull/196). Tracking issue: [#195](https://github.com/arthur-conde/project-gorgon/issues/195).

## What gets recorded and where

When a perf-trace session is active, each emitted event is written as one JSON object per line (Serilog `CompactJsonFormatter`) to:

```
%LocalAppData%\Mithril\Shell\perf\perf-{yyyyMMdd-HHmmss}.jsonl
```

The directory is created on first use. Older sessions are pruned to the most recent **30** files on each session start. Within a single long session, Serilog rolls at 50 MB to `…_001.jsonl`, `…_002.jsonl`, etc., keeping the last 5.

Off by default; enable via **Settings → Diagnostics** (or set `enablePerfTrace: true` in `%LocalAppData%\Mithril\Shell\shell.json`). Start/stop a session via the hotkey "Toggle perf-trace recording" (developer-only; ships unbound), or set `autoStartPerfTrace: true` to record from shell launch.

## File anatomy

Every line is a CompactJson record from Serilog. The base fields are:

| Field | Meaning |
|---|---|
| `@t` | UTC timestamp, ISO-8601 with sub-millisecond precision |
| `@l` | Serilog level (`Information`, `Warning`, `Verbose`, etc.) |
| `@mt` | Message template — the literal format string the producer used |
| `@m` | Rendered message (present when properties were interpolated) |
| _(per-event)_ | Structured properties, top-level. `Kind` discriminates the event. |

The discriminator is **`Kind`** — one of the constants in [`PerfEventKinds`](../src/Mithril.Shared/Diagnostics/Performance/PerfEventKinds.cs). All filtering should key off `Kind`.

## What's instrumented today

Not every event kind has a producer yet — the schema defines the vocabulary; the codebase decides what gets emitted. Current state (PR [#196](https://github.com/arthur-conde/project-gorgon/pull/196)):

| Kind | Producer | Status |
|---|---|---|
| `session_header` | [`PerfTracer.EmitSessionHeader`](../src/Mithril.Shared/Diagnostics/Performance/PerfTracer.cs) | Always emitted on session start |
| `frame_summary` / `frame` / `stall` | [`PerfTracerHostedService` + `CompositionTarget.Rendering`](../src/Mithril.Shared/Diagnostics/Performance/PerfTracerHostedService.cs) | Attached while session active |
| `dispatcher` | `PerfTracerHostedService` + `Dispatcher.Hooks` | Attached while session active |
| `counter` / `gc` | `PerfTracerHostedService` + `System.Threading.Timer` (1Hz) | Running while session active |
| `binding_error` | [`BindingErrorTraceListener`](../src/Mithril.Shared/Diagnostics/Performance/BindingErrorTraceListener.cs) | Attached while session active |
| `input_latency` | `PerfTracerHostedService` + `InputManager.PreProcessInput` | Attached while session active |
| `module_activated` | [`ShellViewModel.ActivateModule`](../src/Mithril.Shell/ViewModels/ShellViewModel.cs) | Fires on every activation (initial + tab clicks) |
| `ref_fetch` | [`ReferenceDataService.RefreshFileAsync`](../src/Mithril.Shared/Reference/ReferenceDataService.cs) | Fires per CDN fetch attempt; cache-hit path not yet wrapped |
| `scope` | _none_ | **No producers yet.** The `IPerfTracer.Scope(name, tags)` API exists for modules to adopt at suspected hot paths; the deferred candidate list (log-line dispatch, `TtlObservableCollection.Reconcile`, etc.) hasn't been wired. A trace with **zero `scope` events is the expected default**, not a wiring bug. |

If you read a trace and a kind you expected is missing, check the producer column first — `scope` and `cache-hit ref_fetch` are the two known gaps today.

## Event kinds

### `session_header`

First line of every trace file. Identifies the recording machine and build so a trace can be interpreted out-of-context.

| Property | Meaning |
|---|---|
| `Build` | `AssemblyInformationalVersion` of `Mithril.Shell` (e.g. `0.42.1+abcdef0`). |
| `Os` | `RuntimeInformation.OSDescription`. |
| `Gpu` | Adapter description — currently empty; reserved. |
| `RefreshRateHz` | Primary display refresh rate — currently `0`; infer from frame intervals if needed. |
| `Dpi` | Effective DPI of the main window (96 = unscaled). Layout cost scales with this. |
| `ActiveCharacter` / `ActiveServer` | Last-selected character at session start. |
| `Modules` | Array of module ids loaded at session start (e.g. `["samwise","pippin",…]`). |

### `frame_summary`

Per-second aggregate of `CompositionTarget.Rendering` intervals. Emitted ~1×/sec while a UI window is visible.

| Property | Meaning |
|---|---|
| `Count` | Frames observed in the window (~60 on a healthy 60 Hz machine). |
| `MeanMs` | Mean inter-frame interval. |
| `P50Ms` / `P95Ms` | Percentile interval. P95 spiking above ~20 ms while P50 stays low ≈ occasional stutter. |
| `MaxMs` | Worst single frame in the window. |
| `StallCount` | Frames with interval > 33 ms (i.e. visibly dropped at 30 fps). |

### `frame` (with `Stall=true`)

Per-frame event emitted when a single render takes longer than 33 ms. The same shape, but `Stall=true` and `IntervalMs` is the bad sample. Raw `frame` events with `Stall=false` are emitted only when `verboseFrameEvents=true`.

| Property | Meaning |
|---|---|
| `IntervalMs` | Frame interval that triggered the event. |
| `Stall` | `true` for the >33 ms emit path; `false` only with verbose. |
| `CurrentOp` | Reserved for the in-flight dispatcher op label; currently empty string. |

### `dispatcher`

Fires from `Dispatcher.Hooks.OperationCompleted` — one event per UI-thread `DispatcherOperation` that ran while the session was active.

| Property | Meaning |
|---|---|
| `Priority` | `DispatcherPriority` of the op (e.g. `Render`, `DataBind`, `Background`). |
| `WaitMs` | Currently `0` (no public API for queued-at time). |
| `RunMs` | Wall-clock duration of `OperationStarted` → `OperationCompleted`. The number that matters. |
| `QueueDepthAtStart` | In-flight op count when this one began. Sustained depth > 1 means the UI thread is saturated. |

### `counter`

1 Hz process-state snapshot.

| Property | Meaning |
|---|---|
| `WorkingSetMB` | Process working set, MB. Trending up across a session ≈ leak suspect. |
| `Gen0` / `Gen1` / `Gen2` | Cumulative GC collection counts. Deltas tell you per-second collection pressure. |
| `Threads` | Process thread count. |
| `Handles` | OS handle count. Trending up = handle leak (file/window/event). |
| `DispatcherQueueDepth` | Current in-flight UI-thread ops (same counter `dispatcher` uses). |

### `gc`

Fires when `GC.CollectionCount(n)` increments between two `counter` ticks. Best-effort generation attribution — only the highest generation observed in the gap is reported.

| Property | Meaning |
|---|---|
| `Generation` | `0`, `1`, or `2`. Gen2 collections are the ones that cause visible frame stalls (compacting, blocking). |
| `DurationMs` | Currently `0` — the polling approach doesn't have start/stop ticks. |

### `binding_error`

WPF binding error captured via `PresentationTraceSources.DataBindingSource`. Throttled to **1 event per identical message per second** — a misnamed binding firing 60×/sec collapses to ~1/sec.

| Property | Meaning |
|---|---|
| `Message` | The raw `System.Windows.Data Error: nn : …` text. |

Binding errors are a classic silent perf sink — WPF retries failed bindings on every layout pass.

### `input_latency`

Time from the first observed `PreProcessInput` event to the next `CompositionTarget.Rendering` tick. Measures *perceived* lag, not just frame time.

| Property | Meaning |
|---|---|
| `InputKind` | `mouse` or `key`. |
| `LatencyMs` | Input → next-frame delta. Persistently > 50 ms = the UI feels unresponsive. |

Coalesced: multiple input events arriving in the same render frame produce one latency sample. Stylus/touch are ignored for now.

### `scope`

Manual timing scope — emitted on `using var s = perf.Scope("name", new { tag = value }).Dispose()`. The one extension point modules can adopt at suspected hot paths.

| Property | Meaning |
|---|---|
| `Name` | The string passed to `Scope`. Conventionally `module.operation` (e.g. `samwise.parse`). |
| `DurationMs` | Wall-clock from `Scope()` call to dispose. |
| `Tags` | Whatever anonymous object the caller passed (or null). |

> **No producers today.** See [What's instrumented today](#whats-instrumented-today). A trace with zero `scope` events is expected on a stock build.

### `module_activated`

Cost of the first activation of a module — the `_gates.For(...).Open()` plus the view-type DI resolution in `ShellViewModel.ActivateModule`.

| Property | Meaning |
|---|---|
| `ModuleId` | The module's `Id` (e.g. `samwise`). |
| `DurationMs` | Wall-clock for the activation block. Lazy modules pay their cost here on first click. |

### `ref_fetch`

CDN refresh in `ReferenceDataService.RefreshFileAsync`. Captures network/disk time per file.

| Property | Meaning |
|---|---|
| `File` | The reference data file name (`items`, `recipes`, `npcs`, …). |
| `CacheHit` | Always `false` today — only the HTTP fetch path is wrapped. Reserved for future cache-hit instrumentation. |
| `DurationMs` | Wall-clock for the HTTP fetch (or until failure). |
| `Bytes` | Response body size, `0` on failure. |

## Reading a trace

Every example below assumes the file is at `$Path`. On Windows PowerShell:

```powershell
$Path = "$env:LocalAppData\Mithril\Shell\perf\perf-20260511-143301.jsonl"
```

### Common false-negative traps

Before concluding "kind X is missing, the wiring is broken," rule out these analysis-side traps. Most "zero events" findings turn out to be one of them:

- **Capital-K `Kind`.** Serilog's CompactJsonFormatter preserves the case of the template placeholder. The trace JSON has `"Kind":"…"`, `"ModuleId":"…"`, `"DurationMs":…` — capital first letter on every property. A filter like `jq 'select(.kind=="module_activated")'` (lowercase `kind`) returns zero results even when events exist. Use `.Kind`. Same for all other properties.
- **The kind isn't instrumented yet.** Check [What's instrumented today](#whats-instrumented-today). `scope` has no producers today; `ref_fetch` only fires on the HTTP path, not on cache hits.
- **The session didn't cover the event-generating window.** Hotkey-toggled sessions start *after* the shell is up — the initial `module_activated` fires during `ShellViewModel` construction and is therefore missed. Only subsequent tab clicks produce `module_activated` in that mode. Use `autoStartPerfTrace=true` if you want the first activation captured.
- **Autostart silently didn't fire.** If `enablePerfTrace=true` and `autoStartPerfTrace=true` aren't both in `shell.json` *before* the process starts, the autostart check at [`Program.cs`](../src/Mithril.Shell/Program.cs) skips. The regular diagnostics log will tell you — grep `%LocalAppData%\Mithril\Shell\logs\mithril-*.json` for `Session started:` dated to that run.
- **Sanity-check the file with a literal grep first.** Before reaching for jq, run a case-sensitive plain-text search for the kind string. If the literal string appears, the events exist and your jq filter has a bug. If it doesn't appear, the event isn't being produced (or wasn't produced during your session window).
  ```powershell
  Select-String -Path $Path -Pattern '"Kind":"module_activated"' -CaseSensitive
  ```

### What machine produced this trace?

```powershell
Get-Content $Path -TotalCount 1 | jq '{build:.Build, os:.Os, dpi:.Dpi, modules:.Modules}'
```

### Worst frame second across the trace

```powershell
Get-Content $Path | jq -c 'select(.Kind=="frame_summary") | {t:."@t", p95:.P95Ms, max:.MaxMs, stalls:.StallCount}' |
  Sort-Object { ($_ | ConvertFrom-Json).max } -Descending | Select-Object -First 5
```

### How many stalls and when

```powershell
Get-Content $Path | jq -c 'select(.Kind=="stall" or (.Kind=="frame" and .Stall==true)) | {t:."@t", ms:.IntervalMs}'
```

### Which module is slowest to first-activate

```powershell
Get-Content $Path | jq 'select(.Kind=="module_activated") | "\(.ModuleId)\t\(.DurationMs)ms"'
```

### Slowest dispatcher operation

```powershell
Get-Content $Path | jq -c 'select(.Kind=="dispatcher") | {p:.Priority, run:.RunMs, depth:.QueueDepthAtStart}' |
  Sort-Object { ($_ | ConvertFrom-Json).run } -Descending | Select-Object -First 10
```

### Memory pressure across the session

```powershell
Get-Content $Path | jq -c 'select(.Kind=="counter") | {t:."@t", ws:.WorkingSetMB, gen2:.Gen2, q:.DispatcherQueueDepth}'
```

### Did GC cause a specific stall? Look for a `gc` event within ~1 s of the stall

```powershell
Get-Content $Path | jq -c 'select(.Kind=="gc" or .Kind=="stall") | {t:."@t", k:.Kind, gen:.Generation, ms:.IntervalMs}'
```

### Binding-error flood detection

```powershell
Get-Content $Path | jq -c 'select(.Kind=="binding_error") | .Message' | Sort-Object -Unique
```

A handful of distinct messages each emitting once a second indicates a persistent broken binding — they accumulate layout-pass cost on every frame.

## Diagnostic patterns

A few signatures that have been useful in practice. Most failure modes have a characteristic combination — looking at one event kind in isolation is rarely enough.

**Gen-2 GC pause masquerading as "the app froze."** Look for a `gc` with `Generation=2` adjacent (≤1 s) to a `stall` event. Working-set in the surrounding `counter` events will usually be at its session-high right before. Action: profile allocations on the hot path; the trace doesn't name the offender but `counter.WorkingSetMB` over time narrows the suspect window.

**UI thread saturated.** `counter.DispatcherQueueDepth` stays > 0 across many samples and `dispatcher.RunMs` p95 is elevated. Usually a hosted service is dispatching too eagerly to the UI thread, or a `RelayCommand` is doing work it shouldn't. Filter `dispatcher` by `Priority` to find which queue is backed up.

**Slow first-render of a lazy module.** Single large `module_activated` event for that module id. The work is the gate-open broadcast plus the view-type resolution; usually means the view's constructor is doing too much (loading data, building grids). Move the work behind `Loaded` or off the UI thread.

**CDN fetch on the UI thread.** `ref_fetch` events with significant `DurationMs` show CDN behavior is fine, but if they appear interleaved with elevated `frame_summary.P95` it means the awaiter resumed on the dispatcher and burned a frame budget on JSON parse. Reference data should be loaded off the UI thread.

**Input lag without obvious render cost.** `input_latency` consistently > 50 ms but `frame_summary` looks healthy. Means the input event itself isn't slow — something between the input firing and the next frame is. Usually a binding update or a `Dispatcher.Invoke` that runs at `DataBind`/`Render` priority. Filter `dispatcher` by timestamp surrounding the lagging input event.

**Binding-error flood.** Many unique `binding_error` messages, or one message that re-emits every second for the whole session. WPF retries failed bindings on every layout pass — even with no visual change, each pass costs CPU. The throttle in [`BindingErrorTraceListener`](../src/Mithril.Shared/Diagnostics/Performance/BindingErrorTraceListener.cs) collapses 60×/sec floods to ~1×/sec for storage, but the underlying app cost is still there.

## What the harness doesn't capture

- Startup time **before** the WPF `Application` exists — Velopack hooks, mutex acquisition, settings JSON load, `Host.Build`, eager-module gate opens. [`Program.Boot()`](../src/Mithril.Shell/Program.cs) markers in `boot.log` cover those.
- Render-thread work below the dispatcher level (WPF composition / DWM). `frame_summary` reflects the *user-visible* result but doesn't decompose into measure/arrange/render phases.
- GPU-side cost. `frame_summary` accounts for the full frame including GPU work, but doesn't attribute it.
- Code on threadpool threads that doesn't reach the dispatcher. `scope` markers cover this if instrumented manually.

## Extending the harness

Adding a new event kind:

1. Add the string constant to [`PerfEventKinds.cs`](../src/Mithril.Shared/Diagnostics/Performance/PerfEventKinds.cs).
2. Add an `EmitX(...)` method to [`IPerfTracer.cs`](../src/Mithril.Shared/Diagnostics/Performance/IPerfTracer.cs) and [`PerfTracer.cs`](../src/Mithril.Shared/Diagnostics/Performance/PerfTracer.cs) following the existing pattern — `if (Volatile.Read(ref _logger) is null) return;` short-circuits the inactive path.
3. Call the emit from wherever the signal originates. For UI-thread hooks, wire/unwire in [`PerfTracerHostedService.AttachHooks`](../src/Mithril.Shared/Diagnostics/Performance/PerfTracerHostedService.cs).
4. Document the new `Kind` in this file under **Event kinds**.

Adding a per-module timing without a new event kind: prefer `using var s = perf.Scope("module.operation", new { tags })`. Cheap to add, costs nothing when no session is active, surfaces as a `scope` event that filters/aggregates cleanly alongside everything else.
