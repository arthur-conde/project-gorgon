# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## What This Is

Mithril is a modular WPF desktop companion app for the MMORPG *Project Gorgon*. It tails the game's log files in real time, parses events, and provides modules for gardening, surveying, food tracking, NPC favor, skill advising, timers, and storage management.

## Build & Test

```bash
# Build everything (requires .NET 10 SDK)
dotnet build Mithril.slnx

# Run all tests
dotnet test Mithril.slnx

# Run a single test project
dotnet test tests/Samwise.Tests

# Run a single test by name
dotnet test tests/Samwise.Tests --filter "FullyQualifiedName~GardenStateMachineTests.Tier1_StartInteraction"

# Run the app
dotnet run --project src/Mithril.Shell
```

Module DLLs are auto-copied to `src/Mithril.Shell/{config}/modules/` on build (see `Directory.Build.targets`). No manual copy step needed.

## Build Configuration

- **.NET 10**, `net10.0-windows`, C# latest, nullable enabled, warnings-as-errors (except CS1591)
- Central package management via `Directory.Packages.props`
- `VSTHRD002` (sync-over-async) is enforced as error; other VSTHRD rules are suppressed (no VS JoinableTaskFactory context)
- Test framework: **xunit** + **FluentAssertions**

## Architecture

### Module System

Every module is a class library whose folder name ends with `.Module`, implementing `IMithrilModule` (in `Mithril.Shared/Modules/`). The interface requires:

- `Id`, `DisplayName`, `Icon` (Lucide), `SortOrder`, `DefaultActivation` (Eager/Lazy)
- `ViewType` (main UI), optional `SettingsViewType`
- `Register(IServiceCollection)` for DI setup

Modules are discovered at runtime via reflection from the `modules/` folder (`ShellServiceCollectionExtensions.AddMithrilModules`). Lazy modules are gated by `ModuleGate` — a `TaskCompletionSource`-based latch opened on first tab selection.

### Current Modules

| Module | Id | Purpose | Activation |
|---|---|---|---|
| Samwise | samwise | Garden/crop tracking, ripeness alarms | Eager |
| Pippin | pippin | Food consumption & recipe tracking | Lazy |
| Legolas | legolas | Surveying, route optimization, map overlay | Lazy |
| Arwen | arwen | NPC favor & gift tracking | Lazy |
| Elrond | elrond | Skill leveling advisor | Lazy |
| Gandalf | gandalf | User-created timers with alarms | Eager |
| Bilbo | bilbo | Storage/inventory management | Lazy |

This table is purpose-only and non-exhaustive (Silmarillion and Celebrimbor also ship). **Before proposing or building work for a module, read [docs/module-charters.md](docs/module-charters.md)** — it records each module's responsibility *boundaries* (what it explicitly does **not** own, and why). A data-availability gap is not a feature unless it serves the module's charter.

### Arda Pipeline (sole log-processing engine)

Arda is a deterministic log-replay and live world-state tracking engine organised in five layers:

| Layer | Project(s) | Responsibility |
|---|---|---|
| L0 | `Arda.Ingest` | Tails `Player.log` + `ChatLogs/*.log` via `ILogLineSource` |
| L1 | `Arda.Ingest` | Span-based zero-alloc line parsing, string interning |
| L2 | `Arda.Dispatch` | `VerbExtractor` + `FrozenDictionary` dispatch table |
| L3 | `Arda.World.Player`, `Arda.World.Chat` | Stateful `IFrameHandler` implementations; emit domain events via `IDomainEventBus` |
| L4 | `Arda.Composition` | Cross-source composers (session fusion, inventory correlation, word-of-power) |

`Arda.Hosting` bootstraps the pipeline and exposes `ArdaOptions` for DI. `Arda.Contracts` holds the public domain events, state interfaces (`ISessionState`, `IAreaState`, `IPlayerState`, `IChatSessionState`), and subscriber/publisher contracts (`IDomainEventSubscriber`, `IDomainEventPublisher`).

Modules consume Arda via `IDomainEventSubscriber` and the read-only state interfaces — they never reference the internal handler or dispatch types.

> **Legacy L0/L1 pipeline.** `IPlayerLogStream`, `IChatLogStream`, `ILogStreamDriver`, and `LogStreamAttentionSource` still exist in `Mithril.Shared` for the subscription-health badge. Follow-on work will retire them once Arda exposes equivalent health signaling.

### Shell Bootstrap (Program.cs)

Single-instance mutex guard &rarr; game root detection &rarr; settings load &rarr; `IHost` build &rarr; eager module gates opened &rarr; WPF `App.Run()`. Second-instance attempts raise the existing window via `EventWaitHandle`.

### Shared Infrastructure (Mithril.Shared)

DI is composed via extension methods in `Mithril.Shared/DependencyInjection/ServiceCollectionExtensions.cs`:

- **Game services**: `IPlayerLogStream`, `IChatLogStream`, `ICharacterDataService` — tail game logs and parse character exports
- **Reference data**: `IReferenceDataService` — fetches JSON (items, recipes, skills, NPCs, XP tables) from `cdn.projectgorgon.com` with bundled fallback and background refresh
- **Settings**: `ISettingsStore<T>` / `JsonSettingsStore<T>` with `System.Text.Json` source-generated contexts; `SettingsAutoSaver<T>` for periodic persistence
- **Hotkeys**: OS-level Win32 hotkey registration; modules provide `IHotkeyCommand` implementations; `HotkeyConflictDetector` validates uniqueness
- **Diagnostics**: Serilog-backed `IDiagnosticsSink`
- **Query system**: SQL-like filtering over data models — `MithrilDataGrid`/`MithrilQueryBox` (tabular UI), `QueryFilter` attached behaviour (any `ItemsControl`), `QueryableSource<T>` (VM-side, headless). See [docs/query-system.md](docs/query-system.md) before adding new filter UI.

