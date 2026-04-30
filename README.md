# Mithril for Project: Gorgon

A modular WPF desktop companion app for the MMORPG [*Project Gorgon*](https://projectgorgon.com). Mithril tails the game's log files in real time, parses events as they happen, and surfaces useful overlays and tools across a set of themed modules — gardening, surveying, food tracking, NPC favor, skill leveling advice, timers, storage, and spell-word discovery.

The app is read-only with respect to the game. It never injects into the client, reads memory, or modifies anything the game produces. It only watches the log and report files the game writes to disk.

## Project knowledge map

| If you're looking for… | It's at… |
|---|---|
| **Roadmap & live status** | the [Mithril Roadmap Project](https://github.com/arthur-conde/project-gorgon/projects) (board view, custom fields per module / target version) |
| **Open work / bugs** | [Issues](https://github.com/arthur-conde/project-gorgon/issues) — file via the bug / feature templates |
| **Stable reference & user guides** | the [Wiki](https://github.com/arthur-conde/project-gorgon/wiki) — CDN data, releasing, treasure system, Arwen / Legolas user guides |
| **Design rationale & roadmap narrative** | [`docs/`](docs/) — per-module roadmap narrative + design notebooks |

## Modules

Each feature lives in its own class library, loaded dynamically at startup. Modules share infrastructure from `Mithril.Shared` but are otherwise independent.

| Module  | Id       | Purpose                                                      | Activation |
|---------|----------|--------------------------------------------------------------|------------|
| Samwise | samwise  | Garden / crop tracking, ripeness alarms                      | Eager      |
| Pippin  | pippin   | Food consumption and recipe tracking                         | Lazy       |
| Legolas | legolas  | Surveying, route optimization, map overlay                   | Lazy       |
| Arwen   | arwen    | NPC favor and gift tracking ([guide](https://github.com/arthur-conde/project-gorgon/wiki/User-Guide-Arwen)) | Lazy     |
| Elrond  | elrond   | Skill leveling advisor                                       | Lazy       |
| Gandalf | gandalf  | User-created timers with alarms                              | Eager      |
| Bilbo   | bilbo    | Storage / inventory management                               | Lazy       |
| Saruman | saruman  | Words of Power — chat-log discovery tracking                 | Lazy       |

*Eager* modules start their background services at app launch. *Lazy* modules defer work behind a `ModuleGate` latch that opens the first time the user selects the module's tab, keeping startup cheap.

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

## Repository layout

```
Mithril.slnx                 Solution file (SLNX format)
Directory.Build.props       Shared MSBuild settings (C# latest, nullable, warnings-as-errors)
Directory.Build.targets     Module auto-copy convention + VSTHRD analyzer tuning
Directory.Packages.props    Central package version management
global.json                 Pins .NET 10 SDK

src/
  Mithril.Shell/             WPF host app, startup, module loader, tray/hotkeys
  Mithril.Shared/            Shared infrastructure (see below)
  Samwise.Module/           Garden tracker
  Pippin.Module/            Food tracker
  Legolas.Module/           Survey / map module
  Arwen.Module/             NPC favor module
  Elrond.Module/            Skill advisor
  Gandalf.Module/           Timers
  Bilbo.Module/             Storage / inventory
  Saruman.Module/           Words of Power codebook

tests/
  Mithril.Shared.Tests/      + one test project per module

tools/
  CropsExtractor/           Helper for building reference data
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

### Shared infrastructure (`Mithril.Shared`)

DI is composed via extension methods in [src/Mithril.Shared/DependencyInjection/ServiceCollectionExtensions.cs](src/Mithril.Shared/DependencyInjection/ServiceCollectionExtensions.cs).

- **Game services** — `IPlayerLogStream`, `IChatLogStream`, `ICharacterDataService`. These tail the live game logs and parse the JSON character exports the game produces.
- **Reference data** — `IReferenceDataService` fetches versioned JSON (items, recipes, skills, NPCs, XP tables) from `https://cdn.projectgorgon.com/{version}/data/{file}.json` with bundled JSON under [src/Mithril.Shared/Reference/BundledData/](src/Mithril.Shared/Reference/BundledData/) as fallback. `CdnVersionDetector` resolves the current CDN version by parsing a redirect meta tag. Item icons come from `https://cdn.projectgorgon.com/{version}/icons/icon_{IconId}.png`.
- **Settings** — `ISettingsStore<T>` / `JsonSettingsStore<T>` using `System.Text.Json` source-generated contexts (no reflection-based serialization). `SettingsAutoSaver<T>` periodically flushes dirty state.
- **Hotkeys** — OS-level Win32 global hotkey registration. Modules contribute `IHotkeyCommand` implementations; `HotkeyConflictDetector` validates uniqueness at registration.
- **Diagnostics** — Serilog-backed `IDiagnosticsSink`, compact JSON file sink.

### Log parsing

Log parsers implement `ILogParser.TryParse(string line, DateTime timestamp)` and return a `LogEvent?`. Modules compose parsers into state machines that consume events from `IPlayerLogStream` and `IChatLogStream`, both of which tail their respective files with rotation handling. `IChatLogParser` was lifted into `Mithril.Shared` so multiple modules can register their own chat-line handlers.

### Patterns

- **MVVM** with `CommunityToolkit.Mvvm` source generators (`[ObservableProperty]`, `[RelayCommand]`).
- **Settings classes** implement `INotifyPropertyChanged` and are serialized through source-generated `JsonSerializerContext`s.
- **HostedServices** carry background work. Lazy-module services gate behind `ModuleGate.WaitAsync()`.
- **WPF resources** (styles, converters, brushes) are shared via [src/Mithril.Shared/Wpf/Resources.xaml](src/Mithril.Shared/Wpf/Resources.xaml). Icons come from the MahApps Lucide pack.

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
- New log parsers belong in the owning module unless they're reused — parsers used by two or more modules get promoted into `Mithril.Shared`.
- Match the existing MVVM and settings conventions (source-generated, not reflection).
- Every behavioral change ships with a test. The test projects mirror the module layout one-for-one.

## Acknowledgements

The Samwise garden module began as a direct C# port of [Goozify/GorgonHelper](https://github.com/Goozify/GorgonHelper), a browser-based garden helper for *Project Gorgon*, and has since evolved on top of that foundation. Thanks to its author for the original log-tailing and garden-state logic that this project is built on.

The Legolas surveying module is inspired by [kaeus/GorgonSurveyTracker](https://github.com/kaeus/GorgonSurveyTracker). Thanks to its author for the original survey-tracking work.
