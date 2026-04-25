# Project Gorgon CDN reference data

What the CDN at `https://cdn.projectgorgon.com/` publishes, captured so we don't have to keep re-crawling.

Snapshot source: `/v470/data/index.html`, fetched 2026-04-24.

## Version discovery

- **Root**: `https://cdn.projectgorgon.com/` serves a meta-refresh redirect to `/{version}/data/index.html` (e.g. `/v470/data/index.html`). `CdnVersionDetector` parses this.
- **File URL pattern**: `https://cdn.projectgorgon.com/{version}/data/{file}.json`
- **Icon URL pattern**: `https://cdn.projectgorgon.com/{version}/icons/icon_{IconId}.png`
- **Fallback version** if detection fails: `v469` (see `ReferenceDataService.FallbackCdnVersion`).

## Files — verbatim descriptions from the CDN's index page

The page titles itself *"Project: Gorgon 3rd-Party Tools Data - File Index"*. Descriptions below are quoted verbatim from that page. `(no description)` means the page lists the file with no accompanying text.

| File | Consumed? | Description — verbatim from index.html |
|---|:-:|---|
| `abilities.json` | ❌ | *(no description)* |
| `abilitykeywords.json` | ❌ | *(no description)* |
| `abilitydynamicdots.json` | ❌ | *(no description)* |
| `abilitydynamicspecialvalues.json` | ❌ | *(no description)* |
| `advancementtables.json` | ❌ | "mostly internal lists of attribute power-ups" |
| `ai.json` | ❌ | "configuration info for monster/pet AIs" |
| `areas.json` | ❌ | *(no description)* |
| `attributes.json` | ❌ | *(no description)* |
| `directedgoals.json` | ❌ | "describes all the entries in the 'stuff to do' pane of the Quest window" |
| `effects.json` | ❌ | "contains client data for every buff, debuff, or other weird special effect" |
| `items.json` | ✅ | *(no description)* |
| `items_raw.json` | ❌ | "raw version of items.json with embedded variables" |
| `itemuses.json` | ❌ | "used by the game to create the 'More Info' window for items" |
| `landmarks.json` | ❌ | *(no description)* |
| `lorebookinfo.json` | ❌ | "contains the categories that lore books can be in" |
| `lorebooks.json` | ❌ | "contains information about each lore book that can be found in-game" |
| `npcs.json` | ✅ | "describes an NPC in the game" |
| `playertitles.json` | ❌ | "Describes all the titles a player can earn" |
| `quests.json` | ❌ | "Describes each quest" |
| `recipes.json` | ✅ | *(no description)* |
| `skills.json` | ✅ | "Describes each skill" |
| `sources_abilities.json` | ❌ | "describes how each ability can be obtained by players" |
| `sources_items.json` | ✅ | *(no description)* |
| `sources_recipes.json` | ❌ | "describes how each recipe can be obtained by players" |
| `storagevaults.json` | ❌ | "Describes each 'storage vault' in the game" |
| `strings_all.json` | ❌ | "Contains all the named localizable strings, with English-language values" |
| `tsysclientinfo.json` | ❌ | "each entry is a possible treasure effect that can be found on loot" |
| `tsysprofiles.json` | ❌ | "a list of treasure effects" |
| `xptables.json` | ✅ | "indicates how much XP needs to be earned to level-up a skill" |
| `Translation.zip` | ❌ | "zip file of the contents of Translation/ folder" |

## Files observed on the CDN but not on the index page

The `/v470/data/` directory listing also contains these, not described on `index.html`:

- `checksums.json` — presumed per-file checksum list.
- `fileversion.txt` — plain-text version string.
- `version.json` — version metadata.
- `strings_skills.json`, `strings_areas.json`, `strings_requested.json`, `strings_npcs.json` — subset / scoped localization tables parallel to `strings_all.json`.

Treat these as second-class: they exist but aren't officially documented by the publisher.

## Schemas we already know from app code

For the six files the app consumes, the projected C# shape is authoritative — see [ReferenceJsonContext.cs](../src/Mithril.Shared/Reference/ReferenceJsonContext.cs).

