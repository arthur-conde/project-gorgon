---
name: growth-calibration
description: Use this agent when working on Samwise's calibration system — the three observation streams (full growth cycles, per-phase transitions, slot-family caps), the items.json-driven seed map, or the trimmed-down crops.json. Covers recording, aggregating, and UI display of calibration data.
---

# Samwise Calibration

## What this is

`crops.json` defines `slotFamily` + `growthSeconds` per crop, which drive progress bars, ripeness alarms, and slot-family max counts. Analysis of real Player.log data showed `growthSeconds` values were consistently 6–10% too high. The **growth calibration service** records three parallel observation streams from live gameplay:

1. **Full cycles** — one `GrowthObservation` per Plant→Ripe cycle with effective-seconds (minus pause time).
2. **Phase transitions** — one `PhaseTransitionObservation` per stage change, even on partial cycles that never reach Ripe. Player-reaction transitions (Thirsty→Growing, NeedsFertilizer→Growing) are stored raw but excluded from rate aggregation.
3. **Slot-family caps** — one `SlotCapObservation` each time the game emits a "can't be used: You already have the maximum of that type of plant growing" error. At error time, the current family plot count equals the cap.

All three streams persist to a single JSON file with export/import so data can be crowd-sourced.

## Where it lives

**Core service** — [src/Samwise.Module/Calibration/](src/Samwise.Module/Calibration/)
- [GrowthCalibration.cs](src/Samwise.Module/Calibration/GrowthCalibration.cs) — data models (`GrowthObservation`, `PhaseRecord`, `CropGrowthRate`, `PhaseTransitionObservation`, `PhaseTransitionRate`, `SlotCapObservation`, `SlotCapRate`, `GrowthCalibrationData`) + JSON context
- [GrowthCalibrationService.cs](src/Samwise.Module/Calibration/GrowthCalibrationService.cs) — subscribes to `GardenStateMachine.PlotChanged` (cycle + phase data) and `SlotCapObserved` (cap data); shadow-tracks phase timestamps; aggregates via `RecomputeRates` / `RecomputePhaseRates` / `RecomputeSlotCapRates`

**UI** — [src/Samwise.Module/Views/](src/Samwise.Module/Views/)
- [SamwiseView.xaml](src/Samwise.Module/Views/SamwiseView.xaml) — tabbed container (Garden + Growth Calibration)
- [GrowthCalibrationTab.xaml](src/Samwise.Module/Views/GrowthCalibrationTab.xaml) — four `MithrilDataGrid`s: Crop Growth Rates, Phase Transitions, Slot Family Caps, Observations (filtered via `MithrilQueryBox`)
- [GrowthCalibrationViewModel.cs](src/Samwise.Module/ViewModels/GrowthCalibrationViewModel.cs)

**Parser** — [src/Samwise.Module/Parsing/](src/Samwise.Module/Parsing/)
- [GardenLogParser.cs](src/Samwise.Module/Parsing/GardenLogParser.cs) `PlantingCapRx` matches `ProcessErrorMessage(ItemUnusable, "<SeedName> can't be used: You already have the maximum of that type of plant growing")`
- [GardenEvents.cs](src/Samwise.Module/Parsing/GardenEvents.cs) `PlantingCapReached(Timestamp, SeedDisplayName)`

**State machine** — [GardenStateMachine.cs](src/Samwise.Module/State/GardenStateMachine.cs)
- `_seedToCrop` map built from items.json `Seed=N`/`Seedling=N`/`Leafling=N`/`Sprout=N` keywords; rebuilt on `IReferenceDataService.FileUpdated`
- `ResolveCropFromItemName` lookup order: seed map → items.json Name fallback (`TrimSeedSuffix`) → conservative `crops.json` first-word prefix match (last-resort, mainly for tests and missing CDN data)
- `HandlePlantingCap` resolves seed display name → crop → family via items.json lookup, counts current family plots, raises `SlotCapObserved` event
- `CountFamilyPlots(charName, family)` — public helper reused by `IsSlotFull`

**Wiring** — [SamwiseModule.cs](src/Samwise.Module/SamwiseModule.cs), [GardenIngestionService.cs](src/Samwise.Module/State/GardenIngestionService.cs) (keep-alive ref to the service)

**Tests** — [tests/Samwise.Tests/GrowthCalibrationServiceTests.cs](tests/Samwise.Tests/GrowthCalibrationServiceTests.cs) — covers cycles, pauses, aggregation, persistence, export/import, hydration skip, phase transitions on partial cycles, player-reaction filtering, slot cap observations, slot cap dedup within 2s window

**Persistence** — `%LocalAppData%/Mithril/Samwise/growth-calibration.json`

## Reference pattern

Modeled on Arwen's gift/favor calibration. When in doubt, look at the equivalent Arwen file:

| Samwise | Arwen analogue |
|---|---|
| `GrowthCalibration.cs` | [src/Arwen.Module/Domain/GiftCalibration.cs](src/Arwen.Module/Domain/GiftCalibration.cs) |
| `GrowthCalibrationService.cs` | [src/Arwen.Module/Domain/CalibrationService.cs](src/Arwen.Module/Domain/CalibrationService.cs) |
| `GrowthCalibrationViewModel.cs` | [src/Arwen.Module/ViewModels/CalibrationViewModel.cs](src/Arwen.Module/ViewModels/CalibrationViewModel.cs) |
| `GrowthCalibrationTab.xaml` | [src/Arwen.Module/Views/CalibrationTab.xaml](src/Arwen.Module/Views/CalibrationTab.xaml) |
| `SamwiseView.xaml` (tabbed) | [src/Arwen.Module/Views/FavorView.xaml](src/Arwen.Module/Views/FavorView.xaml) |

