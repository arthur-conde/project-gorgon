# Treasure System (TSys) Data Reference

How Project Gorgon's enchant / augment "TSys" reference data is laid out across
`items.json`, `recipes.json`, `tsysprofiles.json`, and `tsysclientinfo.json` —
and the eligibility rules we've derived from it. Anything labelled
**[unverified]** is inference from the JSON; flag in-game observation if it
turns out wrong.

## File-level data flow

```
items.json                 tsysprofiles.json            tsysclientinfo.json
  item_NNN                   "All": [                     "WerewolfBoost": {
    InternalName               "WerewolfBoost",   ──►       Skill: "Werewolf",
    TSysProfile: "All"  ──►    "ArcheryBoost",              Suffix: null,
    CraftingTargetLevel        ...                          Tiers: {
      = 50                   ]                                "id_1": {
                                                                EffectDescs: [...],
                                                                MinLevel: 10,
                                                                MaxLevel: 30,
                                                                MinRarity: "Uncommon",
                                                                SkillLevelPrereq: 10
                                                              }, ...
                                                            }
                                                          }
```

- **`items.TSysProfile`** — pool key (`"All"`, `"Sword"`, `"MainHandAugment"`,
  ~40 named pools). Names which `tsysprofiles` entry the gear draws from.
- **`items.CraftingTargetLevel`** — gear level used to bracket which power
  tiers can roll on this template.
- **`tsysprofiles.json`** — flat dict of `profileName → [powerInternalName, …]`.
  Pure pool membership; no per-power tier or rarity info.
- **`tsysclientinfo.json`** — every power, with per-tier eligibility metadata.

## Power tier eligibility fields

Every entry in `tsysclientinfo.json` (45,609 tiers across all powers) carries
the same per-tier shape:

| Field | Meaning |
|---|---|
| `EffectDescs` | `{TOKEN}{value}` placeholders + prose lines. Some prose embeds inline `<icon=N>` markers — strip them as you render. |
| `MinLevel` / `MaxLevel` | Gear-level bracket — tier rolls when `MinLevel ≤ gear.CraftingTargetLevel ≤ MaxLevel`. |
| `MinRarity` | Gear-rarity gate (the **rolled** rarity must be ≥ this). Always set; takes one of `Uncommon` / `Rare` / `Exceptional` / `Epic`. **`Common` is never used** — Common gear isn't enchantable. |
| `SkillLevelPrereq` | Wearer skill level the buff requires post-equip. Doesn't gate the *roll*; affects whether the rolled buff actually fires when you put the gear on. |

## Recipe shapes that consume TSys

### `TSysCraftedEquipment(template[, rarityBump[, subtype]])`

Crafted enchanted gear. Three arg variants observed in `recipes.json`:

| Args | Count | Meaning |
|---|---:|---|
| `(template)` | 745 | Base enchant. Implicit `rarityBump=0` → Uncommon floor. No subtype gate. |
| `(template, 0\|1)` | 706 | Rarity bump explicit. `0` → Uncommon, `1` → Rare. |
| `(template, 0\|1, subtype)` | 194 | Plus a beast/form gate (`Werewolf`, `Cow`, `Deer`, etc.). |

- **`template`** — the gear template's InternalName (e.g. `CraftedWerewolfChest6`). The trailing digit is the gear's own tier (Werewolf chest goes 1-12; Leather boots same).
- **`rarityBump`** — rolled-rarity floor delta: `0` = Uncommon, `1` = Rare.  Source: recipe Description on `*EM` recipes literally says *"Max-Enchanting creates items that are one Rarity level higher than they otherwise would be."* Plus those recipes carry the `MaxEnchanting` keyword.
- **`subtype`** — form/skill gate the treasure system applies at roll time.
  Its absence means the roll is unconstrained within the profile. **Don't
  fabricate a constraint from `SkillReqs`** — `SkillReqs` is the wearer's
  equip gate, *not* the roll-time gate. The treasure system only honors
  arg3.

  **[unverified]** Whether arg3 hard-restricts the roll (only Werewolf-skill
  powers ever roll on Werewolf gear) or only skews the distribution. The JSON
  alone can't tell us.

### `ExtractTSysPower(augmentItem, skill, minTier, maxTier)`

Recipe that extracts an augment power off existing gear into a standalone
augment item. 36 occurrences.

- `augmentItem` — InternalName of the augment item produced (e.g. `MainHandAugment`).
- `skill` — the **crafting** skill needed to do the extraction (e.g. `WeaponAugmentBrewing`). **Not** a power-skill gate.
- `minTier` / `maxTier` — tier bracket of the input augment that can be extracted; the produced augment matches the input's tier within this band.

### `AddItemTSysPower(power, tier)` and `AddItemTSysPowerWax(template, tier, durability)`

Deterministic augment / wax application. `AddItemTSysPower` (92 occurrences,
parsed) attaches a specific power tier directly. `AddItemTSysPowerWax` (19
occurrences, **not yet parsed**) does the same but the result is a finite-use
wax item.

### `CraftWaxItem(waxItem, power, tier, durability)`

72 occurrences, parsed. Crafts a wax/tuneup-kit item that, when applied,
attaches a power tier to its target.

## Recipe naming convention

For a gear template `CraftedX`, the corresponding recipes are typically:

| Recipe internal name | Effects | Output rarity |
|---|---|---|
| `CraftedX` | (no `ResultEffects`) | **Common** — produces base unenchanted gear; skips the TSys system entirely. |
| `CraftedXE` | `TSysCraftedEquipment(CraftedX[,0[,subtype]])` | **Uncommon** — base enchant. |
| `CraftedXEM` | `TSysCraftedEquipment(CraftedX,1[,subtype])` + `MaxEnchanting` keyword | **Rare** — Max-Enchant. |

Max-Enchanting variants only appear at higher gear tiers (≥5 in observed
data); shoddy / tier-1 gear has no `EM` recipe.

## Rolled-rarity ladder

```
Common ── (non-enchanted only; never reaches TSys) ──┐
Uncommon ── arg2 = 0 (base enchant) ─────────────────┤
Rare ───── arg2 = 1 (Max-Enchant)    ────────────────┤
Exceptional ─ (drops, transmute, …)                  ├── tier MinRarity uses
Epic ───── (drops, transmute, …)                     ┘   these four levels
```

Common is the implicit *gear* rarity floor for non-enchanted crafts and other
Common-tier items, but it is **never used as a power-tier `MinRarity`** —
because Common gear doesn't go through the treasure-system roll at all.

For ordinal comparison in queries we rank `Uncommon=1`, `Rare=2`,
`Exceptional=3`, `Epic=4`. A power tier with `MinRarityRank = R` rolls only
when the gear's rolled rarity rank is `≥ R`.

## Full eligibility predicate (per power tier on a given craft)

Given a `TSysCraftedEquipment(template[, rarityBump[, subtype]])` recipe:

```
let gearLevel       = template.CraftingTargetLevel
let rolledRarityRk  = 1 + rarityBump        // 0 → Uncommon=1, 1 → Rare=2
let formGate        = subtype                // null when arg3 absent
let slotGate        = template.EquipSlot     // null on items with no equip slot

powerTier eligible  ⇔
    powerTier.MinLevel  ≤ gearLevel  ≤ powerTier.MaxLevel
  ∧ powerTier.MinRarityRank ≤ rolledRarityRk
  ∧ (formGate is null  ∨  power.Skill = formGate)
  ∧ (slotGate is null  ∨  slotGate ∈ power.Slots)
```

This is the predicate the pool viewer pre-fills its query with when you click
"Browse pool" from a recipe card.

The `slotGate` clause is needed because `tsysprofiles.json` groups powers by
broad family (`"All"`, `"MainHandAugment"`, etc.) rather than by gear slot.
A power like `ParryRiposteBoostTrauma` lives in `"All"` but has
`Slots: ["MainHand", "Ring"]` in `tsysclientinfo.json` — so the *profile*
includes it for a chest piece, but the in-game roll rejects it. Filtering
by `power.Slots ∋ template.EquipSlot` matches the actual eligible-roll set.

Extract recipes (`ExtractTSysPower`) deliberately skip the slot gate: the
recipe doesn't bind a target item at parse time, so there's no slot to filter
on. The slot gate would have to apply at "augment X to item Y" time, which
isn't a UI affordance today.

## What this gives us at the UI layer

- **Pool viewer pre-filled query** for an enchant recipe like Quality Werewolf Barding
  (`CraftedWerewolfChest6`, level 50, MaxEnchant=no, subtype=Werewolf):

  ```
  MinLevel <= 50 AND MaxLevel >= 50 AND MinRarityRank <= 1 AND Skill = "Werewolf"
  ```

- **For an Extract recipe**, the tier bracket replaces the level-bracket
  clauses and there is no rarity / skill pre-fill (arg2 of Extract is the
  crafting skill, not a power-skill gate):

  ```
  Tier >= 0 AND Tier <= 30
  ```

The user can clear / widen / rewrite the query freely; the underlying data
materializes every (power, tier) row so nothing is permanently hidden.

## Calibration: ground-truthing rarity from inventory exports

`StorageItem` in character exports captures `Rarity` (the actual rolled
rarity), `IsCrafted`, `TypeID`, and `TSysPowers`. To verify the rarity ladder
is exactly `arg2=0 → Uncommon, arg2=1 → Rare`:

1. Filter exports to `IsCrafted == true && TSysPowers.Count > 0`.
2. Look up each item's recipe by InternalName — `*E` recipes are arg2=0, `*EM`
   recipes are arg2=1.
3. Tally `(arg2 → Rarity)` distribution.

Expected distribution if the model is right:
- `arg2=0` → 100% `Uncommon` (or higher if the player has transmuted/upgraded
  the item post-craft).
- `arg2=1` → 100% `Rare` (same caveat).

Any rows that disagree call the model into question.

## Known parsing gaps

These TSys-related effects in `recipes.json` are still unparsed at the time
of writing — see `ResultEffectsParser` for current coverage.

- `BoostItemEquipAdvancementTable(table)` — 37 occurrences. Equipment-time skill XP grant.
- Calligraphy / Meditation / Whittling sub-variants beyond
  `DispelCalligraphyA/B/C`, `CalligraphyComboNN`, `MeditationWithDaily`, the
  TSys-augment behavioural tags (`ApplyAugmentOil`, `RemoveAddedTSysPowerFromItem`,
  `ApplyAddItemTSysPowerWaxFromSourceItem`), and the `Decompose<Slot>ItemIntoAugmentResources`
  family — ~150 effects collectively. Same tag shape as the existing entries.
- Ingredient `ItemKeys` (keyword-matched ingredients, e.g. `{ "ItemKeys": ["Crystal"] }`) — silently dropped during parsing today. Not a TSys-specific issue but it can hide one of two ingredients in `*EM` recipes.
