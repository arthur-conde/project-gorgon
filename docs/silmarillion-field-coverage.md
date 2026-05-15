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
recipe sources ("Taught by", from `sources_recipes.json`).

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
| `OtherRequirements` | 90 (~2%) | Polymorphic recipe gating (who/what can run it) | Quest & StorageVault render their `Requirements` as typed rows |
| `ResetTimeInSeconds` | 51 (~1.2%) | Cooldown duration | We render the *cap* half (`MaxUsesChip`, 91 recipes ≈ same prevalence); the *cooldown* half is conspicuously absent |
| `SharesResetTimerWith` | 21 (~0.5%) | Shared-cooldown grouping | Pairs with the above |
| `Costs` | 55 (~1.2%) | Currency/item cost to perform the recipe | No equivalent rendered anywhere; standalone |

**This is two findings, not one:**

1. **`PrereqRecipe` — broad, structural, high-value.** ~45% of all recipes have a
   prerequisite the browser shows nowhere. It is a *navigable cross-link* (recipe →
   prerequisite recipe), which is precisely what Silmarillion's chip model is built for
   — the same shape as the already-shipped ingredient/produces chips, just an unwired
   edge. This is the priority and stands alone as an issue.
2. **`OtherRequirements` + `Costs` + reset-timer pair — long-tail completeness.**
   1–2% each. Worth a single "recipe-detail completeness" pass for parity with how we
   treat Quest/StorageVault requirements and our own `MaxUsesChip`, but low-traffic and
   clearly secondary to (1). The reset-timer omission is the sharpest of these because
   the *consistency* break is measurable: comparable prevalence to `MaxUses`, which we
   do surface.

**Tracked in:** #341 (`PrereqRecipe` cross-link — priority) · #342 (completeness
trio: `OtherRequirements` + `Costs` + reset-timer — lower-priority).

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
- **Recipe-detail completeness pass** (#342) — `OtherRequirements` + `Costs` +
  reset-timer; long-tail (1–2% each) but a parity/consistency fix. Do after #341.
- Quest narrative prose is a *judgement call*, not a clear gap — decide before filing.
- Everything else unsurfaced is deliberate; do not file "increase coverage" issues
  against it. If a future audit re-flags class 1–4 properties, point it here.

## History

- **2026-05-16** — Recipe candidate gaps quantified against bundled `recipes.json`
  (v470). Reframed from one combined issue to two: `PrereqRecipe` (~45%, broad,
  cross-linkable — priority) vs. a long-tail completeness trio (1–2% each). Grounding
  measure, not inference. Filed as #341 (priority) and #342 (completeness).
- **2026-05-16** — Doc created. Recipe verified against source; remaining eight
  shipped detail views recorded as an unverified audit baseline. Field-coverage
  established as an axis distinct from the roadmap's tab-bucketing rule.
