# Celebrimbor · Crafting Planner — roadmap

What shipped in v1, and the obvious next steps.

## Context

Celebrimbor is the crafting planner. Users pick recipes and per-recipe quantities in a two-step wizard: **Step 1 — Pick Recipes** (search, filter, add), then **Step 2 — Shopping List**, which shows every item needed across the selection in a crafting-step ladder (raw materials → intermediates → final targets), grouped by item keyword within each step. On-hand counts from the active character's storage export cross-reference each row, a manual override beats detected stock, and progress bars + auto-collapse tell the user how close they are to being able to start crafting.

## What shipped in v1

### Module scaffolding
- `CelebrimborModule` entry point — lazy activation, custom `celebrimbor.ico` wired via `IconUri`, Lucide `Hammer` fallback, `SortOrder 450`, settings pane registered.
- Persistence via `JsonSettingsStore<CelebrimborSettings>` + `SettingsAutoSaver<CelebrimborSettings>` at `%LOCALAPPDATA%\Mithril\Celebrimbor\settings.json`. Settings surface: filter toggles, expansion depth, tooltip delay, craft list, manual on-hand overrides.

### Pure domain / services
- `RecipeAggregator` — target-centric aggregation: the craft-list entries seed the demand dict with each recipe's *output* item (`ResultItems` first, `ProtoResultItems` fallback), so targets, intermediates, and raw materials all flow through a single code path. Expansion depth now reads intuitively (`0` = targets only, `1` = targets + direct ingredients, `N` = full chain to raw). Cycle-safe via a visited-set; shortfall-only expansion respects both detected on-hand and manual overrides; `ChanceToConsume < 1` handled as expected-value math ceiled at display. Computes per-item dependency **depth** (memoised DFS over a producer lookup) for step grouping.
- `OnHandInventoryQuery` — reads `IActiveCharacterService.ActiveStorageContents` and projects to per-item counts + location chips via `StorageReportLoader.NormalizeLocation`.
- `CraftListFormat` — plain-text share format (`RecipeInternalName x Qty`, `#` comments, both `x` and `×` separators). Lenient parse: unknown recipes and invalid quantities produce warnings, never throw. Merge-append sums duplicates.
- `RecipeSearchIndex` — case-insensitive substring over name / internal name / skill, rebuilds on `IReferenceDataService.FileUpdated`.
- Extended `Mithril.Shared.Reference.RecipeEntry` with `ProtoResultItems` (optional tail param, back-compat with Elrond tests). Reference deserialiser populates it. Celebrimbor uses it as the fallback output source for crafted-equipment recipes.

### Picker (Step 1)
- `MithrilDataGrid` with `MithrilQueryBox` — bare substring search *or* expression queries (`IsKnown AND SkillLevelReq < 30`, `Skill = "Cooking"`).
- Per-row **+** button adds a recipe to the craft list; clicking again increments the quantity. One-click-one-add — no selection model, no multi-select toolbar. Cells are `Focusable=False` + a `SelectionChanged → UnselectAll` hook so the system-highlight never paints.
- Rows flagged gold when already in the craft list; a pill in the "In list" column shows the current quantity.
- Two filter toggles (Known only, Meets skill level); degrade gracefully when no active character.
- Rich row tooltip (Elrond-style): icon + name + skill/level header, Ingredients list with `ChanceToConsume %` when present, Yields list, `Known / Unknown` and `Skill met / Under-skilled` badges.
- Clipboard import/export toolbar (Copy list, Paste list with Append/Replace prompt, Clear).
- Bottom-docked **drawer** surfaces the active craft list: one editable-qty chip per recipe with a × remove button, `N recipes · M batches` summary, and the **Finalize** button. The drawer hides when the list is empty.

### Shell / wizard
- Shared `WizardStepIndicator`: two clickable step "pills" (Pick Recipes / Shopping List) with rounded corners, accent-colour active state, disabled until the craft list has content. Chromeless button template — no default WPF button chrome bleeding through.

