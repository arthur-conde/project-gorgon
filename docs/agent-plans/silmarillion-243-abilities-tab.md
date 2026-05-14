# Silmarillion: Abilities tab (Bucket B — third non-v1 tab, paired with Effects)

**Tracked in:** #243 (this PR). Paired with #244 Effects (next, sequenced after this). **Also folds in** the `IEnumerable<ITabViewModel>` refactor the cookbook flagged in step 3 — see *Pre-work: ITabViewModel refactor* below.

> **Read first:** [silmarillion-tab-cookbook.md](../silmarillion-tab-cookbook.md). It owns scaffolding, DI cycle break, cross-link chip conventions (including the audit-existing-surfaces and Pitfalls sub-sections added post-#242), polymorphic-rendering rule, EntityRef factory normalisation, real-data smoke categories, reverse-lookup rebuild triggers, and verification ladder. This handoff covers only Abilities-specific decisions.

## Context

Abilities is the largest browse surface so far — `abilities.json` is 8.7 MB with 5894 entries. POCOs already exist in [`src/Mithril.Reference/Models/Abilities/`](../../src/Mithril.Reference/Models/Abilities/) (`Ability` + 7 supporting types: `AbilityAmmoKeyword`, `AbilityConditionalKeyword`, `AbilityCost`, `AbilityDoT`, `AbilityPvE`, `AbilitySpecialCasterRequirement`, `AbilitySpecialValue`). What's missing is the service-layer surface + the tab.

This is the **first Bucket B tab where neither a slim `*Entry` projection nor a full POCO surface exists on `IReferenceDataService`** — greenfield plumbing. No path-1-vs-path-2 dilemma; just add the POCO surface directly.

Sequenced before #244 Effects so Effect cross-link chips have ability-side destinations on both ends when Effects ships.

## Pre-work: `ITabViewModel` refactor (commit 1)

Cookbook step 3 has called out, across #241, #242, and now this PR, that adding a tab ripples into `SilmarillionViewModelTests` and `SilmarillionReferenceNavigatorTests` — every `new SilmarillionViewModel(items: null!, recipes: null!, ...)` site (7 across the two files) grows by one positional argument. The structural fix lands **first in this PR** so the Abilities tab uses the new pattern from day one and no future Bucket B/C tab pays the toll again.

### Interface

Add to `src/Silmarillion.Module/ViewModels/ITabViewModel.cs`:

```csharp
namespace Silmarillion.ViewModels;

public interface ITabViewModel
{
    /// <summary>Header text shown in the <c>TabControl</c> chrome.</summary>
    string TabHeader { get; }

    /// <summary>Sort key for tab order. Must match <see cref="IReferenceKindTarget.TabIndex"/> for any kind hosted by this tab.</summary>
    int TabOrder { get; }
}
```

Tab order constants (locked by current shipping order):

| Tab | `TabOrder` |
| --- | --- |
| Items | 0 |
| Recipes | 1 |
| NPCs | 2 |
| Quests | 3 |
| **Abilities** | **4** (added in commit 2) |

### Tab VM changes

Each of `ItemsTabViewModel`, `RecipesTabViewModel`, `NpcsTabViewModel`, `QuestsTabViewModel` implements `ITabViewModel` with two literal-returning properties. No other behaviour change.

### `SilmarillionViewModel` rewrite

```csharp
public SilmarillionViewModel(
    IEnumerable<ITabViewModel> tabs,
    IReferenceNavigator navigator,
    IEnumerable<IReferenceKindTarget> targets,
    IDiagnosticsSink? diag = null)
{
    Tabs = tabs.OrderBy(t => t.TabOrder)
        .Select(t => new ModuleTab(t.TabHeader, t))
        .ToArray();
    // ... rest unchanged ...
}
```

Drop the public `Items` / `Recipes` / `Npcs` / `Quests` properties — confirmed zero external readers (the existing test at `SilmarillionViewModelTests.cs:119` even comments "SilmarillionViewModel never reads .Items / .Recipes / .Npcs here"). XAML resolves child VMs by `DataType`-keyed `DataTemplate`, not by these named properties.

### DI registration

`SilmarillionModule.Register` — each tab VM is currently a `services.AddSingleton<XTabViewModel>()`. Add a second registration per tab that forwards the same instance to `ITabViewModel`:

```csharp
services.AddSingleton<ItemsTabViewModel>();
services.AddSingleton<ITabViewModel>(sp => sp.GetRequiredService<ItemsTabViewModel>());
// ...repeat for Recipes, Npcs, Quests, Abilities
```

Same fail-loud-on-duplicate pattern as `IReferenceKindTarget` already uses; if a future tab is missing its forward registration the empty `Tabs` collection is the symptom.

### Test fixture changes

Both test files have a helper or repeated construction site. Introduce a single `BuildSilmarillionVm(...)` test helper in each file that takes the navigator + targets and constructs the VM with an explicit `Array.Empty<ITabViewModel>()` (or specific stubs when a test exercises tab selection). Each `null!` positional becomes either omitted (when the test never reads it) or a dedicated stub.

This collapses 7 `null!`-laden constructions into terse helper calls and stops the "grow by one positional arg" pattern forever.

### Commit shape

- **Commit 1:** introduce `ITabViewModel`, implement on the 4 existing tab VMs, rewrite `SilmarillionViewModel` constructor, update DI registrations, refactor test fixtures. Tests should still pass — no behaviour change. Diff: ~250–350 lines.
- **Commit 2 onward:** the Abilities tab work below, which now adds itself by implementing `ITabViewModel` + registering one extra DI singleton. No further changes to `SilmarillionViewModel`.

## Service-layer scope (greenfield)

### 1. Add the Abilities POCO surface

`IReferenceDataService` gains:

- `IReadOnlyDictionary<string, Ability> Abilities { get; }` — keyed by `ability_NNNN` envelope key (per `Ability.cs:6` docstring).
- `IReadOnlyDictionary<string, Ability> AbilitiesByInternalName { get; }` — keyed by `Ability.InternalName`, for the navigator and chip cross-links.
- `IReadOnlyDictionary<string, IReadOnlyList<Ability>> AbilitiesBySkill { get; }` — derived index for the master-list skill filter facet. Build in a `BuildAbilitiesBySkill()` method called from `ParseAndSwapAbilities`.

All three with interface defaults (empty dictionary) per the cookbook's *Test scaffolding* pattern so consumer fakes in other modules don't ripple. Verify the parser already exists in `ReferenceDeserializer.ParseAbilities` (the bundled JSON is present, so likely yes); if not, wire it in.

### 2. Plumb `sources_abilities.json`

`sources_abilities.json` is bundled. Mirror the sources_recipes plumbing from PR #258 (which added `RecipeSources`): add `IReadOnlyDictionary<string, IReadOnlyList<AbilitySource>> AbilitySources { get; }` (or whatever name parallels `RecipeSources` consistently). Likely shares the `SourceEntry` polymorphism from `sources_items.json` / `sources_recipes.json` per the parser tier-1 work.

### 3. Defer the three sub-table files to a follow-up issue

The original #243 issue body framed `abilitykeywords.json`, `abilitydynamicdots.json`, and `abilitydynamicspecialvalues.json` as "sub-tables of ability metadata" to fold in as filter facets and detail sections. **The actual JSON shape is different**: these are **conditional rules engines**, not ability metadata.

- `abilitykeywords.json` is an array of conditional rules `{ MustHaveAbilityKeywords: [...], AttributesThatDeltaCritChance: [...], AttributesThatModDamage: [...], ... }` describing "if an ability has keyword X, these attribute deltas apply." Not "what keywords does ability X have" — that's already on `Ability.Keywords` directly.
- `abilitydynamicdots.json` is an array of conditional DoT rules `{ ReqAbilityKeywords: [...], ReqActiveSkill: "Druid", ReqEffectKeywords: [...], DamagePerTick, Duration, NumTicks, ... }` — "when an ability with these keywords is used by a character with this skill and these effect keywords, apply this DoT."
- `abilitydynamicspecialvalues.json` is an array of conditional tooltip-value rules `{ ReqAbilityKeywords, ReqEffectKeywords, Label, Value, Suffix }` — "Restores X Body Heat in cold environments" tooltip-style text.

These are runtime rule-engine inputs, not browseable per-ability metadata. The right destination for them is probably the **Effects tab** (#244) or a future "Conditional behaviors" diagnostic surface, not the Abilities tab's detail pane.

**Filed and deferred as #288.** All three are out of scope for this PR. The follow-up plumbs them as `IReadOnlyList<T>` on `IReferenceDataService` (not `IReadOnlyDictionary` — the JSON is array-shaped) for the **Effects tab** (#244) to consume.

The roadmap doc has been updated via PR #289 to retarget these three from "folds into Abilities tab" → "folds into Effects tab" in Bucket C. Bucket math is unchanged.

If the executing session disagrees with the deferral after their own grep, scope #288 in instead — but **don't render them as ability metadata** in the detail pane; the predicate-shaped data won't read parseably from the ability's perspective.

### 4. Extend `IEntityNameResolver`

Add the Ability case to `ReferenceDataEntityNameResolver` ([src/Mithril.Shared/Reference/ReferenceDataEntityNameResolver.cs](../../src/Mithril.Shared/Reference/ReferenceDataEntityNameResolver.cs)):

```csharp
EntityKind.Ability => ResolveAbility(r.InternalName),

private string ResolveAbility(string internalName) =>
    _refData.AbilitiesByInternalName.TryGetValue(internalName, out var a) && !string.IsNullOrEmpty(a.Name)
        ? a.Name!
        : internalName;
```

No envelope-key prefix to strip (Ability InternalNames are bare ASCII identifiers like `Sword1`, `Mentalism5`). Extend `ReferenceDataEntityNameResolverTests` with the Ability fallback-chain tests.

## Master-list row

Use an `AbilityListRow` projection wrapper. Cross-cuts to expose:

- `InternalName` — copied from `Ability.InternalName`
- `Name` — `Ability.Name ?? InternalName`
- `Skill` — resolved via `IReferenceDataService.Skills[Ability.Skill]?.DisplayName ?? Ability.Skill ?? "(unknown)"`
- `Level` — `Ability.Level`
- `Rank` — nullable, surfaces "Powerful" / "Major" / etc. distinctions
- `Keywords` — `IReadOnlyList<IngredientKeywordValue>`-style wrapped tag set for `CONTAINS` filtering
- `ResetTimeSeconds` — `Ability.ResetTime` (double)
- `IconID` — `Ability.IconID`

Reflect into `SchemaSnapshot` per cookbook step 6.

## Filter facets

Query box, at minimum:

- `Skill = "Sword"`
- `Level >= 50 AND Level <= 70`
- `Keywords CONTAINS "Attack"` (or `"Heal"`, `"Buff"` — eyeball `abilities.json` for the real keyword vocabulary; check whether `ability_groups` are distinct from `Keywords`)
- `Rank = "Major"` (if rank is a useful enum-like axis)
- `Name CONTAINS "Slash"`

5894 entries × the wide POCO means a healthy query box is load-bearing here; expect users to filter heavily before touching the detail pane.

## Detail-pane reading order

The Ability POCO is **wide but flat** (~80 properties on the top-level type, plus the nested `AbilityPvE` block). No polymorphic subclass hierarchy like Quest had — but the wide shape needs grouping discipline per the cookbook's polymorphic-rendering warning (group by intent, not by field name).

Suggested groups (verify against real data before locking in):

1. **Header** — Name + icon (from `IconID`) + skill chip + level + rank badge + ability-group chip (group abilities into clusters by `AbilityGroup` / `AbilityGroupName` when shared)
2. **Description** — `Ability.Description` (use `c:FormattedText.Text="{Binding Description}"` per cookbook *Shared.Wpf helpers*; PG inline `<i>`/`<b>` markup likely)
3. **Core mechanics** — `Target`, `ResetTime` (cooldown), `CombatRefreshBaseAmount`, `Costs` (sub-type — render each cost as a row), `SharesResetTimerWith` (clickable ability chip)
4. **Prerequisites** — `Prerequisite` (clickable ability chip), `UpgradeOf` (clickable ability chip), `ItemKeywordReqs` (these are keyword tags — render as `EntityRef.ItemKeyword(...)` chips so they deep-link to the Items tab filtered by that keyword, #270 pattern), `EffectKeywordReqs` (degrades to plain text until Effects ships), `SpecialCasterRequirements` (sub-type — small chip row)
5. **PvE stats** — unpack the nested `AbilityPvE` block. **Inspect this carefully** — the POCO doc says "per-version stat data" and this is where damage / range / effect references live. Likely effect references are chip targets (degrade until Effects ships).
6. **Conditional behaviors** — `ConditionalKeywords` sub-type list — render each `AbilityConditionalKeyword` as a row labelled by trigger keyword + applied effect chip.
7. **Ammo** (when present) — `AmmoDescription`, `AmmoKeywords`, `AmmoStickChance`, `AmmoConsumeChance` as a single ammo block.
8. **Environmental** — `WorksUnderwater`, `WorksWhileFalling`, `WorksWhileStunned`, `WorksWhileMounted`, `WorksInCombat`, `CanSuppressMonsterShout` — render only when **not** the universal default (cookbook *Default-value noise filtering*). E.g. don't surface `WorksInCombat=true` if 90% of abilities are true; do surface `WorksWhileFalling=true` since most aren't.
9. **Special targeting tags** (when present) — `TargetEffectKeywordReq`, `TargetTypeTagReq`, `PetTypeTagReq`, `SpecialTargetingTypeReq`
10. **Pet-related** (when present) — `PetTypeTagReq`, `PetTypeTagReqMax`, `IsCosmeticPet`
11. **Internal flags** (mono small text, deprioritised) — `InternalAbility`, `SpecialInfo`, `CanBeOnSidebar`, `IsHarmless`, `IsTimerResetWhenDisabling`, `IgnoreEffectErrors`
12. **Sources** — from `AbilitySources` plumbing (NPC trainers, etc.) — clickable NPC chips (NPCs shipped)
13. **Footer** — internal-name (mono, bottom-right)

Empty abilities (rare — most have `PvE` data) collapse to header + description + footer.

## Cross-link plumbing

Reverse lookups likely worth indexing:

- `AbilitiesUpgradingFrom : IReadOnlyDictionary<string priorAbilityInternalName, IReadOnlyList<Ability>>` — for "Upgrades to: …" reverse view on the prior ability's detail pane. Derived from scanning `Ability.UpgradeOf` references.
- `AbilitiesInGroup : IReadOnlyDictionary<string groupInternalName, IReadOnlyList<Ability>>` — for ability-group rosters in the detail pane. Derived from `Ability.AbilityGroup`.
- *(Optional)* `AbilitiesTaughtByNpc : IReadOnlyDictionary<string npcInternalName, IReadOnlyList<Ability>>` — for the NPCs tab to surface a "Teaches abilities" section. Same shape as `RecipesTaughtByNpc`. Defer if scope tightens.

Per cookbook *Reverse-lookup index rebuild triggers*:

| Index | Triggers rebuild from |
| --- | --- |
| `AbilitiesUpgradingFrom`, `AbilitiesInGroup`, `AbilitiesBySkill` | `abilities.json` |
| `AbilitiesTaughtByNpc` (if shipped) | `abilities.json`, `sources_abilities.json` |

## Cross-link chip degradation matrix

| Chip target | Today | Lights up when |
|---|---|---|
| `EntityRef.Item(...)` | navigable | shipped |
| `EntityRef.Recipe(...)` | navigable | shipped |
| `EntityRef.Npc(...)` | navigable | shipped (#241) |
| `EntityRef.Quest(...)` | navigable | shipped (#242) |
| `EntityRef.Ability(...)` | **navigable after this PR** | this PR |
| `EntityRef.ItemKeyword(...)` | navigable (synthetic) | shipped (#270) |
| `EntityRef.Effect(...)` | plain text | #244 |
| `EntityRef.Area(...)` | plain text | #245 |

### Audit existing surfaces (per cookbook)

Grep for `EntityRef.Ability(...)` literals and `ItemSourceChipVm(..., EntityReference: null, IsNavigable: false)` where the source `Type` could match an ability source. Likely stale sites: any existing chip-builder that mentions an ability by `InternalName` but renders plain text. The Items tab's "Sources" section already had this gap for NPCs (#241) and Quests (#242) — confirm it doesn't have a third instance for abilities.

### EntityRef factory normalisation

Per cookbook: check whether ability references in source data come bare or as slug forms. The `Ability.Prerequisite` / `Ability.UpgradeOf` / `Ability.SharesResetTimerWith` fields appear to be bare InternalNames; verify against real entries before relying on it. If any slug-form references surface (e.g. `Skill/AbilityName`), normalise inside `EntityRef.Ability(...)` like NPCs do — not at call sites.

### Chip-stub coverage grid (per cookbook)

Abilities don't have the symmetric requirement-vs-reward shape Quests did, but they do have parallel paths worth gridding for ChipName/Reference coverage:

| Entity kind | Prerequisite side | Cross-link side |
|---|---|---|
| Ability | `Prerequisite`, `UpgradeOf` ✓ chip | `AbilitiesUpgradingFrom`, `AbilitiesInGroup` ✓ chip |
| Item | `ItemKeywordReqs` → `EntityRef.ItemKeyword(...)` ✓ chip | (n/a) |
| Effect | `EffectKeywordReqs`, `TargetEffectKeywordReq` text only | (degrade until #244) |
| NPC | (n/a) | `AbilitiesTaughtByNpc` reverse ✓ chip |

Asserting tests should check `ChipName` / `Reference` / `Prefix`, not just `Text`.

## Tests

Per cookbook *Test scaffolding*. Abilities-specific:

- **`AbilitiesTabViewModelTests`** — master-list construction; skill resolution; keyword `CONTAINS`; `FileUpdated` re-bind on `"abilities"` preserves selection; detail-VM projection over the major grouping families (Identity, Core mechanics, Prerequisites, Conditional behaviors, Ammo, Environmental). One representative test per group.
- **`AbilitiesKindTargetTests`** — standard four-property assertions.
- **`ReferenceDataServiceTests`** — extend with `AbilitiesUpgradingFrom`/`AbilitiesBySkill` reverse-lookup integration tests.
- **`ReferenceDataEntityNameResolverTests`** — Ability case tests.
- **Real-data integration test** per cookbook rung 4: `RealBundledAbility_Sword1_ProjectsSensibly()`, `RealBundledAbility_Mentalism5_ProjectsSensibly()` (or analogous picks from different skills). Skips when bundled data absent. Asserts text shape: no `(unknown)` sentinels, expected groups present, Prerequisite/UpgradeOf chips resolved.

## Verification

Run the cookbook ladder, plus:

- **Real-data sanity walk** (cookbook rung 4): three abilities from different skill families — pick a Sword melee, a Druid spell, a Bard buff. Verify grouped rendering reads parseably; no group with unlabelled mystery chips; environmental-flag chips don't appear for universal defaults.
- **End-to-end cross-link round-trip**: open a high-level ability (e.g. `Sword5`), confirm `Prerequisite` chip clicks to the predecessor → click `UpgradeOf` back. Open an ability with a known NPC trainer, confirm Sources chip is clickable → NPCs tab opens. Click an `ItemKeywordReqs` chip → Items tab opens filtered.
- **Group-roster smoke**: an ability with `AbilityGroup` populated — confirm "Other abilities in group" section lists siblings as clickable chips.
- **Effects degradation**: confirm `EffectKeywordReqs` chips render as plain text (not as failing chips), and that they auto-light-up when #244 ships.

## Out of scope

- The three sub-table files (`abilitykeywords`, `abilitydynamicdots`, `abilitydynamicspecialvalues`). Tracked as **#288**, retargeted to the Effects tab in roadmap PR #289. Defer leans heavily — these are Effects-tab natural, not Abilities-tab natural.
- A full `EntityKind.AbilityKeyword` synthetic deep-link. The keyword filter facet on the master-list is enough for v1; defer keyword-as-chip-anchor unless cross-tab demand surfaces in a follow-up.
- The Skills tab. `Ability.Skill` renders as a labelled chip + filter facet; no Skills tab is planned.
- Effects tab dependencies (`EffectKeywordReqs`, `TargetEffectKeywordReq`, `AbilityPvE` effect references). All chip-degrade per the standard pattern; #244 lights them up.
- The companion "NPCs tab gains a 'Teaches abilities' section" follow-up. Reverse-lookup index can ship in this PR (so data is there); UI wiring can be a small follow-up.

## Adjacent work flagged elsewhere

- **#244 Effects tab** (next in sequence) — closes the ability↔effect cross-link loop.
- **Sub-table plumbing** — tracked as **#288**, can ship before / alongside / after this PR; pure service-layer plumbing with no UI consumer until Effects (#244) exists.

## Commit / PR shape

Single PR against `main`. Suggested branch: `feat/243-silmarillion-abilities-tab`. Two commits:

1. `refactor(silmarillion): introduce ITabViewModel; SilmarillionViewModel takes IEnumerable<ITabViewModel> — #243`
2. `feat(silmarillion): Abilities tab (Bucket B) — #243`

Likely diff size: **~1800–2300 lines total** — refactor commit ~250–350, feature commit ~1500–2000 (service-layer plumbing ~400, reverse-lookup indices ~150, AbilitySources plumbing ~150, tab VM + view ~500, kind target ~50, resolver extension ~30, tests ~400, plus a bundled `sources_abilities.json` membership index if not already plumbed ~50).

Closes #243. Lights up `EntityRef.Ability(...)` chip navigability everywhere; does not close #244 (Effects ships separately) and does not address the three sub-table files (#288).

Also removes the recurrent `null!` test-friction the cookbook called out — future Bucket B/C tabs only register `ITabViewModel` + their kind target, no `SilmarillionViewModel` ctor change.