## Key design decisions to preserve

1. **Subscribes to `GardenStateMachine.PlotChanged` and `SlotCapObserved`** rather than parsing logs separately. The state machine has already resolved crop type, pause duration, harvest detection, and slot-family lookup — don't duplicate that work.

2. **Shadow phase tracking** (`Dictionary<string, List<PhaseRecord>> _activeTracking` keyed by `"{charName}|{plotId}"`). The `Plot` model stores only *accumulated* pause duration, not per-phase timestamps. The service records each transition's wall-clock time as it fires AND emits a `PhaseTransitionObservation` to the aggregated stream the moment a phase closes — so partial cycles still contribute data.

3. **Hydrated plots are NOT tracked.** On app restart, plots come back via `GardenStateMachine.Hydrate` and fire `PlotChanged` with `OldStage=null` but `NewStage != Planted`. Skip those — their `PlantedAt` is from a previous session and phase timestamps are lost. Only fresh plants observed live from `Planted` contribute calibration data.

4. **Cleanup on `PlotsRemoved`.** Plots that never reach Ripe (garbage-collected, manually deleted) would leak `_activeTracking` entries otherwise.

5. **EffectiveSeconds = wall time − (Thirsty + NeedsFertilizer durations).** Player reaction time (water/fertilize delay) doesn't count as growth.

6. **Phase rate aggregation excludes player reactions.** `IsGrowthTransition` filters out `Thirsty→Growing` and `NeedsFertilizer→Growing` — those measure how long the player took to water/fertilize, not the game's growth clock. Raw observations for those transitions are still stored (for transparency), just not aggregated into rates.

7. **Slot cap dedup window: 2s.** Spam-clicking a seed against a full family produces bursts of identical errors; only the first in each window counts as an observation.

8. **Seed map is built from items.json, rebuilt on CDN refresh.** Detection: item has a Keyword with Tag ∈ {Seed, Seedling, Leafling, Sprout}. Crop name = `TrimSeedSuffix(item.Name)` (strips ` Seeds`, ` Seedling`, ` Leafling`, ` Sprout`).

## Trimmed-down crops.json (schema v2)

`crops.json` only carries what can't be derived or crowdsourced:

```json
{
  "schemaVersion": 2,
  "slotFamilies": { "Flowers": { "max": 3 }, ... },
  "crops": {
    "Barley": { "slotFamily": "Grass", "growthSeconds": 150 },
    ...
  }
}
```

**Removed fields (were dead code):** `iconId`, `harvestVerb`, `modelAliases`, `itemNamePrefixes`. The `LearnedAliasesStore` — which existed solely to feed `ModelAliases` — was also deleted.

**Unknown-field tolerance:** System.Text.Json ignores unknown properties silently, so an old user crops.json with the dropped fields still deserializes; the extras become dead bytes on disk until the user edits the file.

## Game event flow (what the state machine sees)

From `Player.log`:
1. `ProcessSetPetOwner(plotId, ...)` → `PlotStage.Planted`, `PlantedAt` recorded
2. `ProcessUpdateDescription(..., "Water X", ...)` → `PlotStage.Thirsty`, pause starts, **phase observation emitted**
3. `ProcessUpdateDescription(..., "Tend X", ...)` → back to `PlotStage.Growing`, pause ends, **phase observation emitted (but excluded from rates)**
4. `ProcessUpdateDescription(..., "Fertilize X", ...)` → `PlotStage.NeedsFertilizer`, pause starts, **phase observation emitted**
5. `ProcessUpdateDescription(..., "Tend X", ...)` → back to `PlotStage.Growing`, **phase observation emitted (excluded from rates)**
6. `ProcessUpdateDescription(..., "Harvest X" | "Pick X", ...)` → `PlotStage.Ripe` — **full-cycle observation recorded here**
7. `ProcessStartInteraction` + harvest detection → `PlotStage.Harvested`
8. `ProcessErrorMessage(ItemUnusable, "... can't be used: You already have the maximum of that type of plant growing")` → **slot-cap observation recorded**

Not all crops have both water and fertilize phases — some go straight Planted → Ripe, some only need water. The service handles variable phase sequences.

## Next candidate signals

- **"Wait about N seconds" error** (from `ProcessScreenText(ErrorMessage, "This X is still growing. (Wait about N seconds.)")`) — gives a direct server-side remaining-time readout. Paired with elapsed time, yields a clean growth-time data point independent of the full-cycle observation. Noise is high (server rounds to bucket) but it's cheap and works on plots the player is interacting with, including hydrated ones. Not implemented yet; worth considering if full-cycle data proves insufficient.

## Build & test

```bash
dotnet build Mithril.slnx
dotnet test tests/Samwise.Tests
dotnet test tests/Samwise.Tests --filter "FullyQualifiedName~GrowthCalibration"
```

## Style & constraints

- .NET 10, C# latest, nullable enabled, warnings-as-errors (except CS1591)
- CommunityToolkit.Mvvm (`[ObservableProperty]`, `[RelayCommand]`)
- `System.Text.Json` source generation — no reflection-based serialization
- Follow the Arwen pattern when adding anything new; don't invent a parallel style
- MahApps Lucide icons, `MithrilDataGrid` / `MithrilQueryBox` for grids