### Shopping list (Step 2)
- **Making** context anchor at the top: icon + name chips with quantity pills, read-only. Always visible — doubles as intent reminder and tooltip host (shared `RecipeCardTemplate`).
- **Crafting-step ladder** as the main surface: outer groups keyed by dependency depth ("Step 1 · Raw materials", "Step N · Intermediate crafts", "Step N · Ready to craft"), inner groups keyed by item `PrimaryTag`. Each level has its own progress bar (gold for steps, green for tag groups), "Complete" badge, click-to-collapse chevron, auto-collapse on completion with a user-pinned latch so a manual expand persists. Single-group steps hide their inner header (redundant with the step header).
- Item rows: shared column layout (Item / Needed / On hand / pin / Override / Remaining). Override is an always-editable `TextBox` — committing fires `Rebuild()` so raw-ingredient shortfalls update live when the user types a count on an intermediate. Replaced `DataGrid` for the row surface with a plain `ItemsControl` so there's no selection model to fight.
- Location pin column opens a tooltip card listing every storage location holding the item, styled like the Elrond card (sourced from the same `RecipeCardTemplate` idiom).
- Header status reports which character's export is backing the on-hand counts.

### Settings pane
- Toggles for Known Only and Meets Skill Level.
- Numeric input for sub-recipe **Expansion Depth** (0–10).
- Slider + `{N} ms` readout for **Tooltip Delay** (0–2000 ms, default 200). Bound to `ToolTipService.InitialShowDelay` across every Celebrimbor tooltip surface via ancestor-`UserControl` binding.

### Testing
- 21 unit tests in `Celebrimbor.Tests`:
  - 14 cover `RecipeAggregator`: target-as-row semantics at depth 0/1/2, shared ingredients across targets, `ChanceToConsume` expected-value math, cycle termination, missing-ingredient tolerance, `Misc` fallback tag, zero/negative-qty skip, unknown-recipe skip, on-hand + override propagation, override-reduces-intermediate-shortfall.
  - 7 cover `CraftListFormat`: round-trip, comment / blank handling, separator variants, unknown / negative-qty warnings, merge-append semantics.

---

## Out of scope for v1

### 1. Multi-character inventory aggregation

**Why deferred:** Keeps v1 focused on the shortest useful loop. `IActiveCharacterService.ActiveStorageContents` is already parsed and cached; scope grew cleanly from "active character only." Multi-character wants to iterate every export, parse each on a background thread, prefix locations with the character name, and cache by `(path, lastModifiedUtc)`. That's a modest but non-trivial feature with its own reliability surface (file-lock handling, stale-cache invalidation, memory ceiling).

**Likely approach for v2:**
- Extract `IInventoryQueryService` into `Mithril.Shared` — the shared abstraction both Celebrimbor and Bilbo would consume. The service iterates `IActiveCharacterService.StorageReports`, parses each via `StorageReportLoader.Load`, and exposes `QueryByInternalName(string) → IReadOnlyList<(Character, Location, Quantity)>`.
- Gate the background parsing behind `IModuleGate` so it doesn't run before the shell is ready.
- Location chips become `"{Character} · {Normalized Location}"`.
- Bilbo migrates to consume the same service; its `StorageRowMapper` stays where it is but starts pulling from the shared parse cache.

### 2. Shopping-by-source hints

**Why deferred:** Out-of-scope UX work for v1. The useful data exists (`IReferenceDataService.ItemSources`) but surfacing it well needs dedicated design — a popover? an inline chip list? a second tab?

**Likely approach for vNext:**
- Expand each ingredient row with a details-pane / flyout showing `ItemSources[item.InternalName]` grouped by source type (Vendor / Drop / Gather / Craft / Quest).
- Default: hide Quest and one-off sources; let the user opt in.
- Combine with multi-character inventory: prefer "you have it here" over "NPC X sells it."

### 3. Recipe prereq chain visualization

**Why deferred:** Not core to the shopping-list goal; users who already have the list in hand have committed to the recipes.

**Likely approach for vNext:**
- Resolve `RecipeEntry.PrereqRecipe` transitively into a chain.
- Render a mini-chain in the picker's row detail pane on hover/select.
- Flag "you cannot learn this yet" when an upstream prereq is missing from `CharacterSnapshot.RecipeCompletions`.

### 3a. Remaining `ResultEffects` prefixes

**Status:** Phase 7 (April 2026) closed the high-volume prefixes — `TSysCraftedEquipment` (1,645 effects), `BestowRecipeIfNotKnown` (215), `CraftWaxItem` (72), `ExtractTSysPower` (36) and the `TSysCraftedEquipment` enchantment-pool sibling, `GiveTSysItem` (58) and `CraftSimpleTSysItem` (37, both folded into `CraftedGearPreview`), `DispelCalligraphyA/B/C` (126), `CalligraphyComboNN` (85), and `MeditationWithDaily` (70). Combined with `AddItemTSysPower` (92, shipped earlier), the parser now covers ~2,436 of the 3,174 effect strings (~77%).

