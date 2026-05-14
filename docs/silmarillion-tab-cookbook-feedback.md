# Silmarillion · Tab Cookbook — Feedback from the NPCs Tab Shipment (#241)

Notes from shipping the first Bucket B tab against the cookbook. Each entry is something a future agent shipping a Quests / Areas / Lorebooks tab would benefit from having spelled out before they start. Items are ordered by how much friction they caused, not where they belong in the cookbook.

## 1. Audit existing chip-builders for hardcoded `IsNavigable: false`

**Friction:** Two surfaces — the Item-detail "Sources" section and the Recipe-detail "Taught by" section — already produced `ItemSourceChipVm` instances anchored to `EntityRef.Npc(...)`. The recipe side correctly set `IsNavigable = _navigator.CanOpen(reference)`, so it lit up the moment the NPC kind shipped. The item side, however, hardcoded `EntityReference: null, IsNavigable: false` from before the chip-VM type even had an EntityReference field. The chip stayed plain-text post-#241 and required a follow-up to fix.

**Cookbook addition:** when shipping a new `EntityKind`, grep the codebase for existing chip-builders that emit `EntityRef.<NewKind>(...)` or `ItemSourceChipVm(..., EntityReference: null, IsNavigable: false)` shapes. Any that match are stale and won't auto-flip when `CanOpen` starts returning true. Treat the audit as part of the "Cross-link chips" section.

The cookbook's *Don't gate on the kind manually — let `CanOpen` decide* rule is right, but it only protects the chips that follow the rule. Pre-existing chip-builders that violated it are invisible to the new author.

## 2. Cross-link chip vocabulary needs both `EntityChip` AND `ItemSourceChip`

**Friction:** The cookbook's "Cross-link chips" section says to use `EntityChip` for entity references and `ItemSourceChipVm` for source-style rows where the anchor may not exist as a clickable target. It does *not* mention how `ItemSourceChipVm` gets rendered — pre-#241 it was a plain `TextBlock` in every consuming XAML, gated by a `TODO(stub:#207)` comment. The cookbook reader is left to discover that the chip control was never built.

**Cookbook addition:** add a parallel paragraph about `ItemSourceChip` (the UserControl now in `Mithril.Shared.Wpf`) alongside `EntityChip`. Make explicit that source-style sections render through `ItemSourceChip` with `ClickCommand="{Binding DataContext.OpenEntityCommand, RelativeSource={...}}"` — same shape as `EntityChip`, the chip auto-degrades to plain text in a transparent frame when `IsNavigable=false`.

## 3. Display-name resolution belongs in a single helper, written once

**Friction:** `Vendor: NPC_Joeh` shipped with the raw envelope key on the chip. The Items tab uses `item.Name`, the Recipes tab uses `recipe.Name ?? r.InternalName ?? r.Key`. For NPCs there was no canonical helper, and source-chip builders on *two* other tabs (Items and Recipes) both needed one. The first patch resolved through `_refData.NpcsByInternalName[s.Npc]?.Name ?? s.Npc` inline in each VM, but the fallback ("strip `NPC_` prefix") was wanted in three places — eventually a `NpcNameResolver` static was factored.

**Cookbook addition:** when shipping a new entity kind, factor a `<Kind>NameResolver.Resolve(refData, internalName)` static **before** writing the tab VM. Use it everywhere the kind's friendly name surfaces — master list, detail header, and any pre-existing chip-builder on a different tab that references this kind's internal name. The cost is one extra file and saves three rewrites later.

## 4. `NpcEntry` vs `Npc` POCO split should be a cookbook decision tree, not handoff prose

**Friction:** Path 1 (sibling property `NpcsByInternalName` exposing the full POCO) vs path 2 (migrate `Npcs` outright) was a real upfront decision that needed explicit framing in the handoff. Future Bucket B handoffs may face the same — `QuestEntry` vs `Quest`, `AreaEntry` vs `Area`. Today's slim projections are consumed by other modules (Arwen, Smaug); the new tab needs the full POCO.

**Cookbook addition:** the "What your handoff still owns" section currently says "Which `IReferenceDataService` source dictionary to read." Strengthen this with a sub-bullet: *"If a slim `*Entry` projection already exists for that kind (check `IReferenceDataService` properties), decide explicitly whether to add a sibling `*ByInternalName` property exposing the full POCO (path 1, smallest blast radius) or migrate the existing projection (path 2, defer to a follow-up issue). Default to path 1 unless the tab is the only consumer."*

