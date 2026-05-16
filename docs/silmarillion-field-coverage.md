# Silmarillion · detail-view field coverage

> **Companion to [silmarillion-roadmap.md](silmarillion-roadmap.md).** The roadmap
> answers *which entities get a tab* (the Bucket A/B/C/D rule). This doc answers a
> different question: *within a shipped detail view, which POCO properties reach the
> user, which are deliberately omitted, and which are genuine gaps.*

## Why this is its own axis

Every CDN source has a faithful `Mithril.Reference` POCO (parser coverage is total —
see [mithril-reference-roadmap.md](mithril-reference-roadmap.md)). A detail view is a
*curated projection* of that POCO, not a property dump: the right design surfaces the
player-relevant mechanics and drops engine noise. So "property X is not bound" is not
automatically a defect — it is only a defect if X is player-relevant.

The purpose of this doc is to make that judgement **once, explicitly**, so the
deliberate omissions don't get re-flagged as gaps every time someone eyeballs a POCO
next to its view (the same phantom-gap problem ai.json / itemuses.json had at the
reference-library layer).

## Omission taxonomy

Unsurfaced properties fall into one of these classes. The first four are
**deliberate-by-design** and should stay omitted; the last is the only one worth
filing issues against.

| Class | Examples | Verdict |
|---|---|---|
| **VFX / appearance** | `Particle`, `LoopParticle`, `SelfParticle`, `TargetParticle`, `*Appearance*` | Omit — irrelevant to a reference browser |
| **Animation timing** | `UsageAnimation`, `UsageAnimationEnd`, `UsageDelay`, `*Delay*` | Omit — engine playback detail |
| **Engine / lifecycle flags** | `IsClientLocal`, `DeleteFromHistoryIfVersionChanged`, `AttuneOnPickup`, internal IDs (`ID`, envelope `Key`) | Omit — not player-facing semantics |
| **Raw keyword/tag lists** | `Keywords` on most entities | Usually omit — internal predicate tokens, not human-readable; surface only when a slot/filter consumes them |
| **Player-relevant mechanic, unsurfaced** | recipe gating, costs, cooldowns, prerequisite chains; quest narrative prose | **Candidate gap — file an issue** |

A property is a *candidate gap* only if it changes what a player would do/expect and
we already render the same class of data on another entity. Consistency across tabs is
the test: if Quest and StorageVault render a polymorphic `Requirements` block but
Recipe drops its `OtherRequirements`, that asymmetry is a gap, not a design choice.

---

## Recipe — VERIFIED 2026-05-16

The one entity audited against source directly
([Recipe.cs](../src/Mithril.Reference/Models/Recipes/Recipe.cs),
[RecipeDetailViewModel.cs](../src/Silmarillion.Module/ViewModels/RecipeDetailViewModel.cs),
[RecipeDetailView.xaml](../src/Silmarillion.Module/Views/RecipeDetailView.xaml)).

