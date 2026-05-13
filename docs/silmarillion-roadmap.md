# Silmarillion · Reference Browser — roadmap

> **Active backlog:** [Mithril Roadmap Project — `Module: Silmarillion`](https://github.com/users/arthur-conde/projects/3/views/1?filterQuery=module%3A%22Silmarillion%22).

What shipped in v1 and the rationale for which CDN sources will (and won't) become future tabs. Per-item *task tracking* lives in Issues; this doc keeps the design narrative — *why* each piece was scoped this way, and the bucketing rule that decides what becomes a tab.

## Context

Project Gorgon's CDN serves **29 reference-data files** (pg-data MCP, v470). Every file has a faithful POCO parser as of `Mithril.Reference` Phase 0–6 ([mithril-reference-roadmap.md](mithril-reference-roadmap.md)), so the constraint for the browser module is no longer *can we parse it* — it's *is it worth browsing*.

Silmarillion v1 (PR #236, merged 2026-05-13) shipped the two highest-payoff browse surfaces: **Items** and **Recipes**. The architecture is built to fan out:

- [`EntityRef`](../src/Mithril.Shared/Reference/EntityRef.cs) enumerates 11 entity kinds — `Item, Recipe, Ability, Effect, Npc, Quest, Lorebook, Landmark, Area, PlayerTitle, StorageVault` — plus one synthetic deep-link variant (`RecipeIngredientKeyword`, added by #259) used to route keyword chips into a pre-filtered Recipes tab; the synthetic kind isn't a CDN source and isn't part of the Bucket A/B/C/D classification below. Cross-link chips for kinds without a registered `IReferenceKindTarget` render as plain text and silently become navigable the moment that target ships (#239 retired the old `V1TabbedKinds` HashSet).
- The master-detail pattern (`MithrilQueryBox` → virtualised list → detail pane with cross-link chips) is duplicable per tab — adding a tab is `TabItem + View + ViewModel + EntityKind handling + V1TabbedKinds entry`.

This doc is the rationale for which of the remaining 27 CDN sources warrant that duplication.

## What shipped in v1

- **Items tab** — master-detail. Right-pane shows produced-by recipes, consumed-by recipes, and sources (NPCs / quests / monsters / etc.). Cross-link chips degrade to plain text for non-tabbed kinds.
- **Recipes tab** — master-detail. Right-pane shows ingredient chips, result chips, and skill / level metadata. Skill internal names resolve to display names via `IReferenceDataService.Skills`. An "open in window" command supports side-by-side comparison.
- **Reference navigator** — `IReferenceNavigator` + `EntityRef` decouple "open this entity" from view implementation. Back-stack history is unbounded.
- **Result-icon fallback** — recipes with `IconId=0` walk `ResultItems → ProtoResultItems → [0].ItemCode` for a usable icon.

Polish follow-ups filed against v1 (compact rows, right-pane reading-order rework, recipe-sources section, `mithril://` deep-link route) are tracked on the Project board, not enumerated here.

## Bucketing rule

A CDN source earns a tab if it is **an enumerable set of player-recognisable entities** with a stable name and detail-worthy fields. It does **not** earn a tab if it is a numeric lookup table, an engine-internal mechanic, or a sub-table consumed only by another entity's detail view.

Applied to the 29 sources:

| Bucket | Count | Treatment |
|---|---|---|
| A — Already a tab | 2 | `items`, `recipes` |
| B — Should become a tab | 9 | Every remaining `EntityKind` value (see deferred sections below) |
| C — Fold into a Bucket-A or -B tab | 10 | Provider / index / sub-tables that surface as sections, filters, or sidecars within a parent tab |
| D — Never a tab | 8 | Lookup / engine-internal / raw-projection feeds |

Totals: 2 + 9 + 10 + 8 = 29, and Bucket A + Bucket B covers every `EntityKind` value that maps to a CDN source. (`EntityKind` also carries the synthetic `RecipeIngredientKeyword` variant noted above; it's deep-link routing, not a tab.)

## Out of scope for v1 — tabs deferred (Bucket B)

### NPCs

**Why deferred:** Highest cross-link payoff in Bucket B (recipe teachers, item sources, gift preferences via Arwen, quest givers / turn-ins) — but a full NPC card has its own polymorphism (`Services`, `Preferences`, `ItemGifts`) that wasn't in scope for the v1 master-detail spike. v1 prioritised proving the pattern on simpler entities first.

**Likely approach:** Third tab using the same `TabItem + View + ViewModel` shape. Detail pane surfaces Services (Store / Barter / Consignment / Training), gift-preference chips, and back-links to areas. Unblocks the recipe-sources cross-link target (i.e. "this recipe is taught by NPC X" becomes a live link).

### Quests

**Why deferred:** Quest POCOs are the most mature in the codebase (Phase 1 canary: 25 requirement T-values, 9 reward T-values, full validation harness coverage). The deferral is UI-only — no parser obstacle.

**Likely approach:** Card-style tab listing quests; detail pane renders objectives, requirements, and rewards as typed chips. `directedgoals.json` folds in as a filter chip ("guided objectives"), not its own tab.

### Abilities and Effects

**Why deferred together:** Abilities (8.7 MB) and Effects (6.5 MB) are paired — items and abilities already reference effect strings, so neither tab is complete without the other. Both are large enough that the master-detail spike needs a real design pass, not a copy-paste from Recipes.

**Likely approach:** Sequence Abilities first (so Effects' cross-link chips have destinations on both ends), then Effects. Sub-tables (`abilitykeywords`, `abilitydynamicdots`, `abilitydynamicspecialvalues`) fold into the Abilities tab as filter facets and detail sections.

### Areas and Landmarks

**Why deferred:** Geographic browsing is genuinely useful for context (where does this quest happen, where does this NPC live) but lower-payoff than the entity-centric tabs above. Areas and Landmarks share a near-identical card shape, so they're best built as a pair.

**Likely approach:** Areas tab as the parent (geographic root, contextualises NPCs and Quests); Landmarks as a sibling that always cross-links to its parent Area.

### Lorebooks

**Why deferred:** Self-contained narrative content; ideal master-detail shape (list of titles → page body) but no urgent cross-link demand. `lorebookinfo` becomes sidecar metadata in the same tab.

**Likely approach:** Standalone tab with a long-form detail body. The cheapest standalone win once the core entity tabs are in.

### PlayerTitles and StorageVaults

**Why deferred:** Both are completionist / long-tail tabs — small, low-traffic, no blocking dependency. PlayerTitles cross-links from Quests; StorageVaults complements Bilbo's existing inventory view.

**Likely approach:** Ship together when higher-priority tabs are done, or defer if Bucket B scope tightens.

---

## Bucket C — folded into existing tabs (not their own tab)

| Source | Folds into | How it surfaces |
|---|---|---|
| `sources_items` | Items tab | Sources section in item detail (already used in v1) |
| `sources_recipes` | Recipes tab | Sources section in recipe detail (filed as a v1 follow-up) |
| `sources_abilities` | Abilities tab (when shipped) | Sources section in ability detail |
| `itemuses` | Items tab | "Where is this consumed" reverse-lookup section |
| `lorebookinfo` | Lorebooks tab (when shipped) | Title / category metadata sidecar |
| `directedgoals` | Quests tab (when shipped) | Filter chip alongside regular quests |
| `abilitykeywords` | Abilities tab (when shipped) | Filter facets and detail sections |
| `abilitydynamicdots` | Abilities tab (when shipped) | Sub-table in ability detail |
| `abilitydynamicspecialvalues` | Abilities tab (when shipped) | Sub-table in ability detail |
| `skills` | Recipes + Abilities tabs | Filter chips and skill-level metadata; small enough (~30 skills) that a dedicated tab adds little |

These would each require their own master-detail just to render a row count and a few fields — better as enrichment of a parent entity.

## Bucket D — never a tab

| Source | Why not |
|---|---|
| `items_raw` | Raw projection of `items`; internal parser feed only |
| `strings_all` | 15 MB localisation dictionary; pure render-time lookup |
| `xptables` | Numeric tables consumed by skill progression math |
| `advancementtables` | Internal advancement mechanic |
| `attributes` | Low-level effect-attribute primitives consumed by the Effects renderer |
| `ai` | NPC behaviour trees; engine-side, not player-browsable |
| `tsysclientinfo` | TSys augment metadata; calculator-shaped, not card-shaped |
| `tsysprofiles` | TSys profile envelopes; same as above |

`tsysclientinfo` / `tsysprofiles` could plausibly become a "Powers" surface in the future, but the natural shape is a calculator (Celebrimbor's territory), not a card browse. Out of scope for Silmarillion until Celebrimbor migrates per Step 5 of the reference-DB epic — and even then likely as a calculator pane embedded in Celebrimbor, not a Silmarillion tab.

## History

- **2026-05-13** — v1 shipped (PR #236, Items + Recipes tabs, cross-link navigator, master-detail scaffold). Bucketing rationale captured in this doc; per-tab follow-ups filed against `module:silmarillion` on the [Roadmap Project](https://github.com/users/arthur-conde/projects/3/views/1?filterQuery=module%3A%22Silmarillion%22).
- **2026-05-13** — `EntityKind` grew the synthetic `RecipeIngredientKeyword` variant via PR #259 (keyword-collapse design for the item-detail "Used as" section, replacing a naive fan-out widening of `RecipesByIngredientItem`). Sets the precedent for "deep-link to a tab with pre-filled query" as a kind-target shape distinct from "select a row by name."