## 5. Polymorphic-entity rendering needs a real-data sanity check before shipping

**Friction:** The NPC POCO has six subclasses of `NpcService` (Store, Training, Barter, Storage, Consignment, InstallAugments, …). My first pass collapsed each subclass's payload via `Concat(left, right)` — e.g. `Concat(training.Skills, training.Unlocks)` — and rendered the result as one undifferentiated bullet list. Synthetic test fixtures with simple data didn't catch how confusing it reads against real game NPCs whose `Training` row showed `["Unarmed", "Lore", "Neutral", "Comfortable", "Friends", "CloseFriends", "BestFriends"]` — skill names visually indistinguishable from favor-tier unlocks.

**Cookbook addition:** for polymorphic kinds, the verification ladder should include a "walk the bundled JSON for 2-3 real examples before shipping the detail VM, and confirm each polymorphic subclass renders legibly with real data, not just unit-test fixtures." Synthetic tests verify projection correctness; only real data exposes whether the *grouping* is parseable.

## 6. "Default values that render as noise" — common with PG data

**Friction:** Every `NpcService` in `npcs.json` carries `Favor = "Despised"` (the lowest favor tier, i.e. "anyone can access"). Rendering `Favor: Despised` on every service row was pure noise. The fix was to null it out in the VM for the default case so the XAML hides the chip.

**Cookbook addition:** specific to this codebase but broadly applicable — when a chip / badge surfaces an enum-like value, identify whether one value is the *default* and treat it as null in the projection. Common offenders: `Despised` (favor), zero-valued numeric thresholds, default-empty arrays. The verification ladder could flag this: *"Confirm every persistent chip on the detail pane carries information — if it'd say the same thing for every row, fold it into the projection."*

## 7. Name collisions between `Mithril.Reference.Models.*` and `Mithril.Shared.Reference.*`

**Friction:** `NpcService` exists in both `Mithril.Reference.Models.Npcs` (full POCO) and `Mithril.Shared.Reference` (slim record consumed by Arwen). Same with `NpcPreference`. Required `using NpcServicePoco = Mithril.Reference.Models.Npcs.NpcService;` aliases in two files (VM + tests).

**Cookbook addition:** mention this gotcha in the "Which source dictionary to read" section. Heads-up: *"For kinds with both a POCO and a slim projection (NPCs, possibly Quests/Areas in the future), expect to alias `using <Kind>Poco = Mithril.Reference.Models.<Kind>s.<Kind>;` in the tab VM and tests to disambiguate at every reference site."*

## 8. Constructor changes to `SilmarillionViewModel` ripple to navigator tests

**Friction:** Adding `NpcsTabViewModel npcs` to the `SilmarillionViewModel` constructor broke not only `SilmarillionViewModelTests` (5 sites of `items: null!, recipes: null!`) but also `SilmarillionReferenceNavigatorTests` (5 more sites). The cookbook's scaffolding checklist mentions "add a constructor parameter `XTabViewModel x` and a public `X { get; }` property" without flagging that this ripples into two test files and the named-argument calls there.

**Cookbook addition:** in the scaffolding checklist's step 3, add a sub-bullet: *"This will ripple to `SilmarillionViewModelTests` and `SilmarillionReferenceNavigatorTests` — both currently call `new SilmarillionViewModel(items: null!, recipes: null!, ...)`. Update each `null!` site to add the new positional argument. Use named arguments throughout to keep the diff readable."*

## 9. The "two things that go wrong" list missed a third

**Friction:** The cookbook's "Two things that go wrong" section calls out (a) tab VM registered without kind target, and (b) lookup against `IReferenceDataService` instead of bound collection. A third snuck through this iteration:

**(c) The new kind's tab ships, but pre-existing chip-builders on *other* tabs are stale.** Items tab's `BuildSourceChips` and the section XAMLs were both stale w.r.t. the new kind. The kind target was registered, the lookup was correct, and *new* chip-builders auto-degrade — but pre-existing surfaces don't get the upgrade for free.

