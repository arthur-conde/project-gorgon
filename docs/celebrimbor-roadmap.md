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

**Why deferred:** The crafted-gear preview that shipped in the `ResultEffects` iteration parses only `TSysCraftedEquipment` — ~63% of all `ResultEffects` usage across `recipes.json`. The remaining 14 prefixes (`BestowRecipeIfNotKnown`, `AddItemTSysPower`, `CraftWaxItem`, calligraphy effects, etc.) cumulatively cover the rest, but each one needs its own UI treatment and the cost/value stays low until users request specific ones.

**Likely approach when revisited:**
- `BestowRecipeIfNotKnown(recipeInternalName)` → "Unlocks: <recipe name>". Pairs naturally with §3 (Recipe prereq chain visualization); bundle there.
- `AddItemTSysPower(template, tier)` → "Augments with <template> · Tier N". Similar render to crafted gear.
- Calligraphy effects and zero-arg prefixes → skip or summarize in a collapsed details area.

### 4. Shareable URL / token formats for craft lists

**Why deferred:** The plain-text paste format already covers the "share this list on Discord" use case; clipboard round-trips losslessly. Any URL-flavored format is a second serialization contract to maintain.

**Likely approach, ordered by cost:**
- (a) Self-contained encoded token: `celebrimbor:v1:<base64-json>`. Compact, paste-only, no OS integration, ~30 extra lines. The right next step if users ask for shorter share strings.
- (b) Real custom URI scheme: `celebrimbor://list?items=Butter:5,Bread:2`. Registered in `HKCU\Software\Classes\celebrimbor`, handled in `Program.cs` single-instance entry, parsed into a craft list on first-instance activation. Needs a security pass — any URL the OS can hand us is untrusted input. Only worth it if we imagine people posting clickable links rather than text.

### 5. ResultEffects-driven output preview

**Why deferred:** Crafted-equipment recipes carry their real output properties (enchant tier, imbue power, etc.) in `ResultEffects` procedural strings like `TSysCraftedEquipment(CraftedWerewolfChest6,0,Werewolf)` that `RecipeEntry` doesn't model. v1's name+icon fallback is visually correct for ~100% of equipment recipes (the recipe's display name matches the item's by authoring convention). Parsing the effects into strongly-typed records is a genuine new feature, not a bug fix. Scope separately when users ask for "which enchant tier am I making" feedback.

**Likely approach:**
- See `memory/celebrimbor_result_effects.md` for the exploration plan: enumerate effect prefixes, taxonomise, decide strongly-typed vs string-parsed, render a crafted-gear summary on the recipe card tooltip.

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