**Surfaced:** `IconId`, `Name`→`DisplayName`, `InternalName` (footer), `Description`,
`Skill`+`SkillLevelReq` (chip, skill resolved to display name), `MaxUses` (chip),
`Ingredients` (item chips), keyword-slot ingredients (provenance popup, #318),
`ResultItems`/`ProtoResultItems`→"Produces", `ResultEffects` (plain-text stub, #214),
recipe sources ("Taught by", from `sources_recipes.json`), `OtherRequirements`
(one "Requirements" list of dual-shape rows — prose, or "{prefix} [inline chip]"
for `RecipeKnown`/cross-recipe-`RecipeUsed` — in authored order, the Quest
dual-shape idiom so cross-links read in the prose flow not as an orphaned pill
cluster; via `RecipeRequirementProjector`; `PetTypeTag` resolved through
`strings_all["npc_<tag>_Name"]` per the id→display-name convention — pets are
NPC/monster entities, "SummonedBakingBread" → "Rising Dough", not camel-split;
#342), `Costs` ("Cost" lines; #342),
`ResetTimeInSeconds` (cooldown chip beside `MaxUses`) + `SharesResetTimerWith`
(navigable recipe→recipe cross-link chip — every corpus value 19/19 is a real
recipe `InternalName` — labelled "Shares cooldown with", not prose; #342).

**Deliberate omissions** (taxonomy classes 1–4): `Key`, `UsageAnimation`,
`UsageAnimationEnd`, `UsageDelay`, `UsageDelayMessage`, `ActionLabel`, `Particle`,
`LoopParticle`, `SortSkill`, `DyeColor`, all `ItemMenu*`, `Keywords`,
`ValidationIngredientKeywords`, `RewardSkillXpDropOff*`, `RewardAllowBonusXp`,
`NumResultItems`, `RequiredAttributeNonZero`, `ResultEffectsThatCanFail`.

**Candidate gaps — player-relevant, unsurfaced (class 5).** Prevalence measured
against the bundled `recipes.json` (v470, 4427 entries) on 2026-05-16 — the gap is
*not* uniform, so it is not one issue:

| Property | Recipes carrying it | Why it matters | Precedent |
|---|---|---|---|
| **`PrereqRecipe`** | **2004 (~45%)** | Prerequisite-recipe chain — a primary crafting-progression axis | Navigable cross-link shape already exists (`EntityRef` → same Recipes tab); identical to how `Ingredients`/`Produces` chips work |

`OtherRequirements` (90, ~2%), `ResetTimeInSeconds` (51, ~1.2%),
`SharesResetTimerWith` (21, ~0.5%), and `Costs` (55, ~1.2%) **were** in this table
and are **now surfaced** (see "Surfaced" above) — resolved 2026-05-16, #342.

**Remaining gap — `PrereqRecipe`, broad, structural, high-value.** ~45% of all
recipes have a prerequisite the browser shows nowhere. It is a *navigable
cross-link* (recipe → prerequisite recipe), precisely what Silmarillion's chip
model is built for — the same shape as the shipped ingredient/produces chips, just
an unwired edge. This is the priority and stands alone.

> **Why the trio was resolved ahead of its "long-tail" priority.** It wasn't just
> Quest/StorageVault parity. These exact fields are the ones `CrossSkillPlanner`
> *deliberately punts on* (see
> [planner-recipe-field-consumption.md](planner-recipe-field-consumption.md)). The
> planner's punt is justified by a "user-asserted" contract — the user is assumed
> to know the gate exists. If the browser also hides it, that knowledge has no
> source: a silent trap, the `MaxUses`-bug shape one layer up. Surfacing them here
> is the *load-bearing complement* to the planner punt, not cosmetic completeness.

**Tracked in:** #341 (`PrereqRecipe` cross-link — priority, open) · #342
(`OtherRequirements` + `Costs` + reset-timer — **resolved 2026-05-16**).

---

## Other shipped detail views — AUDIT BASELINE (unverified) 2026-05-16

> **Verification owed.** The rows below come from an automated field-coverage audit,
> not a line-by-line source read. Treat as a baseline, not ground truth: when a tab is
> next touched, spot-verify its row and promote it to a VERIFIED section like Recipe's.
> Coverage is described qualitatively on purpose — the audit's percentages were
> estimates and are deliberately not reproduced here as fact.

| Entity | Coverage shape | Deliberate omissions (taxonomy 1–4) | Candidate gaps (class 5) |
|---|---|---|---|
| **Npc** | Comprehensive — slim POCO, fully surfaced | `AreaFriendlyName` (resolution fallback only) | None apparent |
| **Area** | Comprehensive | `ShortFriendlyName` filtered when == `FriendlyName` | None apparent |
| **Effect** | Near-complete | `Particle` (VFX), `SpewText` (combat-log string) | None apparent |
| **Lorebook** | Near-complete | `IsClientLocal`, `Visibility`, `Keywords`, `InternalName` | None apparent |
| **PlayerTitle** | Near-complete | `Keywords` | None apparent |
| **StorageVault** | Near-complete | `ID`, `Grouping` label, `SlotAttribute` | None apparent |
| **Ability** | Moderate — large POCO, core mechanics surfaced | Many VFX/animation/flag fields, attribute-delta lists, `SpecialInfo` | None confirmed — large flag tail is mostly class-3 noise |
| **Quest** | Moderate — typed objectives/requirements/rewards surfaced | Engine flags, `Reward_SkillLevels` dict | `PrefaceText` / `SuccessText` / `MidwayText` narrative prose — debatable; some players want lore text |

### Modeled but no detail view (by design — not gaps)

- **Item** — browsable in the Items master list; dedicated detail pane deferred per
  the roadmap ("cheapest standalone win once core entity tabs are in"). Cross-link
  infrastructure exists; the right-pane view does not. Tracked on the Roadmap Project.
- **Skill** — intentionally folded into Recipe/Ability tabs as chip/metadata
  (~30 skills; a dedicated tab adds little). No standalone tab planned.
- **Landmark** — non-standalone by design: renders inside Area detail as grouped
  provenance rows. Not a defect.

---

## Acting on this doc

- **`PrereqRecipe` cross-link** (#341) — the priority. ~45% of recipes affected; a
  navigable edge in Silmarillion's existing chip model.
- **Recipe-detail completeness pass** (#342) — **done 2026-05-16.**
  `OtherRequirements` + `Costs` + reset-timer now render. This was the
  load-bearing complement to the `CrossSkillPlanner` punt, not just
  Quest/StorageVault parity — keep it in lockstep: a new planner-punted
  `RecipeRequirement` arm must also get a `RecipeRequirementProjector` arm.
- Quest narrative prose is a *judgement call*, not a clear gap — decide before filing.
- Everything else unsurfaced is deliberate; do not file "increase coverage" issues
  against it. If a future audit re-flags class 1–4 properties, point it here.

### Known visual debt (axis: presentation, not coverage)

- **No fact / control / link visual grammar** (#404). A design critique found
  the shared `EntityChip` is visually identical to the header stat badges
  (`Skill N`, `MaxUses`, cooldown) and breaks prose in `{prefix} [chip]` rows.
  Root cause is the *absence of a grammar* distinguishing passive facts from
  controls from navigable links — the chip collision is one symptom. This is a
  *coverage-complete, presentation-wrong* state: #342's fields are all surfaced,
  the visual grammar is the debt. Agreed direction: grammar-first; link tier →
  **V2** (small lead-icon + gold name, no box — V5's prose/list dual form
  rejected as a call-site footgun); badge tier restyled to read inert (same
  pass, not orthogonal). Design-system change, deliberately **out of #342/#400
  scope** — tracked in #404. Don't re-flag the recipe-detail chips as a coverage
  gap; they're not.

## History

- **2026-05-16** — #342 resolved: `OtherRequirements` (typed lines + recipe
  cross-link chips, `RecipeRequirementProjector`), `Costs`, and reset-timer
  surfaced in the recipe detail. Reframed from "long-tail completeness" to the
  *load-bearing complement* to the `CrossSkillPlanner` deliberate punt — the
  display axis and the planner-consumption axis are now coupled by an explicit
  lockstep rule in both this doc and
  [planner-recipe-field-consumption.md](planner-recipe-field-consumption.md).
- **2026-05-16** — Recipe candidate gaps quantified against bundled `recipes.json`
  (v470). Reframed from one combined issue to two: `PrereqRecipe` (~45%, broad,
  cross-linkable — priority) vs. a long-tail completeness trio (1–2% each). Grounding
  measure, not inference. Filed as #341 (priority) and #342 (completeness).
- **2026-05-16** — Doc created. Recipe verified against source; remaining eight
  shipped detail views recorded as an unverified audit baseline. Field-coverage
  established as an axis distinct from the roadmap's tab-bucketing rule.
