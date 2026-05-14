# Silmarillion: NPCs tab (first Bucket B — third tab)

**Tracked in:** #241. Reopened today after #258 closed it administratively without shipping the tab itself.

> **Read first:** [silmarillion-tab-cookbook.md](../silmarillion-tab-cookbook.md). It owns the scaffolding (file layout, DI registration, `IReferenceKindTarget` shape, `ModuleTab` wiring, `SchemaSnapshot` for autocomplete, test trio, verification ladder). This handoff covers only NPC-specific decisions.

## Context

NPCs are the highest cross-link payoff in Bucket B (recipe teachers, item sources, gift preferences via Arwen, quest givers / turn-ins) but the v1 master-detail spike deferred them because the entity carries real polymorphism — `Services`, `Preferences`, `ItemGifts`. PR #258 added recipe-detail "Taught by" chips that already point at `EntityRef.Npc(...)`; those chips currently degrade to plain text. Shipping this tab makes them navigable and validates the cookbook end-to-end as the first non-v1 tab.

## Service-layer decision required up front

`IReferenceDataService.Npcs` is currently `IReadOnlyDictionary<string, NpcEntry>` ([IReferenceDataService.cs:90](../../src/Mithril.Shared/Reference/IReferenceDataService.cs#L90)) — a thin projection consumed by Arwen for gift preferences. The NPCs tab needs the full [`Mithril.Reference.Models.Npcs.Npc`](../../src/Mithril.Reference/Models/Npcs/Npc.cs) POCO (Services, ItemGifts, Pos, AreaFriendlyName, etc.). Two paths:

1. **Add a sibling `IReadOnlyDictionary<string, Npc> NpcsByInternalName { get; }`** on `IReferenceDataService` (or similarly-named) that exposes the full POCO. Leaves Arwen on `NpcEntry` untouched. Smallest blast radius. **Recommended for this PR.**
2. **Migrate `Npcs` to be `IReadOnlyDictionary<string, Npc>` directly** and delete `NpcEntry`, mirroring the path Items took in #209. Cleaner long-term but touches Arwen and any other consumer + test fakes. Defer to a follow-up issue if you take this path; don't bundle into the tab PR.

Either way, populate the dictionary inside `ParseAndSwapNpcs` (or equivalent) — `Mithril.Reference.Models.Npcs.Npc` is already deserialised by `ReferenceDeserializer.ParseNpcs`, so this is a projection wiring change, not parser work.

## Master-list row

Use a `NpcListRow` projection wrapper (mirror [`RecipeListRow`](../../src/Silmarillion.Module/ViewModels/RecipeListRow.cs)). Raw `Npc` lacks an `InternalName` field (the JSON envelope key is the internal name) and needs cross-cuts:

- `InternalName` — copied from the `Npcs` dictionary key
- `Name` — `Npc.Name ?? InternalName`
- `AreaDisplayName` — `Npc.AreaFriendlyName ?? Npc.AreaName ?? "(unknown)"`
- `ServiceTypes` — `IReadOnlyList<IngredientKeywordValue>`-style tag set, one per `NpcService.T` value (Store / Barter / Consignment / Training / Stables / …) for `CONTAINS` filtering
- Optional sort key for stable row ordering (likely `Name` ASC)

Expose the reflected `SchemaSnapshot` from `typeof(NpcListRow)` per cookbook step 6.

## Filter facets

The query box, plus any chip-style row filters, should cover at minimum:

- `ServiceTypes CONTAINS "Store"` (and the other service kinds)
- `AreaDisplayName = "Serbule"` and substring match
- `Name CONTAINS "Joeh"` for partial-name lookup

If you want a quick-filter chip strip above the list (analogous to recipe-detail's keyword chips), add it but keep it composable with the query box — chips should mutate the same `QueryText` state, not a parallel filter model.

## Detail-pane reading order

Per the #234 reading-order convention, lead with cross-link sections, push tail metadata to the bottom:

1. **Header** — name + area chip (clickable once Areas tab ships, plain text otherwise) + position string if non-null
2. **Services** — one block per `NpcService`, grouped by `T`. Store/Barter blocks should hint at the in-game implications (e.g. "Sells items", "Buys items at favor X+"). Subordinate `CapIncreases`, `Keywords`, `MinFavorTier` from each service render as sub-rows or chips.
3. **Teaches recipes** — reverse lookup over `IReferenceDataService.RecipeSources` filtered to entries with `Npc == this.InternalName` and `Type == "Training"`. Recipe chips are navigable today.
4. **Sells items** — reverse lookup over `IReferenceDataService.ItemSources` filtered the same way (`Type == "Vendor"`, `Npc == this.InternalName`). Item chips are navigable today.
5. **Quests given / turned in** — degrades to plain text until Quests tab ships. Source: scan `IReferenceDataService.Quests` for entries whose giver / turn-in NPC matches. May or may not need a reverse-lookup primitive on the service — see plumbing section below.
6. **Gift preferences** — render `Npc.Preferences` (sentiment-tier thresholds + gift category chips) and `Npc.ItemGifts` (the sentiment-threshold list). Cross-check Arwen's existing rendering of this data so the same vocabulary is used.
7. **Description** — `Npc.Desc` if present, free-form text
8. **Footer** — internal-name (mono, bottom-right) per the [detail-view internal-name footer convention](../../docs/silmarillion-tab-cookbook.md) memory item

Use the detail-pane hide-when-empty convention — every section above except header collapses cleanly when the source data is null/empty. Empty NPCs (altars, pedestals with no services) should render as just header + footer with no dead zones.

## Cross-link plumbing

Reverse lookups needed on `IReferenceDataService` if not already present:

- `IReadOnlyDictionary<string npcInternalName, IReadOnlyList<Recipe>> RecipesTaughtByNpc` — derived from `RecipeSources` during refresh; cache to avoid per-selection scans.
- `IReadOnlyDictionary<string npcInternalName, IReadOnlyList<Item>> ItemsSoldByNpc` — derived from `ItemSources` filtered to `Type == "Vendor"`. Same cache pattern.
- *(Optional)* `IReadOnlyDictionary<string npcInternalName, IReadOnlyList<Quest>> QuestsByNpc` — only if quest cross-links land in this PR; otherwise scan `Quests` lazily inside the detail VM and defer the index to the Quests tab handoff.

Build all three in `BuildRecipeCrossLinkIndices`-adjacent code paths in `ReferenceDataService` so they get rebuilt on items / recipes / npcs / quests refresh as appropriate.

## Cross-link chip kinds (degradation matrix)

Per cookbook: `IsNavigable = _navigator.CanOpen(reference)` decides per-chip; you don't gate manually. State today:

| Chip target | Today | Ships when |
|---|---|---|
| `EntityRef.Item(...)` | navigable | already shipped |
| `EntityRef.Recipe(...)` | navigable | already shipped |
| `EntityRef.Area(...)` | plain text | #245 |
| `EntityRef.Quest(...)` | plain text | #242 |
| `EntityRef.PlayerTitle(...)` | plain text | #248 |

No new synthetic kinds needed for NPCs. The synthetic-kinds pattern in the cookbook is for high-cardinality or query-shaped chips; NPC cross-links are bounded enough that direct-ref chips suffice.

## Test fixtures

Beyond the standard test trio called out in the cookbook:

- **`StubReferenceData`** in `tests/Silmarillion.Tests/` needs `NpcsByInternalName` (or whatever name the new service property gets) and the two reverse-lookup dictionaries.
- **A representative NPC fixture** with at least one Service of each common type (Store, Barter, Training), one Preference entry, one ItemGift threshold, a non-null `Desc`. Reuse for both `NpcsTabViewModelTests` and `NpcsKindTargetTests`.
- **An empty NPC fixture** (altar / pedestal with `Services = null`, `Preferences = null`, `ItemGifts = null`) to assert the detail VM renders header + footer only and the section-hide convention works.
- **A reverse-lookup integration test** in `tests/Mithril.Shared.Tests/ReferenceDataServiceTests.cs` — fixture an NPC + a Recipe whose `RecipeSources` entry teaches it, assert `RecipesTaughtByNpc[npcInternalName]` returns the recipe.

Use `NavFactory.WithKinds(EntityKind.Npc, EntityKind.Item, EntityKind.Recipe)` to assert chip navigability — area / quest / title chips should report `IsNavigable=false` until those tabs land.

## Verification (delta from the cookbook ladder)

Run the standard cookbook ladder, plus:

- **End-to-end cross-link round-trip:** open a recipe with a known NPC teacher (e.g. anything from a Store-type vendor), click the "Taught by Joeh" chip → NPCs tab opens, Joeh selected, Joeh's "Teaches recipes" section lists the recipe you came from. Click that recipe chip → returns to the original recipe detail. The back/forward buttons should walk this history correctly.
- **Empty-NPC smoke:** select an altar (e.g. anything in `Altar_*` keyspace) — confirm only header + footer render, no empty section headers.
- **Arwen non-regression:** open Arwen's favor view, switch characters, confirm gift preferences still render. (`NpcEntry` consumers shouldn't have moved.)

## Out of scope

- The full `NpcEntry` → `Npc` migration in path 2 above. File a follow-up if desired.
- An "NPC location map" pin / overlay using `Pos`. Defer until Areas tab ships and a map surface exists somewhere.
- Faction or merchant-stock detail beyond what `npcs.json` carries directly. The "Sells items" section already pulls from `sources_items` which is the canonical answer; don't duplicate by parsing in-game vendor logs.
- Localization of service-type / sentiment-threshold strings — out of scope per #265's deferral.

## Commit / PR shape

Single PR against `main`. Suggested branch: `feat/241-silmarillion-npcs-tab`. Conventional commit:

```
feat(silmarillion): NPCs tab (first Bucket B) — #241
```

Likely diff size: ~600–900 lines across service-layer plumbing (~150), tab VM + view (~300), kind target (~50), tests (~250), and possibly a bundled `npcs.json` if not already bundled.

Closes #241. Does **not** close any other Bucket B issue — but does light up the navigability of NPC chips already shipping in recipe details (#235's "Taught by" section).
