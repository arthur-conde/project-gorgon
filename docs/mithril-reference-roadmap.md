# Mithril.Reference Roadmap

A faithful POCO library covering every BundledData JSON file, with serialization
isolated so the library can be split into a Models / Serialization pair later
without touching consumers.

## Why this exists

Project Gorgon's CDN serves hand-tended JSON: discriminator-by-string
polymorphism (`T` field), single-or-list shapes on the same field, string-or-int
scalars, fields that appear and disappear between patches. The current
`Mithril.Shared.Reference` "raw" model (`RawQuest` and friends in
`ReferenceJsonContext.cs`) is a deliberately minimal projection feed — it covers
~20% of fields and absorbs polymorphism via `JsonElement?`.

The longer-term goal is a queryable typed layer over all of BundledData (a
NoSQL-shaped consumer model). Reaching that requires:

1. Faithful per-file POCOs that mirror the JSON.
2. Discriminator-aware deserialization that tolerates unknown values (CDN drift
   becomes a logged warning, not a crash).
3. Cross-file references treated honestly (today, `FavorNpc: string` everywhere;
   eventually typed handles).
4. A projection layer that reads from POCOs instead of `JsonElement` unfolding.

This roadmap covers (1) and (2). (3) and (4) are downstream work that becomes
mechanical once the POCO layer is in place.

## Architecture

Single project, internally split:

```
src/Mithril.Reference/
├── Models/             POCOs, no Newtonsoft references
│   └── <File>/         one folder per BundledData source
├── Serialization/      Newtonsoft + JsonSubTypes confined here
│   ├── Converters/     SingleOrArrayConverter, StringOrInt, etc.
│   ├── Discriminators/ JsonSubTypes fluent registration per polymorphic family
│   ├── ReferenceDeserializer.cs   public Parse<File> entry points
│   └── SerializerSettings.cs      configured JsonSerializerSettings
└── Mithril.Reference.csproj
```

Disciplines that keep the future split free:

- `Models/` is dependency-free — no `[JsonProperty]`, no `[JsonConverter]`, no
  `JToken` fields, no `using Newtonsoft.Json;`.
- Models target `netstandard2.0`; if Serialization needs net10 features, the
  csproj multi-targets only on Serialization-touching files.
- Property-name divergence (`Reward_Favor` ↔ `FavorReward`) is handled by a
  custom contract resolver in `Serialization/`, not by attributes on POCOs.
- Polymorphic discrimination uses **`JsonSubTypes` fluent registration**, not
  `[JsonSubtypes(...)]` attributes on the base class — keeps the type → CLR
  mapping in `Serialization/`.
- `Newtonsoft.Json` and `JsonSubTypes` are referenced with `PrivateAssets="all"`
  so they don't transit to consumers via project references.
- Public surface from `Serialization/` is the `ReferenceDeserializer` entry
  point and a configured `JsonSerializerSettings` factory; everything else is
  `internal`.

If a non-Newtonsoft consumer ever materialises, splitting into
`Mithril.Reference.Models` + `Mithril.Reference.Serialization.Newtonsoft` is a
`git mv` of the two folders plus updating two csproj files. No code changes.

## Phasing

### Phase 0 — Project skeleton  *(~½ day)*

- Create `src/Mithril.Reference/Mithril.Reference.csproj`.
- Add `Newtonsoft.Json` and `JsonSubTypes` package versions to
  `Directory.Packages.props`.
- Reference both with `PrivateAssets="all"` from `Mithril.Reference.csproj`.
- Folders: `Models/`, `Serialization/Converters/`, `Serialization/Discriminators/`.
- Add `tests/Mithril.Reference.Tests/Mithril.Reference.Tests.csproj`.
- Wire both into `Mithril.slnx`.
- Empty build passes.

### Phase 1 — Bridge primitives + Quests end-to-end  *(~2 days)*

Quests is the canary because its polymorphism (3 dict-or-list shapes, 25
requirement T-values, 9 reward T-values, single-or-list `Target`, string-or-int
`Level`) exercises every primitive the rest of BundledData will need.

**Bridge primitives** (`Serialization/Converters/`):

- `SingleOrArrayConverter<T>` — JSON value or array → `IReadOnlyList<T>`.
- `StringOrIntStringConverter` — coerces both to string.
- `DiscriminatedUnionFallbackConverter<TBase>` — wraps `JsonSubTypes` so an
  unknown discriminator deserializes to a sentinel `Unknown` subclass instead
  of throwing. **Critical for CDN drift tolerance.**

**Models** (`Models/Quests/`):

- `Quest` — top-level POCO (~45 fields).
- `QuestObjective` — ~25 fields including objective-level Requirements.
- `QuestRequirement` abstract + 25 concrete subclasses + `UnknownRequirement`
  sentinel.
- `QuestReward` abstract + 9 concrete subclasses.

**Tests:**

- Round-trip the bundled `quests.json`; assert count == 2981.
- Assert zero `Unknown*` sentinels (means discriminator coverage is complete).
- Spot-check fixtures: `quest_13` (dict-shape Requirements), `quest_17101`
  (Or-list), `quest_45131` (list-shape RequirementsToSustain), `quest_29010`
  (objective-level Requirements).

**Exit criterion:** `ParseQuests(bundledJson)` returns 2981 quests, zero
unknowns, zero exceptions. Tests passing.

### Phase 2 — Tier 1 files  *(~3-4 days)*

Files with structurally unique polymorphism. Each gets its own ½-day. Order by
polymorphism complexity, not file size:

1. **`recipes.json`** — `ResultEffects` strings (parser already exists in
   `ResultEffectsParser.cs`); `Ingredients`/`ResultItems` keyword-matched slot
   variants likely need a polymorphic ingredient hierarchy.
