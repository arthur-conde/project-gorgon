# Planner · Recipe field consumption

> **A different axis from [silmarillion-field-coverage.md](silmarillion-field-coverage.md).**
> That doc asks *which `Recipe` properties reach the user in the reference
> browser*. This one asks *which `Recipe` properties `CrossSkillPlanner`
> consumes when it generates a leveling plan* — i.e. which fields, if ignored,
> make the planner schedule **impossible or mis-counted crafts**.

## Why this is its own axis

The two axes are independent and a field can sit differently on each. The
canonical example: `MaxUses` is **surfaced** in Silmarillion (it has a chip) yet
was a **planner bug** — the planner ground recipes past their per-character cap.
The display-coverage doc would never have caught it because, by its axis, the
field was fine. So planner-consumption needs recording on its own, once, or the
next planner-relevant recipe field gets silently ignored the same way.

A `Recipe` property is **planner-relevant** iff ignoring it changes the *set or
count of crafts* a viable plan would schedule. Cosmetic / time-cyclical /
currency fields are not — see "Correctly ignored".

## Respected — the planner gates/counts on these

| Field | How `CrossSkillPlanner` uses it |
|---|---|
| `Skill` + `SkillLevelReq` | `IsAvailable`: recipe ineligible until the gating skill ≥ req. |
| `PrereqRecipe` | `IsAvailable`: ineligible until the prereq recipe is in `completed`. |
| `RewardSkill` / `RewardSkillXp` / `RewardSkillXpFirstTime` / drop-off | XP math (`LevelingMath`) — phase sizing + first-time-bonus ordering. |
| `Ingredients` | `RecipeExpander` / `SourcingPolicy` — intermediate-reuse credit + sub-DAG pruning. |
| `MaxUses` | Per-character lifetime cap. `RemainingUses` against the run's `completions` (history + this run); filters `available` and clamps the grind. (#396) |
| `OtherRequirements` → `AlwaysFail` | `IsAvailable` hard-exclude — `ImproveProphesied*` can never succeed; never schedule despite large advertised XP. |
| `OtherRequirements` → `RecipeKnown` | `IsAvailable` gate — same shape as `PrereqRecipe`, against `completed`/`history.IsKnown`. |
| `OtherRequirements` → `RecipeUsed` | Per-character craft cap (the WeatherWitching litany; self-referential `RecipeUsed{self, n}` ⇒ `n+1` crafts). Folded into `RemainingCraftBudget` alongside `MaxUses`. |

## Deliberate punt — user-asserted, NOT auto-gated

`OtherRequirements` kinds that are genuine **non-skill, non-recipe-state**
unlocks: `PetCount`, `HasHands`, `HasEffectKeyword`, `EquipmentSlotEmpty`,
`Appearance`, `EntityPhysicalState`, `EntitiesNear`, `InGraveyard`,
`DruidEventState`, `IsLycanthrope`, `HasGuildHall` (~42 recipes, ≤19 each).

These match the `AssertedUnlocks` philosophy (`PlanInputs.cs`: *"the planner does
NOT pursue favor/quests/lorebooks itself — non-skill unlocks are user-asserted"*).
The planner does **not** read them; a user who knows they'll satisfy the
condition plans as normal. **Known limitation:** `AssertedUnlocks` is keyed by
recipe `InternalName`, so it cannot actually *express* most of these
(pet count, body form, equipped-slot, world event). This is accepted: these
gates are rare, situational, and outside the planner's "skill grind path"
remit. Do not file "increase coverage" issues against this list; if a future
audit re-flags it, point here.

## Correctly ignored — not planner-relevant

| Field(s) | Why ignored |
|---|---|
| `OtherRequirements` → `TimeOfDay`, `Weather`, `MoonPhase`, `FullMoon` | Time/RNG-cyclical. The plan is a **time-stateless craft-count** projection by design (`SkillState` is stateless in time); these don't change the count. |
| `OtherRequirements` → `IsHardcore` | Character-mode flag; trivially rare; not a grind-path gate. |
| `ResetTimeInSeconds`, `SharesResetTimerWith` | Cooldown duration/grouping. Time-stateless design — pacing, not count. (Silmarillion *display* gap is the separate #342.) |
| `Costs` | Currency/item cost to perform the recipe. Doesn't change XP or craft count; sourcing is modelled via `Ingredients`, not `Costs`. |
| `RequiredAttributeNonZero` | 1 recipe in the bundled corpus; negligible; treat as the Bucket-B punt class if it ever matters. |
| VFX / animation / `ItemMenu*` / `SortSkill` / `Particle` / engine flags | Never affects the plan. |

## Acting on this doc

- A **new `RecipeRequirement` subtype** (parser emits `UnknownRecipeRequirement`)
  or a new planner-relevant `Recipe` field ⇒ decide its row here *before*
  assuming the planner handles it. Default assumption for an unhandled gate is
  "bug" until classified, given the `MaxUses` precedent.
- The "deliberate punt" / "correctly ignored" rows are settled decisions — do
  not re-open them as planner bugs.

## History

- **2026-05-16** — Created alongside the `OtherRequirements` planner fix
  (`AlwaysFail` exclude · `RecipeKnown` gate · `RecipeUsed` cap). Recipe-field
  prevalence and the kind taxonomy came from a full-corpus audit of bundled
  `recipes.json` (v470, 4427 recipes). Builds on the `MaxUses` fix (#396);
  `RecipeUsed` shares its `RemainingCraftBudget` path.