**Out-of-scope-for-v1 stance dropped — every effect is in scope.** Per the roadmap as of April 2026, we no longer treat unhandled prefixes as "skip or summarize in a collapsed details area"; each one will eventually parse into either a typed preview (when it produces a meaningful item / augment / unlock) or an `EffectTagPreview` (humanized tag line) so a recipe never silently drops one of its outcomes. The remaining ~738 effects are listed below by family. Source: `recipes.json` audit, April 2026.

#### TSys-augment family (~52 effects)
- `AddItemTSysPowerWax(template, tier, durability)` — **19**. Sibling of `AddItemTSysPower` for finite-use wax items. Should render through a new `WaxAugmentPreview` (or extend `WaxItemPreview` with a kind discriminator).
- `ApplyAugmentOil` — **24**. Zero-arg behavioural tag. Renders as `EffectTagPreview("Applies augment oil")`.
- `RemoveAddedTSysPowerFromItem` — **9**. Zero-arg tag.
- `ApplyAddItemTSysPowerWaxFromSourceItem` — **5**. Variant tag.
- `DecomposeMainHandItemIntoAugmentResources` and slot siblings (Off-hand / Hands / Chest / Leg / Helm / Feet / Ring / Necklace) — **9 total**. Slot-flavoured decomposition tags.

#### Calligraphy / Meditation / Whittling tag variants (~150 effects)
Same shape as the prefixes already in `ParseEffectTags` — short prefix + small numeric/letter suffix — and should fold in via a single regex extension rather than one method per family:
- Calligraphy: `CalligraphyArmorRepair{N}` (3), `CalligraphyRage{N}` (7), `CalligraphySlash{N}` (16), `CalligraphyFirstAid{N}` (10), `CalligraphyPiercing{N}` (2), `CalligraphySlashingFlat{N}` (1), `Calligraphy{tier}{slot}` (17), `CalligraphyFirstAid` (10).
- Meditation: `MeditationHealth{N}` (6), `MeditationPower{N}` (6), `MeditationVulnPsi{N}` (6), `MeditationBreath{N}` (4), `MeditationVulnCold/Fire/Darkness/Nature/Electricity{N}` (6 total), `MeditationCritDmg{N}` (1), `MeditationIndirect{N}` (1), `MeditationBuffIndirectCold{N}` (1), `MeditationDeathAvoidance{N}` (1), `MeditationBodyHeat{N}` (1), `MeditationMetabolism{N}` (1), `MeditationNoDaily` (1).
- Whittling: `Whittling{N}` (10), `WhittlingKnifeBuff{N}` (10).

#### Item-producing effects (~80 effects)
These genuinely yield items / surveys / maps and deserve typed-preview shapes parallel to `CraftedGearPreview`:
- `BrewItem(spec)` — **40**. Brewing recipes producing a parameterized brewed item.
- `CreateMiningSurvey{N}X(name)` — **18**. Mining survey items with parameterized region.
- `CreateGeologySurvey{Color}{Region?}(name)` — **13** total (Blue/Green/White/Orange/Redwall × Serbule/Povus/Vidaria). Same shape; can collapse into one parser.
- `CreateXxxTreasureMap{Quality}` — **12** total (Eltibule / Ilmari / SunVale × Poor / Good / Great / Amazing).
- `SummonPlant(spec)` — **17**.
- `GiveNonMagicalLootProfile(profile)` — **1**.
- `CreateNecroFuel` — **2**.

#### Equipment-property effects (~57 effects)
- `BoostItemEquipAdvancementTable(table)` — **37**. The crafted item, when equipped, grants permanent skill XP toward `table`. Worth a typed preview (`EquipBonusPreview` or similar).
- `CraftingEnhanceItemArmor(N, M)` — **10**.
- `CraftingEnhanceItemPockets(N, M)` — **8**.
- `CraftingEnhanceItem{Element}Mod(scalar, M)` — **12** total (Fire / Cold / Electricity / Psychic / Nature / Darkness).
- `CraftingResetItem` — **4**.
- `RepairItemDurability(spec)` — **8**.
- `TransmogItemAppearance` — **1**.