2. **`items.json`** — `EffectDescs` is a procedural-string list; treat as
   `IReadOnlyList<string>` and resolve at projection time. ~30 fields, mostly
   scalar.
3. **`npcs.json`** — `Preferences`, `Services`, `ItemGifts` polymorphism is
   mild.
4. **`sources_items.json` / `sources_recipes.json` / `sources_abilities.json`** —
   envelope is `{ "item_N": { "entries": [...] } }`. Entries polymorphic by
   `Type` (Vendor, Recipe, Quest, Monster, Angling, Barter, NpcGift, HangOut,
   …). Discriminator pattern reusable from quests.

Per file: POCOs, discriminator wiring (if polymorphic), `Parse<File>` entry,
round-trip test with zero-unknowns assertion plus ~3 spot-check fixtures.

**Exit criterion:** every Tier-1 file deserializes its bundled copy with zero
unknowns. **Stop and revisit the bridge primitives if any new polymorphism
shape can't be expressed by the existing converters** — fix the primitive, not
the file.

### Phase 3 — Tier 2 files  *(~2 days)*

Mostly straightforward dictionary-of-records files with limited polymorphism;
~1 hour each.

`skills.json`, `xptables.json`, `areas.json`, `attributes.json`,
`tsysprofiles.json`, `tsysclientinfo.json`, `landmarks.json`,
`storagevaults.json`, `directedgoals.json`, `lorebookinfo.json`,
`lorebooks.json`, `playertitles.json`, `itemuses.json`.

`tsysclientinfo.json` (10 MB) is the largest in this tier and has a two-level
envelope (`power_NNNN` → `id_N`); mirror the structure already captured in
`RawPower`/`RawPowerTier`.

### Phase 4 — Tier 3 files: large & sparse  *(~2 days)*

Big files Mithril doesn't currently parse. POCO at minimum; projection layer
comes later when a consumer actually needs the data.

`abilities.json` (8.7 MB), `effects.json` (6.5 MB), `items_raw.json` (6.8 MB),
`advancementtables.json` (2 MB), `ai.json`, `abilitykeywords.json`,
`abilitydynamicdots.json`, `abilitydynamicspecialvalues.json`.

Each likely needs a Python-style polymorphism inspection pass (top-level
field counter, discriminator-T enumeration). Budget ~½ day per: inspect →
POCO → register → test count.

**Defer:** `strings_all.json` (15 MB; flat `Dictionary<string, string>`, no
POCOs needed).

### Phase 5 — Migrate existing projection  *(~2 days)*

Once enough Tier-1 files have POCO parity, swap consumers file-by-file:

- `Mithril.Shared.Reference.QuestEntry` projects from `Quest` POCO instead of
  `RawQuest` + `JsonElement` unfolding. Polymorphic-cast logic deletes.
- `ReferenceDataService` calls `Mithril.Reference.ParseQuests` instead of
  `JsonSerializer.Deserialize<Dictionary<string, RawQuest>>`.
- Delete `RawQuest` and friends from `ReferenceJsonContext.cs`.
- Repeat per file.

**Exit criterion:** `ReferenceJsonContext.cs` shrinks file-by-file. Eventually
the file becomes redundant and `Mithril.Shared.Reference` houses only
projection types and `IReferenceDataService`.

### Phase 6 — CDN drift instrumentation  *(~1 day)*

Once unknowns are tolerated, surface them.

- `IDiagnosticsSink` warning whenever an `Unknown*` sentinel is parsed:
  includes file, key, and discriminator value.
- Optional startup diagnostic running `ParseAll()` against bundled data and
  logging unknown counts per file.
- Future: a CDN-refresh hook that diffs unknown counts against the previous
  version. That's the schema-drift alarm.

## Budget

| Phase | Effort | Output |
|-------|--------|--------|
| 0 | ½ day | Skeleton + test project + dependencies wired |
| 1 | 2 days | Bridge primitives + Quest POCOs end-to-end |
| 2 | 3-4 days | Tier-1 files (recipes, items, npcs, sources_*) |
| 3 | 2 days | Tier-2 files (~13 small/medium files) |
| 4 | 2 days | Tier-3 files (~7 large/sparse files) |
| 5 | 2 days | Projection layer migration |
| 6 | 1 day | Drift instrumentation |
| **Total** | **~12-13 days** | Faithful POCO coverage of all BundledData |

Marginal cost after Phase 1 is ~½ day per typical file. The bridge primitives
are the upfront tax; everything else is mechanical.

## Decision points

- **After Phase 1:** review the bridge primitives. If `SingleOrArrayConverter`,
  `StringOrIntStringConverter`, and `DiscriminatedUnionFallbackConverter`
  covered every quest shape, the design holds. If any shape needed a one-off
  hack, fix the primitive before Phase 2.
- **After Phase 2:** check whether cross-file structural patterns warrant
  shared base types (e.g. `IItemRef { string InternalName }` for fields that
  reference items). Probably premature; revisit at end of Phase 3.
- **Before Phase 5:** confirm projection consumers actually benefit from POCOs.
  If a projection only uses ~5 fields, migration is mostly cosmetic — still
  worth doing for `RawQuest` deletion, but de-prioritise.

## Non-goals

- No JSON Schema authoring. The "loose data, tolerate drift" design supersedes
  schema-first.
- No source generators or codegen. Hand-written POCOs against hand-tended JSON.
  Codegen revisitable in a year if pain emerges.
- No two-library split now. Folder discipline + `PrivateAssets="all"` +
  `internal`-scoped serializer. Future split is a `git mv`.
- No simultaneous migration of all consumers. Old `RawQuest` and new `Quest`
  coexist during Phase 5; per-file rollout.
