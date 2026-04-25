# BundledData Index

Fallback copies of the CDN reference data fetched at runtime from
`https://cdn.projectgorgon.com/{version}/data/{file}.json`. The csproj globs
`**/*.json` into `Content` (PreserveNewest), so dropping a new file here is
sufficient — no project edit needed.

Snapshot version: **v470** (April 2026).

## Files

| File | Size | Purpose |
|---|---:|---|
| `abilities.json` | 8.4 MB | Every player + monster ability. Each `Ability_<id>` key holds a `PvE` block (and optional `PvP` overrides). Monster-only abilities are tagged `Lint_MonsterAbility`. |
| `abilitydynamicdots.json` | 0.7 KB | Lookup table for damage-over-time formulas referenced by abilities. |
| `abilitydynamicspecialvalues.json` | 1 KB | Lookup table for ability "special value" placeholders (variable damage/scaling). |
| `abilitykeywords.json` | 6 KB | Maps common ability keywords to attribute bundles that get auto-applied. |
| `advancementtables.json` | 2.0 MB | Per-skill level-up reward tables (referenced from `skills.json`). |
| `ai.json` | 152 KB | Monster/pet AI configs. Side-use: any ability referenced here is for monsters/pets, not players. |
| `areas.json` | 3 KB | Zone metadata. |
| `attributes.json` | 350 KB | Token formatter — resolves `{MOD_SKILL_ARCHERY}`, `{DELTA_ARMOR}`, etc. into player-facing strings. **Used by the EffectDescs renderer.** |
| `directedgoals.json` | 24 KB | "Stuff to do" pane: categories + entries (mixed; `IsCategoryGate`=true marks headers). |
| `effects.json` | 6.4 MB | Buffs/debuffs/visual effects with a client-side display. `DisplayMode` ∈ {Effect, AbilityModifier, Instant}. Source-of-truth for tooltip/effects-pane content. |
| `items.json` | 6.7 MB | **Item template catalog** (≈ all equippable / consumable / quest items). Variables already substituted (use this, not items_raw). |
| `items_raw.json` | 6.7 MB | Same as items.json but with `<<<TripleStab>>>` localization placeholders un-resolved. Only useful for translation tooling. |
| `itemuses.json` | 202 KB | "More Info" window data; redundant with recipe scan today, but may grow. |
| `landmarks.json` | 41 KB | Map landmarks. |
| `lorebookinfo.json` | 0.6 KB | Categories used by lore books. |
| `lorebooks.json` | 217 KB | Lore book metadata + text (text omitted if `IsClientLocal=false`; server pushes those on pickup). |
| `npcs.json` | 292 KB | NPC roster. Internal name → friendly name + likes/dislikes (`Pref` is sign-bearing but not exact favor math). **Used by Arwen.** |
| `playertitles.json` | 166 KB | Earnable title definitions. |
| `quests.json` | 3.9 MB | Quest definitions. Some fields (`Requirements`, `InteractionFlags`) are server-only and will be pruned eventually. |
| `recipes.json` | 4.8 MB | Crafting recipes with `Ingredients` (`ItemCode` or `ItemKeys`), `ResultItems`, optional `ProtoResultItems` (enchanted output). `ResultEffects` strings drive Celebrimbor's recipe parser. |
| `skills.json` | 310 KB | Skill metadata, XP curve refs, advancement table refs, level-gated rewards. |
| `sources_abilities.json` | 394 KB | How players can obtain each ability (NPCs, drops, quests). Not yet wired into the client. |
| `sources_items.json` | 981 KB | How players can obtain each item — by NPC, monster, quest, recipe. **Used by Smaug / inventory tools.** |
| `sources_recipes.json` | 454 KB | How players can learn each recipe. |
| `storagevaults.json` | 26 KB | Storage NPCs/locations. **Used by Bilbo.** |
| `strings_all.json` | 15.0 MB | Localizable strings as a single dict. Synthesized convenience file; pair with `*_raw.json` for translation work. |
| `tsysclientinfo.json` | 10.5 MB | **Treasure System effect catalog.** Each `power_NNNN` lists tiered `EffectDescs`, eligible `Slots`, optional `Suffix` ("of Archery"). The pool of every possible roll. |
| `tsysprofiles.json` | 238 KB | **Maps an item's `TSysProfile` to its eligible powers.** 40 named profiles (`Sword`, `Bow`, `Shield`, `All`, `Newb`, ...) → arrays of `InternalName`s into `tsysclientinfo.json`. This is how to enumerate the random rolls a given item can have. |
| `xptables.json` | 43 KB | XP-per-level curves. Keys are `Level_N` despite naming awkwardness. |

## Random-roll data flow

```
items.json                       tsysprofiles.json              tsysclientinfo.json
  item_NNN                         "Sword": [                     "BackstabBoost": {
    TSysProfile: "Sword"  ──►        "BackstabBoost",     ──►       Slots: [...],
                                     "AccuracyBoost",                Tiers: [...],
                                     ...                             EffectDescs: [...],
                                   ]                                 Suffix: "of ..."
                                                                   }
```

Recipes can also bypass profiles entirely:
- `AddItemTSysPower(power, tier)` — deterministic augment (already parsed).
- `ExtractTSysPower(slot, skill, minTier, maxTier)` — extracts an existing augment off input gear; the second arg is the gating crafting skill, **not** a pool name.

## `.meta.json` sidecars

The seven oldest files have meta sidecars (`attributes.meta.json`, etc.) written by `ReferenceDataService` on download. Newer files don't have them yet — they'll be created on first runtime fetch.