#### Knowledge / progression effects (~120 effects)
- `GiveTeleportationXp` — **46**. Tag-style.
- `Research{Topic}{N}` — **78** total (`ResearchWeatherWitching` 24, `ResearchFireMagic` 24, `ResearchIceMagic` 24, `ResearchExoticFireWalls` 6).
- `DiscoverWordOfPower{N}` — **6**.
- `LearnAbility(name)` — **1**.
- `HoplologyStudy` — **1**.

#### Recipe-system effects (~36 effects)
- `AdjustRecipeReuseTime(deltaSeconds, condition)` — **36**. Modifies a recipe's cooldown. Should render the delta human-readable ("Reduces cooldown by 24h on Quarter Moon").

#### Behavioural / location / cosmetic long tail (~240 effects)
Bucket into a generic "Effect: humanized prefix" fallback in `ParseEffectTags` so we never silently drop a recipe outcome. Notable members:
- Mushroom-circle suite: `SaveCurrentMushroomCircle` (18), `TeleportToBoundMushroomCircle{N}` (19), `TeleportToLastUsedMushroomCircle` (18), `BindToMushroomCircle{N}` (2), `TeleportToNearbyMushroomCircle` (1).
- Teleport-circle suite: `SaveCurrentTeleportCircle` (5), `TeleportToBoundTeleportCircle{N}` (4), `BindToTeleportCircle{N}` (2), `TeleportToLastUsedTeleportSpot` (1), `TeleportToSpontaneousSpot` (1), `TeleportToMostCommonSpot` (1), `TeleportToGuildHall` (1), `TeleportToEntrypoint` (3), `Teleport(area, name)` (3), `SpawnPlayerPortal{N}` (2), `StoragePortal{N}` (3).
- Decomposition: `DecomposeItemIntoPhlogiston` (15), `DecomposeItemIntoCrystalIce` (3), `DecomposeFoodIntoCrystalIce_*` (1), `DecomposeItemIntoFairyDust` (1), `DecomposeDemonOreIntoEssence` (1).
- Calligraphy-system specials: `Calligraphy{tier}{slot}` (17, see above), `CalligraphySlash{N}` (16, see above), `CalligraphyFirstAid` (10, see above), `Calligraphy*` long tail (`CalligraphyPiercing` 2, `CalligraphySlashingFlat` 1).
- Fairy-light / Fairy-energy: `DispelFairyLight` (1), `SummonFairyLight` (1), `DeltaCurFairyEnergy(deltaInt)` (12).
- Tempest: `PermanentlyRaiseMaxTempestEnergy(N)` (13).
- Plant / nectar / drink: `DrinkNectar` (1), `DeployBeerBarrel` (1), `ConsumeItemUses(template, N)` (1).
- Polymorph / cosmetic: `PolymorphRabbitPermanentBlue/Purple` (2), `CraftingDyeItem` (12), `HairCleaner` (1).
- Survey / divination / metadata: `MoonPhaseCheck` (1), `WeatherReport` (1), `Augury{N}` (4), `ShowWardenEvents` (1), `SpawnPremonition_*` (3), `DispelSpawnPremonitionsOnDeath` (3), `Particle_{kind}` (4), `HelpMsg_{topic}` (17).
- Misc fishing / utility: `CheckForBonusFishScales` (5), `CheckForBonusPerfectCotton` (4), `SendItemToSaddlebag` (1), `ApplyRacingRibbonToReins` (1), `SummonStatehelm` (1), `SummonPovusPaleomonster` (1), `StorageCrateDruid12Items` (2).
- Status effects: `Infertility` (1), `SleepResistance` (1), `SexualEnergy` (1), `ArgumentResistance` (1).

**Suggested implementation order:**
1. **TSys-augment family** — `AddItemTSysPowerWax` first (closes the last big augment-shape gap), then the zero-arg tags. Bundle with the §5 typed-preview pipeline.
2. **Calligraphy / Meditation / Whittling tag variants** — single regex extension to `ParseEffectTags`. High coverage, low risk.
3. **Equipment-property effects** — `BoostItemEquipAdvancementTable` is the highest-value entry; the rest are crafting-enhance siblings that share a render shape.
4. **Item-producing effects** — `BrewItem`, surveys, treasure maps. Each gets a small typed preview.
5. **`AdjustRecipeReuseTime`** — single-prefix parser, render as a humanized delta.
6. **Generic fallback** — extend `ParseEffectTags` with a "humanize unknown prefix" path so the long tail produces *some* preview rather than silently dropping. Filter out a small allow-list of intentionally-silent prefixes (e.g. `Particle_*` is internal display-only) at the same time.