- **`items.json`** → `RawItem` → [ItemEntry](../src/Mithril.Shared/Reference/ItemEntry.cs). Projected fields: `Name`, `InternalName`, `MaxStackSize`, `IconId`, `Keywords`, `EquipSlot`, `SkillReqs`, `Value`, `FoodDesc`. Known present-but-dropped fields: `EffectDescs`, `IsCrafted`, `CraftingTargetLevel`, `CraftPoints`, `RequiredAppearance`.
- **`recipes.json`** → `RawRecipe` → [RecipeEntry](../src/Mithril.Shared/Reference/RecipeEntry.cs). Projects `ResultItems`, `ProtoResultItems`, `ResultEffects`, `Ingredients`, and the XP-reward shape. Only `TSysCraftedEquipment(...)` effects are parsed today (see [ResultEffectsParser](../src/Mithril.Shared/Reference/ResultEffectsParser.cs)).
- **`skills.json`** → `RawSkill` → `SkillEntry`. XP-table pointer + combat flag.
- **`xptables.json`** → `RawXpTable` → `XpTableEntry`.
- **`npcs.json`** → `RawNpc` → `NpcEntry`. Gifts, preferences, services.
- **`sources_items.json`** → `RawItemSourceEnvelope` → `ItemSource`. Keyed by `"item_N"`; each has `entries[]` with `type` ∈ {Vendor, Drop, Gather, Craft, Quest, Barter, Monster, Angling, …}.

## Resolving equipment `EffectDescs` placeholders

Equipment's `EffectDescs` (on items.json) contains templated strings like `{MAX_ARMOR}{49}`, `{BOOST_SKILL_WEREWOLF}{12}`, `{COMBAT_REFRESH_HEALTH_DELTA}{3}`. The same format is used on `tsysclientinfo.json` power definitions.

**Resolution table: `attributes.json`** — 1914 entries, ~343 KB. Each top-level key is the placeholder token *without* the braces. Schema:

```json
"MAX_ARMOR":    { "DisplayRule": "Always",       "DisplayType": "AsInt",        "IconIds": [101], "Label": "Max Armor" }
"BOOST_SKILL_WEREWOLF":
                { "DisplayRule": "IfNotZero",    "DisplayType": "AsBuffDelta",  "IconIds": [108], "Label": "Lycanthropy Damage" }
"MOD_SKILL_ALL_KNIFE":
                { "DefaultValue": 1, "DisplayRule": "IfNotDefault", "DisplayType": "AsBuffMod", "IconIds": [108], "Label": "Knife Damage %" }
"MOD_TRAUMA_INDIRECT":
                { "DefaultValue": 1, "DisplayRule": "IfNotDefault", "DisplayType": "AsPercent", "IconIds": [107], "Label": "Indirect Trauma Damage %" }
```

Fields:
- **`Label`** — human-readable name. Also mirrored in `strings_all.json` as `attribute_<TOKEN>_Label` for localization.
- **`DisplayType`** — numeric formatting hint. Known values seen in sampling: `AsInt` (raw integer), `AsBuffDelta` (signed integer, prefix `+` when positive), `AsBuffMod` (multiplier → subtract 1 and render as signed percent), `AsPercent` (raw × 100 with `%` suffix), `AsDoubleTimes100` (raw × 100 with `%`, typically for probability fields).
- **`DisplayRule`** — when to render: `Always`, `IfNotZero`, `IfNotDefault`.
- **`IconIds[]`** — icon reference(s) from the CDN icon namespace.
- **`DefaultValue`** (optional) — used with `IfNotDefault` rule.

**Other candidates investigated and ruled out:**
- `effects.json` — status-effect registry (Sticky!, Hasted, …), keyed `effect_NNNN`. Not equipment placeholders.
- `tsysclientinfo.json` — treasure-effect power catalog, uses the same `{TOKEN}{value}` format in its own `EffectDescs`. Does not resolve tokens; it's a sibling consumer of `attributes.json`.
- `itemuses.json` — reverse index `item_N → RecipesThatUseItem[]`. Misleading description on the CDN page; it's just a crafting-dependency lookup.
- `strings_all.json` — 15 MB localization table. Has the labels under `attribute_<TOKEN>_Label` keys but `attributes.json` is both smaller and carries the formatting metadata.

**How to use it:** extend `RawItem` to deserialize `EffectDescs: string[]`, carry forward to `ItemEntry`, then render each entry by splitting `"{TOKEN}{value}..."` into `(token, value)` pairs and looking them up in `attributes.json`. Human-readable prose entries (no braces) pass through unchanged. Apply `DisplayRule` to decide whether to render, `DisplayType` to format the value, and prepend the `Label`.

## Adding a new file to the app

1. Add a `Raw*` type in [ReferenceJsonContext.cs](../src/Mithril.Shared/Reference/ReferenceJsonContext.cs) covering the fields we need.
2. Register it in the file-key switches in `ReferenceDataService.cs` (`RefreshAsync`, `LoadFile`, `GetSnapshot`, and `Keys`).
3. Add a `ParseAndSwap*` method that projects into a typed record under `Mithril.Shared/Reference/`.
4. Bundle a fallback copy under `src/Mithril.Shared/Reference/BundledData/` (and its sidecar `.meta.json`) so the app works offline.
5. Expose on `IReferenceDataService`.
6. Update this doc when sampling reveals new schema.
