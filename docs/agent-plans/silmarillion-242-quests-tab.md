# Silmarillion: Quests tab (Bucket B — second non-v1 tab)

**Tracked in:** #242. Builds on the third-tab precedent set by #241 (NPCs) and #282 (`IEntityNameResolver`).

> **Read first:** [silmarillion-tab-cookbook.md](../silmarillion-tab-cookbook.md). It owns scaffolding, DI cycle break, cross-link chip conventions, audit-existing-surfaces pass, polymorphic-rendering warning, real-data sanity check, reverse-lookup rebuild triggers, and verification ladder. This handoff covers only Quest-specific decisions.

## Context

Quest POCOs are the most mature in the codebase — Phase-1 canary of the `Mithril.Reference` rewrite, with full validation harness coverage: 25 typed `QuestRequirement` subclasses + 9 typed `QuestReward` subclasses, 2981 entries deserialise cleanly. The deferral was UI-only — no parser obstacle.

With NPCs shipped (#241), most cross-link chips from quest detail now light up automatically: NPC givers/turn-in are clickable, item rewards are clickable, recipe rewards are clickable. The only large remaining degradation is Effects and Areas (#243/#244, #245/#246).

## Service-layer decisions (up front)

### 1. Quest POCO surface

`IReferenceDataService.Quests` AND `QuestsByInternalName` are **both** `IReadOnlyDictionary<string, QuestEntry>` ([IReferenceDataService.cs:172, 175](../../src/Mithril.Shared/Reference/IReferenceDataService.cs#L172-L175)). The slim `QuestEntry` was tailored for Quest-source resolution in `sources_items.json`; the tab needs the full [`Mithril.Reference.Models.Quests.Quest`](../../src/Mithril.Reference/Models/Quests/Quest.cs) POCO with `Requirements`, `Rewards`, `Objectives`, `FollowUpQuests`, `FavorNpc`, `MainNpcName`, `Keywords`, etc.

Unlike NPCs, both natural names are taken by the slim projection. Three paths — decide first, before writing the tab VM:

1. **Add `QuestPocosByInternalName : IReadOnlyDictionary<string, Quest>`** as a third property. Slightly ugly name; smallest blast radius.
2. **Migrate `QuestEntry` out entirely** (cookbook's path 2): replace `QuestEntry` with `Quest` everywhere, rename `Quests`/`QuestsByInternalName` properties to expose `Quest`. Grep `QuestEntry` consumers first — likely only `ResolveSourceContext` in `ReferenceDataService` and a handful of tests. If the consumer set is small, this is the cleaner long-term shape and the precedent Items took in #209.
3. **Rename and add**: rename existing slim `QuestsByInternalName` → `QuestEntriesByInternalName` (keeping it for `sources_items.json` resolution), then add `QuestsByInternalName : IReadOnlyDictionary<string, Quest>` exposing the POCO. Splits the naming-collision difference; mid-sized blast radius.

**Recommendation: try path 2 first**, fall back to path 1 if `QuestEntry` consumers are more entangled than a quick grep suggests. The cookbook recommends path 1 as default but Quests is the case where path 1 produces an awkward name; the marginal cost of path 2 is worth checking. Either way, alias `using QuestPoco = Mithril.Reference.Models.Quests.Quest;` in the tab VM and tests to disambiguate from any remaining `QuestEntry` references.

### 2. `directedgoals.json` plumbing

`directedgoals.json` is in the bundled-data folder (24 KB per [INDEX.md:22](../../src/Mithril.Shared/Reference/BundledData/INDEX.md#L22)) but not currently parsed or exposed on `IReferenceDataService`. The shape per the index comment is "categories + entries (mixed; `IsCategoryGate`=true marks headers)."

Per the issue body, fold `directedgoals` in as a **filter chip** on the Quests tab ("guided objectives" / "stuff-to-do pane"), **not its own tab**. Two implementation paths:

1. **Plumb minimally**: add an `IReferenceDataService.DirectedGoalQuestKeys : IReadOnlySet<string>` exposing just the quest internal names referenced by directedgoals entries (drop the category headers). Master-list filter facet becomes `IsGuidedObjective = true|false` on the projected row.
2. **Plumb richly**: full POCO of `directedgoals.json` with category grouping, and the Quests tab can surface "appears in: [Category]" as detail-pane metadata.

**Recommendation: path 1.** Surface area is smaller, deferral cost is low if richer plumbing is later wanted, and the filter-chip use case the issue calls out only needs membership testing.

### 3. Reverse-lookup indices

For the detail pane to surface "Quests given by this NPC" (when the NPCs tab is opened — already shipped), and for the NPCs tab to start showing a "Quests given" section (handoff for that update can ship in the same PR or follow-up):

- `IReadOnlyDictionary<string npcInternalName, IReadOnlyList<Quest>> QuestsByGiverNpc` — derived from quests where `QuestNpc` or `MainNpcName` matches an NPC internal name. Likely both fields can give-and-turn-in; check sample data to confirm semantics.
- *(Optional in this PR)* `IReadOnlyDictionary<string itemInternalName, IReadOnlyList<Quest>> QuestsRewardingItem` — for surfacing on item detail. Defer to a follow-up unless the item-detail cross-link audit (below) needs it now.

Build these in a `BuildQuestCrossLinkIndices()` method on `ReferenceDataService`, mirroring `BuildRecipeCrossLinkIndices`. Per the cookbook's *Reverse-lookup index rebuild triggers* table:

| Index | Triggers rebuild from |
| --- | --- |
| `QuestsByGiverNpc` | `quests.json`, `npcs.json` |
| `QuestsRewardingItem` (if shipped) | `quests.json`, `items.json` |

### 4. `IEntityNameResolver` — add the Quest case

`ReferenceDataEntityNameResolver` ([src/Mithril.Shared/Reference/ReferenceDataEntityNameResolver.cs](../../src/Mithril.Shared/Reference/ReferenceDataEntityNameResolver.cs)) has cases for `Item`, `Recipe`, `Npc` per #282. Add a `Quest` case:

```csharp
EntityKind.Quest => ResolveQuest(r.InternalName),

private string ResolveQuest(string internalName) =>
    _refData.<QuestsByInternalName-or-whichever-name>.TryGetValue(internalName, out var q) && !string.IsNullOrEmpty(q.Name)
        ? q.Name!
        : internalName;  // no obvious envelope-key prefix to strip; quest_N keys don't appear in InternalName
```

Extend `ReferenceDataEntityNameResolverTests` with the Quest fallback-chain tests (POCO Name present, Name absent → InternalName).

## Master-list row

Use a `QuestListRow` projection wrapper (mirror `RecipeListRow`, `NpcListRow`). Cross-cuts to expose:

- `InternalName` — copied from dictionary key
- `Name` — `Quest.Name ?? InternalName`
- `Level` — nullable `int`
- `FavorNpcDisplayName` — `_nameResolver.Resolve(EntityRef.Npc(Quest.FavorNpc))` if present, else null
- `DisplayedLocation` — area string as-is (degrades to plain text — Areas not shipped)
- `Keywords` — `IReadOnlyList<IngredientKeywordValue>`-style wrapped tag set for `CONTAINS` filtering
- `IsGuidedObjective` — bool, from the directedgoals membership check
- `IsCancellable`, `IsGuildQuest` — surface as bools for query filtering

Reflect into `SchemaSnapshot` per cookbook step 6.

## Filter facets

Query box, at minimum:

- `Level >= 30 AND Level <= 50`
- `Keywords CONTAINS "MainStory"` (or whatever the prominent tag namespaces are — eyeball quests.json for the real keyword vocabulary)
- `FavorNpc = "Joeh"` (substring match across the resolved display name)
- `IsGuidedObjective = true`
- `IsGuildQuest = true`
- `DisplayedLocation = "Serbule"`

Quick-filter chip strip above the list is optional; the issue body calls out `IsGuidedObjective` specifically, so a single "Guided objectives only" toggle is probably worth shipping even before chip-strip plumbing arrives.

## Detail-pane reading order

Per the #234 reading-order convention, cross-links lead, metadata trails:

1. **Header** — `Quest.Name` + level chip + area chip (Area chip degrades to plain text until Areas ships) + cancellable/guild badges
2. **Giver / turn-in NPCs** — chip pair via `EntityRef.Npc(Quest.QuestNpc)` and `EntityRef.Npc(Quest.MainNpcName)`. Both clickable (NPCs shipped). Confirm semantics: `QuestNpc` is likely the giver, `MainNpcName` is the turn-in — verify against real data.
3. **Objectives** — `Quest.Objectives` rendered as a numbered list, each with its own typed `QuestObjective.Requirements` chip set (see polymorphism warning below).
4. **Requirements** — `Quest.Requirements` chip set, grouped by subclass family. **Do not** Concat() the requirements into a flat bullet list — see *Polymorphic rendering*, next section.
5. **RequirementsToSustain** — same shape as Requirements but separately labeled ("Maintain to keep quest").
6. **Rewards** — typed `Rewards` chip set + flat `Rewards_Items` chips (clickable, items shipped) + `Rewards_Recipes` (clickable) + `Rewards_Effects` (plain text until Effects ships) + `Reward_Favor` number with `FavorNpc` link.
7. **Pre-give** — `PreGiveItems`, `PreGiveRecipes`, `PreGiveEffects` chip rows.
8. **Follow-up quests** — `Quest.FollowUpQuests` as `EntityRef.Quest(...)` chips — these are self-referential to your new tab, so they're navigable from day one. **Audit pass:** confirm any pre-existing surface that mentions follow-up quest InternalNames now wires through `EntityRef.Quest(...)`.
9. **Reuse / repeatability** — `ReuseTime_Days`/`Hours`/`Minutes` rendered as a "Repeatable every N" line; null → "One-time" chip.
10. **Description / preface / success text** — long-form text block.
11. **Footer** — internal-name (mono, bottom-right).

Empty quests (no objectives, no requirements, no rewards) should collapse to header + description + footer with no dead zones.

## Polymorphic rendering — the load-bearing callout

25 `QuestRequirement` subclasses, 9 `QuestReward` subclasses. Per cookbook *"What your handoff still owns"*: **do not** render every subclass field via `Concat(...)` into one undifferentiated bullet list. Group by subclass family with a label.

Suggested grouping (verify against real data before locking in):

- **Player-state requirements** — `MinSkillLevelRequirement`, `MinCombatSkillLevelRequirement`, `MinFavorLevelRequirement`, `MinFavorRequirement`, `RaceRequirement`, `IsVampireRequirement`, `IsWardenRequirement`, `IsLongtimeAnimalRequirement`, `IsNotGuestRequirement`, `MoonPhaseRequirement`, `FullMoonRequirement`, `TimeOfDayRequirement`, `DayOfWeekRequirement`, `AppearanceRequirement` — rendered as a chip row.
- **Inventory / equipment requirements** — `InventoryItemRequirement`, `EquipmentSlotEmptyRequirement`, `EquippedItemKeywordRequirement`, `ActiveCombatSkillRequirement`, `HasMountInStableRequirement` — chip row with item links where applicable.
- **Progression requirements** — `QuestCompletedRequirement`, `QuestCompletedRecentlyRequirement`, `HangOutCompletedRequirement`, `GuildQuestCompletedRequirement` — chip row with **clickable quest chips** (`EntityRef.Quest(...)`).
- **Area / world-state requirements** — `AreaEventOnRequirement`, `AreaEventOffRequirement`, `InHotspotRequirement`, `MonsterTargetLevelRequirement` — chip row.
- **Flag / atomic requirements** — `InteractionFlagSetRequirement`, `InteractionFlagUnsetRequirement`, `AccountFlagUnsetRequirement`, `ScriptAtomicMatchesRequirement`, `AttributeMatchesScriptAtomicRequirement`, `HasEffectKeywordRequirement`, `RuntimeBehaviorRuleSetRequirement`, `GeneralShapeRequirement` — render as small monospace badges (these are mostly internal-developer-facing flags, low player legibility).
- **Composite** — `OrRequirement` wraps `List : IReadOnlyList<QuestRequirement>?` — render its children inline with an "any of" header.
- **`UnknownQuestRequirement`** — render as a warning chip with the raw discriminator string. CDN drift surfaces here.

**Real-data sanity check before shipping** (per cookbook verification rung 4): walk 3 real quests with mixed requirements — pick one each from a starter quest (`Quest 10001`), a high-level questline, and the `MoonPhaseRequirement` set. Confirm grouped rendering reads parseably with real data, not just unit-test fixtures. The NPCs `Training` cautionary tale was exactly this: synthetic tests green, real data unscannable.

For rewards (9 subclasses, simpler): one chip per `QuestReward` instance, labeled by subclass:

- `SkillXpReward(Skill, Xp)` → "Skill: 1,200 XP" chip
- `CombatXpReward(Xp)` / `GuildXpReward(Xp)` / `RacingXpReward(Xp)` — single-value XP chips
- `CurrencyReward(Currency, Amount)` / `WorkOrderCurrencyReward(Amount)` / `GuildCreditsReward(Amount)` — currency chips
- `RecipeReward(Recipe)` → clickable `EntityRef.Recipe(...)` chip
- `AbilityReward(Ability)` → ability chip (plain text until Abilities ships)
- `UnknownQuestReward` → warning chip

## Cross-link chip degradation matrix

| Chip target | Today | Lights up when |
|---|---|---|
| `EntityRef.Item(...)` | navigable | shipped |
| `EntityRef.Recipe(...)` | navigable | shipped |
| `EntityRef.Npc(...)` | navigable | shipped (#241) |
| `EntityRef.Quest(...)` | **navigable after this PR** | this PR |
| `EntityRef.Area(...)` | plain text | #245 |
| `EntityRef.Ability(...)` | plain text | #243 |
| `EntityRef.Effect(...)` | plain text | #244 |
| `EntityRef.PlayerTitle(...)` | plain text | #248 |

### Audit existing surfaces

Per cookbook *Cross-link chips → audit existing surfaces*: before shipping, grep for `EntityRef.Quest(...)` and `ItemSourceChipVm(..., EntityReference: null, IsNavigable: false)` literals where the source `Type` could match Quest (`"Quest"` in `sources_items.json`). The most likely stale site is the Items-detail "Sources" section (the same place where the NPC chip-builder was stale before #241). Replace hardcoded falsy flags with `_navigator.CanOpen(reference)`.

## Tests

Per cookbook *Test scaffolding*. Quest-specific additions:

- **`QuestsTabViewModelTests`** — master-list construction; sort order; `FileUpdated` re-bind on `"quests"` (and `"directedgoals"` if path-1 plumbing chosen) preserves selection; one detail-VM-projection test per `QuestRequirement` subclass family (player-state, inventory, progression, area, flag, composite) and per `QuestReward` subclass (one each). 25 requirements × 1 test would bloat; the family grouping caps it at 6 representative tests.
- **`QuestsKindTargetTests`** — standard four-property assertions.
- **`ReferenceDataServiceTests`** — extend with a `QuestsByGiverNpc` reverse-lookup integration test (fixture an NPC + a Quest with `QuestNpc` matching, assert the index entry).
- **`ReferenceDataEntityNameResolverTests`** — new test methods for the Quest case (POCO name present, POCO name absent → InternalName).
- **`StubReferenceData`** — gains a `QuestPocosByInternalName` (or whichever path-1/2/3 name lands) and the reverse-lookup dictionary. Interface-default empty per cookbook so other modules' fakes don't ripple.

## Verification

Run the cookbook ladder, plus:

- **Real-data sanity walk** (cookbook rung 4): three quests with mixed requirement families — verify grouped rendering reads parseably.
- **End-to-end cross-link round-trip**: open an NPC who gives quests, click a quest chip → Quests tab opens with that quest selected → click the giver chip from the quest detail → back to the NPC. Click a quest's `FollowUpQuests` chip → that quest opens. Back/forward navigation walks the chain correctly.
- **Items tab cross-link audit**: open an item that's a quest reward (e.g. anything that appears as `Rewards_Items` in some quest) → confirm Sources section shows a navigable quest chip, not plain text.
- **Cancellable + guild filter smoke**: query box `IsGuildQuest = true` → list shrinks to guild quests only; `IsCancellable = true AND Level >= 50` composes.

## Out of scope

- Effects/Areas/Abilities/PlayerTitle tab dependencies — those chips render plain text and degrade per cookbook. The handoff isn't responsible for shipping the dependent tabs.
- `QuestObjective`-level objectives that themselves carry requirements — render them as nested chip rows but don't build a parallel objective-completion-tracking system. This tab is a reference browser, not a quest-progress tracker.
- `Rewards_NamedLootProfile` and other internal-only fields — render as monospace metadata at the very bottom or omit entirely.
- The companion "NPCs tab gains a 'Quests given' section" follow-up. The reverse-lookup index ships in this PR (so the data is there); wiring it into the NPCs detail VM can be a small follow-up PR if scope tightens.
- Localization of requirement / reward strings — defer to #265 (typed `StringRef`).

## Commit / PR shape

Single PR against `main`. Suggested branch: `feat/242-silmarillion-quests-tab`. Conventional commit:

```
feat(silmarillion): Quests tab (Bucket B) — #242
```

Likely diff size: ~900–1300 lines across service-layer (path 2 migration: ~300, path 1 add: ~150) + reverse-lookup indices (~100) + tab VM + view (~400) + kind target (~50) + tests (~300) + resolver extension (~30) + bundled `directedgoals.json` membership plumbing (~50). Closer to 1300 if path 2.

Closes #242. Lights up `EntityRef.Quest(...)` chip navigability everywhere in the codebase. Does not close #12 (typed quest requirements in the projection layer) — that issue tracks separate work and remains adjacent infrastructure.