### 3b. Ingredient `ItemKeys` (keyword-matched ingredients)

**Status:** Not parsed today. Current `RawRecipeItem` only reads `ItemCode`; recipes that specify `{ "ItemKeys": ["Crystal"], "StackSize": 1 }` (e.g. the auxiliary-crystal slot on every `*E` enchanted recipe) silently drop that ingredient at parse time, so the user sees an incomplete ingredient list.

**Why it matters:** Every `TSysCraftedEquipment` enchanted recipe (1,645 of them) has at least one keyword-matched ingredient. The shopping list is currently undercounting.

**Likely approach:**
- Extend `RawRecipeItem` to also expose `Desc` and `ItemKeys`.
- Surface a new `RecipeItemRef` variant that carries the keyword list instead of an item code.
- Aggregator-side: when expanding the demand, treat keyword-matched rows as "any item keyworded `<X>` covers this slot." The on-hand resolver then has to scan inventory for any matching item rather than a single item code.
- Tooltip / picker render: `"1× <Desc> (any Crystal)"` instead of dropping the row.

### 4. Shareable URL / token formats for craft lists

**Why deferred:** The plain-text paste format already covers the "share this list on Discord" use case; clipboard round-trips losslessly. Any URL-flavored format is a second serialization contract to maintain.

**Likely approach, ordered by cost:**
- (a) Self-contained encoded token: `celebrimbor:v1:<base64-json>`. Compact, paste-only, no OS integration, ~30 extra lines. The right next step if users ask for shorter share strings.
- (b) Real custom URI scheme: `celebrimbor://list?items=Butter:5,Bread:2`. Registered in `HKCU\Software\Classes\celebrimbor`, handled in `Program.cs` single-instance entry, parsed into a craft list on first-instance activation. Needs a security pass — any URL the OS can hand us is untrusted input. Only worth it if we imagine people posting clickable links rather than text.

### 5. ResultEffects-driven output preview

**Status: shipped.** Phase 6 (April 2026) introduced the strongly-typed preview pipeline (`AugmentPreview` for `AddItemTSysPower` plus the `EffectDescsRenderer` that resolves `{TOKEN}{value}` placeholders against `attributes.json`). Phase 7 extended it with `CraftedGearPreview` (the `TSysCraftedEquipment` / `GiveTSysItem` / `CraftSimpleTSysItem` chip), `TaughtRecipePreview` (`BestowRecipeIfNotKnown`), `WaxItemPreview` (`CraftWaxItem`), `AugmentPoolPreview` + the dedicated `AugmentPoolView` (the `TSysCraftedEquipment` enchantment-pool sibling and `ExtractTSysPower`), and `EffectTagPreview` for the calligraphy / meditation tag families. Every typed preview renders inline on the recipe card and tooltip; clicking through opens `ItemDetailWindow` with the full per-effect breakdown.

The eligibility model the pool viewer's pre-fill query uses (gear-level bracket × rolled-rarity floor × form/skill gate) is documented in [treasure-system.md](treasure-system.md). What's still open is purely the long-tail prefix coverage tracked in §3a — the preview pipeline itself is in place and the remaining work is "add another `Parse*` method and a render template."

### 6. Tighter persistence of UI state

**Why deferred:** v1 persists the craft list, overrides, filter toggles, expansion depth, and tooltip delay. It does not persist DataGrid column layout, expanded-state of step / group headers across sessions, or recent search queries.

**Likely approach for vNext:**
- Wire `DataGridStateBinder.Bind(...)` on the picker grid for column-width and sort persistence.
- Persist `IsExpanded` per group / step so the user's mental model of what's "done" survives restarts.
- Recent search queries — drop-down history on the query box.

### 7. Variable-yield planning

**Why deferred:** `RecipeItemRef` currently exposes `ChanceToConsume` only; there's no `PercentChance` on result items in the schema. If the schema grows to include bonus-output probabilities (certain recipes do produce bonus copies at low probability), the aggregator's expected-value pipeline is the right slot.

**Likely approach if the schema grows:**
- Model `RecipeItemRef.PercentChance` for result rows.
- Add an "assume bonus yield" settings toggle that divides planned batch count by `1 + Σ percentChance * yieldMultiplier` when enabled (off by default — pessimistic planning avoids unhappy surprises).
- Surface the raw chance in a tooltip regardless of toggle state.
