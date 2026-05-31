# Mithril for Project: Gorgon

A modular WPF desktop companion app for the MMORPG [*Project Gorgon*](https://projectgorgon.com). Mithril tails the game's log files in real time, parses events as they happen, and surfaces useful overlays and tools across a set of themed modules — gardening, surveying, food tracking, NPC favor, skill leveling advice, timers, storage, and spell-word discovery.

The app is read-only with respect to the game. It never injects into the client, reads memory, or modifies anything the game produces. It only watches the log and report files the game writes to disk.

## Project knowledge map

| If you're looking for… | It's at… |
|---|---|
| **Roadmap & live status** | the [Mithril Roadmap Project](https://github.com/orgs/moumantai-gg/projects/1) (org-level board, custom fields: `Status` / `Priority` / `Module`) |
| **Open work / bugs** | [Issues](https://github.com/moumantai-gg/mithril/issues) — file via the bug / feature templates |
| **Stable reference & user guides** | the [Wiki](https://github.com/moumantai-gg/mithril/wiki) — CDN data, releasing, treasure system, Arwen / Legolas user guides |
| **Design rationale & roadmap narrative** | [`docs/`](docs/) — per-module roadmap narrative + design notebooks |

## Modules

Each feature lives in its own class library, loaded dynamically at startup. Modules share infrastructure from `Mithril.Shared` but are otherwise independent.

Listed in tab order (`SortOrder`):

| Module       | Id           | Purpose                                                                 | Activation |
|--------------|--------------|-------------------------------------------------------------------------|------------|
| Samwise      | samwise      | Garden / crop tracking, ripeness & loss-prevention alarms               | Eager      |
| Pippin       | pippin       | Gourmand support — not-yet-eaten foods and where to get them            | Lazy       |
| Elrond       | elrond       | Skill leveling advisor (recipe-anchored)                                | Lazy       |
| Legolas      | legolas      | Surveying, route optimization, map overlay                              | Lazy       |
| Arwen        | arwen        | NPC favor and gift tracking ([guide](https://github.com/moumantai-gg/mithril/wiki/User-Guide-Arwen)) | Eager |
| Smaug        | smaug        | NPC store prices & sale economics                                       | Lazy       |
| Saruman      | saruman      | Words of Power — learn / known / consumed tracking                      | Lazy       |
| Gandalf      | gandalf      | Timers & repeatable-quest cooldowns                                     | Eager      |
| Bilbo        | bilbo        | Storage browsing + immediate craftability                               | Lazy       |
| Celebrimbor  | celebrimbor  | Crafting planner — shopping lists for *N* of *X*                        | Lazy       |
| Palantir     | palantir     | Debug / dev tools — inspector over the live world state                 | Lazy       |
| Silmarillion | silmarillion | Reference-data browser (items, recipes, NPCs, treasure system, …)       | Lazy       |

*Eager* modules start their background services at app launch. *Lazy* modules defer work behind a `ModuleGate` latch that opens the first time the user selects the module's tab, keeping startup cheap. Palantir is a developer/diagnostic surface, not a player-facing feature.

Each module's responsibility *boundaries* — what it owns and, just as importantly, what it explicitly does **not** own — live in [docs/module-charters.md](docs/module-charters.md).

## Requirements

- Windows 10 / 11 (x64)
- [.NET 10 SDK](https://dotnet.microsoft.com/download) (`10.0.100` or newer — see [global.json](global.json))
- A local install of *Project Gorgon* (the app auto-detects the game root and reads files under `%LocalAppData%Low\Elder Game\Project Gorgon\`)

## Build and run

```bash
# Build everything
dotnet build Mithril.slnx

# Run the app
dotnet run --project src/Mithril.Shell

# Run all tests
dotnet test Mithril.slnx

# Run a single test project
dotnet test tests/Samwise.Tests

# Run a single test by name
dotnet test tests/Samwise.Tests --filter "FullyQualifiedName~GardenStateMachineTests.Tier1_StartInteraction"
```

Module DLLs are copied into `src/Mithril.Shell/{Configuration}/modules/` automatically via [Directory.Build.targets](Directory.Build.targets) — there's no manual copy step. Drop-in third-party modules work the same way: place the assembly in the `modules/` folder and it will be discovered at startup.

### Running tests on a fresh clone

`[Trait("Category", "FileIO")]` tests write small JSON files to `tests/.tmp/` through `AtomicFile`'s write-tmp-then-rename sequence. That sequence is behaviourally identical to ransomware encryption passes, so Windows Defender's real-time scan will occasionally lock or quarantine the freshly-written file — the test then sees `items.meta.json` on disk but `items.json` missing. The repo already routes test scratch out of `%TEMP%` and into `tests/.tmp/` (see [tests/TestSupport/TestPaths.cs](tests/TestSupport/TestPaths.cs)) to dampen this; the residual flake comes from Defender still scanning that subtree under parallel load. CI sidesteps Defender entirely on the runner (see [.github/workflows/release.yml](.github/workflows/release.yml)); local dev environments use a targeted path exclusion:

```powershell
# Run once. Self-elevates if not already admin.
.\tools\setup-defender-exclusions.ps1

# To undo:
.\tools\setup-defender-exclusions.ps1 -Remove
```

The script whitelists exactly two paths — `tests\.tmp\` inside this repo and `%TEMP%\mithril-tests-fallback` (the fallback used when `TestPaths` can't locate the repo). Nothing broader: no repo-wide exclusion, no `dotnet.exe` process exclusion. Without it, expect the FileIO-category tests under `Mithril.Shared.Tests` and a few file-IO heavy tests in `Samwise.Tests` / `Gandalf.Tests` to flake intermittently when run as part of the full suite; in isolation they still pass.

### Force-quit hotkey

When Mithril or the machine is bogged down enough that finding the tray icon, right-clicking it, and clicking **Exit** is painful — but the dispatcher is still pumping messages — a single global keypress can land faster than the mouse round-trip. The `Force quit Mithril` command is that fallback: it triggers the same shutdown path as the tray's Exit (host stops, settings save, mutex releases) and ships **unbound**, so you have to assign a combo before you can use it.

To bind it:

1. Settings → Appearance → enable **Developer mode**.
2. Open the **Hotkeys** page (gear icon in the sidebar).
3. Find **Force quit Mithril** under *Shell · Diagnostics*, click the chip, press the combo you want.

The binding survives turning Developer mode back off — that flag only gates whether the row is visible in the Hotkeys UI, not whether the hotkey fires.

**What this is not:** if the UI thread is fully wedged (deadlocked lock, infinite loop, modal sync-wait), `WM_HOTKEY` sits in the queue and the hotkey never delivers — the same freeze that locked the UI locks the rescue. Task Manager is the only way out of that case; `Process.Kill` from inside Mithril wouldn't help either, since we can't run code the dispatcher won't dispatch.

## Repository layout

```
Mithril.slnx                Solution file (SLNX format)
Directory.Build.props       Shared MSBuild settings (C# latest, nullable, warnings-as-errors)
Directory.Build.targets     Module auto-copy convention + VSTHRD analyzer tuning
Directory.Packages.props    Central package version management
global.json                 Pins .NET 10 SDK

src/
  Mithril.Shell/            WPF host app, startup, module loader, tray/hotkeys
  Mithril.Shared/           Shared infrastructure (see below)
  Mithril.Shared.Wpf/       Shared WPF styles, converters, brushes, icons
  Arda/                     Log-replay & world-state engine (L0–L4; see Architecture)
  Mithril.Reference/        CDN reference-data models + serialization
  Mithril.GameReports/      Character-export (Reports/) parsing
  Mithril.Leveling/         Skill-XP math engine (consumed by Elrond)
  Mithril.Planning/         Craft-plan / shopping-list engine
  Mithril.Persistence/      Per-character JSON stores
  Mithril.Overlay/          DirectX overlay surface + windowing layer
  Mithril.MapCalibration/   Per-area world ↔ map-pixel transforms
  Mithril.Shared.Telemetry/ OTLP export wiring (opt-in)
  *.Module/                 One project per module (Samwise, Legolas, …)

tests/
  Mithril.Shared.Tests/     + one test project per module and per library

tools/
  Mithril.LogSanitizer/     Scrub personal paths/names out of captured logs
  RefreshAndValidate/       Refresh bundled CDN data and validate it
  XamlResourceLint/         Lint shared XAML resource usage
```

## Architecture

### Shell bootstrap

[src/Mithril.Shell/Program.cs](src/Mithril.Shell/Program.cs) handles startup in this order:

1. Single-instance mutex guard. A second launch raises the existing window via a named `EventWaitHandle` instead of starting a new process.
2. Game root detection.
3. Settings load.
4. `IHost` build — DI composition, module discovery, hosted services registered.
5. Eager module gates open; lazy modules stay gated until their tab is selected.
6. WPF `App.Run()`.

### Module contract

Every module is a project whose folder name ends with `.Module`. The project produces a class library implementing `IMithrilModule` (declared in [src/Mithril.Shared/Modules/](src/Mithril.Shared/Modules/)):

```csharp
public interface IMithrilModule
{
    string Id { get; }
    string DisplayName { get; }
    PackIconLucideKind Icon { get; }   // MahApps Lucide icon pack
    int SortOrder { get; }             // controls tab order
    ActivationMode DefaultActivation { get; }  // Eager | Lazy
    Type ViewType { get; }             // main UI
    Type? SettingsViewType { get; }    // optional per-module settings page
    void Register(IServiceCollection services);
}
```

Modules are discovered at runtime via reflection by `ShellServiceCollectionExtensions.AddMithrilModules`, which scans the `modules/` folder next to the shell. Lazy modules are gated by `ModuleGate` — a `TaskCompletionSource`-based latch that opens on first tab selection — so hosted services can `await gate.WaitAsync()` before doing work.

### Log processing — the Arda pipeline

All log ingestion runs through **Arda**, a deterministic log-replay and live world-state engine under [src/Arda/](src/Arda/). It is the sole log-processing engine — modules never tail `Player.log` themselves. The pipeline is layered:

| Layer | Project | Responsibility |
|---|---|---|
| L0 | `Arda.Ingest` | Tails `Player.log` + `ChatLogs/*.log` via `ILogLineSource` |
| L1 | `Arda.Ingest` | Span-based zero-alloc line parsing + string interning |
| L2 | `Arda.Dispatch` | `VerbExtractor` + `FrozenDictionary` dispatch table |
| L3 | `Arda.World.Player`, `Arda.World.Chat` | Stateful `IFrameHandler` implementations that emit domain events via `IDomainEventPublisher` |
| L4 | `Arda.Composition` | Cross-source composers (session fusion, inventory correlation, word-of-power) |

`Arda.Hosting` bootstraps the pipeline and exposes options for DI. `Arda.Contracts` holds the public domain events plus the read-only state interfaces (`ISessionState`, `IAreaState`, `IPlayerState`, `IChatSessionState`) and the subscriber/publisher contracts (`IDomainEventSubscriber`, `IDomainEventPublisher`). Modules consume Arda through `IDomainEventSubscriber` and these state interfaces — they never reference the internal handler or dispatch types. Because the engine replays the log deterministically, a module sees the same event sequence whether it attaches at startup or after the fact.

### Shared infrastructure (`Mithril.Shared`)

DI is composed via extension methods in [src/Mithril.Shared/DependencyInjection/ServiceCollectionExtensions.cs](src/Mithril.Shared/DependencyInjection/ServiceCollectionExtensions.cs).

- **Game services** — `IGameClock`, `IShiftCatalog`, `IGameReportsService`, `IActiveCharacterService`: game clocks, the time-of-day shift schedule, and character snapshots parsed from the game's `Reports/` exports. Live log tailing is *not* here — it belongs to the Arda pipeline above.
- **Reference data** — `IReferenceDataService` fetches versioned JSON (items, recipes, skills, NPCs, XP tables) from `https://cdn.projectgorgon.com/{version}/data/{file}.json` with bundled JSON under [src/Mithril.Shared/Reference/BundledData/](src/Mithril.Shared/Reference/BundledData/) as fallback. `CdnVersionDetector` resolves the current CDN version by parsing a redirect meta tag. Item icons come from `https://cdn.projectgorgon.com/{version}/icons/icon_{IconId}.png`.
- **Settings** — `ISettingsStore<T>` / `JsonSettingsStore<T>` using `System.Text.Json` source-generated contexts (no reflection-based serialization). `SettingsAutoSaver<T>` periodically flushes dirty state.
- **Hotkeys** — OS-level Win32 global hotkey registration. Modules contribute `IHotkeyCommand` implementations; `HotkeyConflictDetector` validates uniqueness at registration.
- **Diagnostics & telemetry** — `ILogger` via `DiagnosticsLoggerProvider` (in-app ring buffer, Rx live stream, Serilog compact-JSON file sink). Traces and metrics are emitted through the canonical `System.Diagnostics` `ActivitySource`/`Meter` instances in `Mithril.Shared.Diagnostics.Telemetry`; an opt-in OTLP exporter (`Mithril.Shared.Telemetry`) is available behind a setting. Recording is zero-cost when no session is listening.

### Patterns

- **MVVM** with `CommunityToolkit.Mvvm` source generators (`[ObservableProperty]`, `[RelayCommand]`).
- **Settings classes** implement `INotifyPropertyChanged` and are serialized through source-generated `JsonSerializerContext`s.
- **HostedServices** carry background work. Lazy-module services gate behind `ModuleGate.WaitAsync()`.
- **WPF resources** (styles, converters, brushes) are shared via [src/Mithril.Shared.Wpf/Resources.xaml](src/Mithril.Shared.Wpf/Resources.xaml). Icons come from the MahApps Lucide pack.

## Paths the app touches

Read from `%LocalAppData%Low\Elder Game\Project Gorgon\`:

- `Player.log` — primary event source (garden actions, combat, items, skill ticks)
- `ChatLogs/` — chat message logs
- `Reports/` — character data exports

App settings persist under `%LocalAppData%\Mithril\` — each module gets its own subfolder with a `settings.json`.

## Build configuration

- **Target framework:** `net10.0-windows`
- **Language:** C# latest, nullable enabled, warnings-as-errors (except `CS1591` — missing XML docs)
- **Central package management:** [Directory.Packages.props](Directory.Packages.props)
- **Threading analyzer:** `VSTHRD002` (sync-over-async) is enforced as an error. Other VSTHRD rules that assume a Visual-Studio `JoinableTaskFactory` context are suppressed — this is a standalone WPF app, not a VS extension.
- **Test stack:** xunit + FluentAssertions. Each module has a parallel test project under `tests/`.

## Contributing

- Keep modules independent of each other. Cross-module dependencies go through `Mithril.Shared`.
- New log grammar belongs in the Arda pipeline (a L2/L3 handler emitting a domain event), not in a module. Modules consume the resulting domain events via `IDomainEventSubscriber` — they don't parse `Player.log` themselves.
- Match the existing MVVM and settings conventions (source-generated, not reflection).
- Every behavioral change ships with a test. The test projects mirror the module layout one-for-one.

## Credits

First and foremost, thank you to the team at **Elder Game** for making [*Project Gorgon*](https://projectgorgon.com), the wonderfully strange MMORPG this companion app exists to serve. Mithril is a fan project built *around* their game: it reads the log and report files the game writes to disk, draws on the publicly published reference data, and would have nothing to do without the world they've built. All game content, names, and data belong to Elder Game; this project claims none of it.

Mithril is unofficial and not affiliated with or endorsed by Elder Game. As noted above, it only ever reads what the game writes to disk.

The project and module names (*Mithril*, *Arda*, Samwise, Gandalf, and the rest) are affectionate nods to J.R.R. Tolkien's legendarium, used purely as internal code names. They imply no affiliation with, endorsement by, or rights to Tolkien's works, which belong to their respective rights holders.

## Acknowledgements

The Samwise garden module began as a direct C# port of [Goozify/GorgonHelper](https://github.com/Goozify/GorgonHelper), a browser-based garden helper for *Project Gorgon*, and has since evolved on top of that foundation. Thanks to its author for the original log-tailing and garden-state logic that this project is built on.

The Legolas surveying module is inspired by [kaeus/GorgonSurveyTracker](https://github.com/kaeus/GorgonSurveyTracker). Thanks to its author for the original survey-tracking work.

The in-game-clock anchor and time-of-day shift schedule (Midnight / Dawn / Morning / Afternoon / Dusk / Night) are sourced from [pgemissary.com](https://pgemissary.com) — its `/api/game-clocks` endpoint provides the wall-clock anchor consumed by `GameClock`, and the `TIME_OF_DAY` constant in `static/js/game_clock.js` is the source of truth for the bundled shift catalog at [`src/Mithril.Shared/Reference/BundledData/shifts.json`](src/Mithril.Shared/Reference/BundledData/shifts.json). Thanks to pgemissary's author for keeping that data publicly observable.