### Patterns to Follow

- **MVVM with CommunityToolkit.Mvvm** source generators (`[ObservableProperty]`, `[RelayCommand]`)
- **Settings classes** implement `INotifyPropertyChanged` with source-generated JSON serialization contexts (not reflection)
- **Log parsing**: Arda L3 handlers implement `IFrameHandler.Handle(ReadOnlySpan<char> args, string sourceLog, LogLineMetadata metadata)` with span-based zero-alloc parsing and emit domain events via `IDomainEventPublisher`. Module-level consumers subscribe via `IDomainEventSubscriber`
- **HostedServices** for background work; gated behind `ModuleGate.WaitAsync()` for lazy modules
- **WPF resources** shared via `Mithril.Shared.Wpf/Resources.xaml`; icons from MahApps Lucide icon pack
- **Before editing any `*.xaml` or writing a new view, read [docs/wpf-gotchas.md](docs/wpf-gotchas.md)** — catalogues runtime-only WPF traps (hit-testing, null-leak templates, binding-mode defaults, `ItemContainerStyle` rules, etc.) that build green + tests green but break the UI silently.
- **For C# work touching >1 type, load the LSP tool first** (`csharp-lsp` plugin is enabled; `ToolSearch query: "select:LSP"` to fetch its schema, then use it for go-to-def / find-refs / type info). Grep alone misses partial classes, source-generated members (`[ObservableProperty]` setters, JSON contexts), and overload signatures, so a "no callers" or "no implementations" claim from text search alone is not load-bearing.
- **Cross-source correlation** — before wiring a new consumer that fuses Player.log + chat (or any two streams), read [docs/cross-source-correlation.md](docs/cross-source-correlation.md). It defines the Tier 1/2/3/4 decision tree and points at the canonical references (`PendingCorrelator<TKey,TReq>` for Tier 1, `MotherlodeMeasurementCoordinator` for Tier 2). Skip a tier and you'll reinvent the pre-#541 "credit at least 1 if the add never arrived" folk fallback.

### Game Data Paths

The app reads from `%LocalAppData%Low/Elder Game/Project Gorgon/`:
- `Player.log` — primary event source (garden actions, combat, items)
- `ChatLogs/` — chat message logs
- `Reports/` — character data exports

App settings persist to `%LocalAppData%/Mithril/`.

### CDN Reference Data

`ReferenceDataService` fetches versioned JSON from `https://cdn.projectgorgon.com/{version}/data/{file}.json`. Version is auto-detected by `CdnVersionDetector` (parses redirect meta tag). Bundled copies under `Mithril.Shared/Reference/BundledData/` serve as fallback. Item icons are available at `https://cdn.projectgorgon.com/{version}/icons/icon_{IconId}.png`. Full file inventory + schema notes: [wiki: CDN Reference Data](https://github.com/moumantai-gg/mithril/wiki/CDN-Reference-Data).

## Where does new content go?

Project knowledge is split across four tiers. Route new content by what it is:

| If you're writing… | Put it… |
|---|---|
| A pending unit of work (bug, feature, chore) | A GitHub Issue. Use the bug/feature template; the dropdowns auto-apply `module:*` and `area:*` labels. |
| Roadmap / prioritisation state | The [**Mithril Roadmap** Project](https://github.com/orgs/moumantai-gg/projects/1) (org-level, replaced the legacy user-level board 2026-05-21). Custom fields: `Status`, `Priority`, `Module`. (Earlier scheme had `Effort` + `Target Version`; dropped to reduce maintenance friction — re-add only if a real need surfaces.) Don't add inline checklists to roadmap docs — the doc holds *why*, the issue holds *what*. |
| Stable reference, process, how-to, user guide | The [wiki](https://github.com/moumantai-gg/mithril/wiki). Stable content; doesn't co-evolve with code. |
| Design rationale that co-evolves with code | `docs/` in this repo. Roadmap *narrative* (why we deferred X, what would unblock Y), design notebooks, architecture decisions. |
| Implementation spec / finalized plan for a follow-up agent | The **GitHub Issue body itself**. Fold the full spec — struct dumps, caveats, "verification owed", test plan — into the issue so a cold/spawned session is self-contained from the issue alone. **GitHub is the only home for finalized plans.** |

**Workflow rules:**

1. **Backlog item → Issue first.** Don't add a checkbox to a roadmap doc. Issues are queryable, have state, and surface on the Project board.
2. **Issue references doc, doc doesn't list issues.** Each issue body links to the relevant `docs/` or wiki page for context. Roadmap docs link to the *Project* (which lists the issues), not to individual issues, so docs don't rot when issues close.
3. **Anything load-bearing-but-unverified gets a "Verification owed" marker** in the design notebook. Filing an issue for the spot-check is the *task side*; the doc entry stays for context.
4. **Finalized plans live in GitHub only; local plan files are scratch.** Implementation specs go in the issue body, not a checked-in plan doc. `docs/agent-plans/`, `.claude/plans/`, and temp files are *thinking scratch only* — never the canonical artifact, never required reading for a spawned session, and **deleted once the implementation lands** (plain delete; if it was never committed, just `rm`). Do not commit a plan doc solely so a cold session can read it — fold it into the issue instead.