**Cookbook addition:** add this as a third entry. Symptom: clicking a chip on a tab *other* than the one being shipped doesn't navigate. Cause: a chip-builder elsewhere hardcoded `IsNavigable: false` or rendered through a plain `TextBlock` instead of `EntityChip` / `ItemSourceChip`. Fix: grep for the new EntityKind's name across all consumers; replace hardcoded falsy flags with `_navigator.CanOpen(reference)` and upgrade `TextBlock` source rows to `<c:ItemSourceChip .../>`.

## 10. Build file-lock when Mithril is running for smoke

**Friction:** During iterative manual-smoke + fix cycles, Mithril runs while I rebuild. MSB3026/27 cascade locks every module's copy-to-`modules/` step. This is documented in `mithril_build_file_lock_silent.md` as a memory item, but the cookbook's verification ladder doesn't mention it.

**Cookbook addition:** in step 4 of the verification ladder, note: *"For iterative fix cycles during manual smoke, close Mithril between rebuilds — the post-build copy-to-`Mithril.Shell/modules/` cascades to MSB3027 file-lock errors when the running app holds the module DLL open. Symptom: a clean build of the Module fails 10× retries before erroring; the test DLL builds cleanly because tests don't copy to `modules/`."*

## 11. Reverse-lookup rebuild plumbing pattern is implicit

**Friction:** The cookbook talks about reverse-lookup indices as a handoff concern ("Reverse-lookup plumbing on `IReferenceDataService` if the detail pane needs new cross-links") but doesn't show the *rebuild trigger* pattern. For NPCs, the indices `RecipesTaughtByNpc` and `ItemsSoldByNpc` derive from `_recipeSources` + `_recipesByInternalName` and `_itemSources` + `_itemsByInternalName` respectively. The rebuild needs to fire on parse-and-swap of *any* of those four files. Mirror of `BuildRecipeCrossLinkIndices` which already does this for items+recipes.

**Cookbook addition:** the "Cross-link plumbing" sub-section should reference `BuildRecipeCrossLinkIndices` and `BuildNpcCrossLinkIndices` as the template, and explicitly list the trigger sites: *"For each input dictionary the index depends on, call your `Build<X>CrossLinkIndices()` at the end of that file's `ParseAndSwap*` method. Files-input matrix:*

| Index | Triggers rebuild from |
| --- | --- |
| `RecipesByProducedItem`, `RecipesByIngredientItem` | items.json, recipes.json |
| `RecipesTaughtByNpc` | recipes.json, sources_recipes.json |
| `ItemsSoldByNpc` | items.json, sources_items.json |

## 12. Default `IReadOnlyDictionary<,>` for new `IReferenceDataService` properties

**Friction:** Adding `NpcsByInternalName`, `RecipesTaughtByNpc`, `ItemsSoldByNpc` to `IReferenceDataService` would normally cascade across every test fake (Bilbo, Arwen, Smaug, Celebrimbor, ...). The cookbook's "Test scaffolding" section says *"Extend `StubReferenceData` with new source dictionaries as needed; consumer fakes in other modules use the interface default for any property they don't care about"* — this is the rule, and it worked, but the *pattern* (declaring a private static empty default + `=> EmptyXMap` on the interface declaration) deserves a one-line example.

**Cookbook addition:** in the test scaffolding section, show the interface-default pattern with a one-line example: *"Default-implement every new `IReferenceDataService` property to an empty dictionary so test fakes in other modules don't need to opt in. Pattern:*

```csharp
IReadOnlyDictionary<string, Npc> NpcsByInternalName => EmptyNpcMap;
private static readonly IReadOnlyDictionary<string, Npc> EmptyNpcMap
    = new Dictionary<string, Npc>(StringComparer.Ordinal);
```

## Summary — proposed cookbook delta

The bigger restructure suggested by this list:
- Split "Cross-link chips" into two sub-sections: **entity-anchored** (`EntityChip` / `EntityChipVm`) and **source-anchored** (`ItemSourceChip` / `ItemSourceChipVm`).
- Add an **"Audit existing surfaces"** sub-section to "What your handoff still owns" — a checklist of grep targets when shipping a new `EntityKind`.
- Add a **"Real-data sanity check"** rung to the verification ladder before "manual checks", calling for a 2-3 NPC walkthrough of polymorphic shapes.
- Promote the `<Kind>NameResolver` pattern to a first-class cookbook step alongside the master-list row projection.

Items 1, 3, and 9 are the highest-friction items — addressing them in the cookbook would have saved the bulk of the post-smoke iteration cycle.
