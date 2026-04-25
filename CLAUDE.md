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

### Shell Bootstrap (Program.cs)

Single-instance mutex guard &rarr; game root detection &rarr; settings load &rarr; `IHost` build &rarr; eager module gates opened &rarr; WPF `App.Run()`. Second-instance attempts raise the existing window via `EventWaitHandle`.

### Shared Infrastructure (Mithril.Shared)

DI is composed via extension methods in `Mithril.Shared/DependencyInjection/ServiceCollectionExtensions.cs`:

- **Game services**: `IPlayerLogStream`, `IChatLogStream`, `ICharacterDataService` — tail game logs and parse character exports
- **Reference data**: `IReferenceDataService` — fetches JSON (items, recipes, skills, NPCs, XP tables) from `cdn.projectgorgon.com` with bundled fallback and background refresh
- **Settings**: `ISettingsStore<T>` / `JsonSettingsStore<T>` with `System.Text.Json` source-generated contexts; `SettingsAutoSaver<T>` for periodic persistence
- **Hotkeys**: OS-level Win32 hotkey registration; modules provide `IHotkeyCommand` implementations; `HotkeyConflictDetector` validates uniqueness
- **Diagnostics**: Serilog-backed `IDiagnosticsSink`

### Patterns to Follow

- **MVVM with CommunityToolkit.Mvvm** source generators (`[ObservableProperty]`, `[RelayCommand]`)
- **Settings classes** implement `INotifyPropertyChanged` with source-generated JSON serialization contexts (not reflection)
- **Log parsing**: implement `ILogParser.TryParse(string line, DateTime timestamp)` returning a `LogEvent?`; state machines consume events from `IPlayerLogStream`/`IChatLogStream`
- **HostedServices** for background work; gated behind `ModuleGate.WaitAsync()` for lazy modules
- **WPF resources** shared via `Mithril.Shared/Wpf/Resources.xaml`; icons from MahApps Lucide icon pack

### Game Data Paths

The app reads from `%LocalAppData%Low/Elder Game/Project Gorgon/`:
- `Player.log` — primary event source (garden actions, combat, items)
- `ChatLogs/` — chat message logs
- `Reports/` — character data exports

App settings persist to `%LocalAppData%/Mithril/`.

### CDN Reference Data

`ReferenceDataService` fetches versioned JSON from `https://cdn.projectgorgon.com/{version}/data/{file}.json`. Version is auto-detected by `CdnVersionDetector` (parses redirect meta tag). Bundled copies under `Mithril.Shared/Reference/BundledData/` serve as fallback. Item icons are available at `https://cdn.projectgorgon.com/{version}/icons/icon_{IconId}.png`.
