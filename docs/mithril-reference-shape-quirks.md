# Mithril.Reference design notebook

A running log of two kinds of decisions made while authoring POCO models for
the BundledData files:

- **Shape quirks** — JSON oddities the deserializer copes with, including any
  unverified semantic gambles that might bite a future consumer.
- **Design notes** — non-obvious modelling choices (e.g. when to share a base
  type across files, when to keep hierarchies separate). The "why" matters
  more than the "what" — the code records the choice; this file records the
  reasoning so future-Arthur can re-evaluate when context shifts.

This file is the place future-Arthur should look first if a downstream
consumer (quest-eligibility evaluator, recipe planner, cross-file query layer)
starts producing wrong answers — the bugs are likely to be in the
*interpretation* of these models rather than in the parse.

---

## Shape quirks

### Quests: nested-array `Requirements` (interpretation: flatten as AND)

**Where:** `quests.json`, applies to a small number of quests including the
vampire-themed quest near line 40605 of the bundled `quests.json`. The
shape is `Requirements` whose array elements are themselves arrays:

```json
"Requirements": [
    { "Quest": "Grasuul", "T": "QuestCompleted" },
    [
        { "T": "IsVampire" },
        { "DaysAllowed": ["Monday", "Thursday"], "T": "DayOfWeek" }
    ]
]
```

That is: an outer array of two elements where element [0] is a plain
requirement and element [1] is itself an array of requirement objects.
Surfaced during the Phase 1 validation run as a `JsonReaderException` —
`DiscriminatedUnionConverter` expected `StartObject`, found `StartArray`.

**How the deserializer copes:** `SingleOrArrayConverter<T>` recursively
flattens nested arrays into the parent list. The example above parses to a
flat 3-element `IReadOnlyList<QuestRequirement>` containing
`QuestCompletedRequirement`, `IsVampireRequirement`, and
`DayOfWeekRequirement`. See [SingleOrArrayConverter.cs](../src/Mithril.Reference/Serialization/Converters/SingleOrArrayConverter.cs).

**Semantic gamble:** the bundled JSON allows three plausible interpretations
of nesting:

1. **Flat AND** — `[A, [B, C]]` means "A AND B AND C". Nested-ness is a
   hand-editing artifact; the data designer visually grouped some
   requirements but didn't intend distinct semantics.
2. **Implicit nested AND** — `[A, [B, C]]` means "A AND (B AND C)".
   Semantically equivalent to (1).
3. **Implicit nested OR** — `[A, [B, C]]` means "A AND (B OR C)". Nested
   arrays carry hidden OR semantics.

We chose interpretation (1) because:

- Project Gorgon already has an explicit `OrRequirement` (JSON `"T": "Or"`
  with a `List` field). If a quest designer wanted OR-grouping, they would
  use `Or` rather than implicit nesting.
- The affected quests' surrounding context (vampire + day-of-week, etc.)
  reads as a simple AND ("must be a vampire, must be Monday or Thursday")
  rather than as an OR.
- Flattening preserves all entries — no data loss in either direction.

**Risk:** if interpretation (3) is actually correct, flattening silently
drops a constraint. A quest meant to require "A AND (B OR C)" would parse
as "A AND B AND C", over-restricting eligibility in a future quest-eligibility
evaluator. The validation harness cannot detect this — the data parses
successfully under any interpretation. Only behavioural comparison against
the live game would catch the mistake.

**Verification owed:** before any consumer evaluates quest eligibility from
the parsed `Quest` model, identify every quest in `quests.json` with
nested-array `Requirements`, look up their gating logic on the Project
Gorgon wiki or in-game, and confirm the AND interpretation. ~15 minutes of
spot-checking, deferred until a real consumer needs the certainty.

---

## Design notes

### `RecipeRequirement` is a separate hierarchy from `QuestRequirement`

**Where:** [Models/Quests/QuestRequirement.cs](../src/Mithril.Reference/Models/Quests/QuestRequirement.cs)
and [Models/Recipes/RecipeRequirement.cs](../src/Mithril.Reference/Models/Recipes/RecipeRequirement.cs).
The two abstract bases share no inheritance; their concrete subclasses use
suffixes (`HasEffectKeywordRecipeRequirement`, `MoonPhaseRecipeRequirement`)
to disambiguate where T-value names overlap.

**The overlap:** eight T-values appear in both files —
`HasEffectKeyword`, `MoonPhase`, `EquipmentSlotEmpty`, `PetCount`,
`Appearance`, `EntityPhysicalState`, `FullMoon`, `TimeOfDay`. A naïve reading
suggests these *are* the same requirement and should share concrete types
across `Quests/` and `Recipes/`.

**Why we kept them separate:** field sets diverge between the two domains
in subtle ways:

- Quest `HasEffectKeyword` has just `Keyword`. Recipe `HasEffectKeyword` adds
  an optional `MinCount` field.
- Quest `TimeOfDay` has a `Hours` string field plus `MinHour`/`MaxHour`.
  Recipe `TimeOfDay` has only `MinHour`/`MaxHour`.
- Quest `PetCount` requires `MinCount` and `MaxCount`. Recipe `PetCount`
  requires only `MaxCount` (and on a different domain — quest pet vs druid
  summoned pet).

If we shared a single `HasEffectKeywordRequirement` class, it would carry
the union of both field sets, leaving consumers unable to tell whether a
given field was permitted in their context. That's worse than two classes
that each precisely describe their own domain.

**Trade-offs accepted:**

- ❌ **Boilerplate.** ~8 duplicated subclasses across the two files.
- ❌ **Cross-domain consumers cannot iterate generically.** A future query
  like "every requirement gated on a moon phase" needs to walk both
  hierarchies. Today there are no such consumers.
- ✅ **Each domain stays honest.** A `RecipeRequirement` carries exactly the
  fields recipes are observed to use; same for quests. Adding a field to one
  doesn't muddle the other.
- ✅ **Schema drift in one domain doesn't propagate.** If Elder Game adds a
  new field to recipe-side `HasEffectKeyword`, only the recipe model needs
  updating; the quest model stays pinned.

**Reconsider when:** a real cross-domain consumer materialises (e.g. a
"runtime behaviour evaluator" that needs to check whether the player is
currently a vampire, regardless of whether the gating context is a quest or
a recipe). At that point, the right move is probably to extract a shared
abstract base — e.g. `IRuntimeStateRequirement` — that both
`QuestRequirement` and `RecipeRequirement` can implement on their relevant
subclasses, rather than collapsing the two hierarchies into one.

**Reconsider also when:** the duplicated subclasses start drifting
accidentally (e.g. someone adds a field to one but forgets the other). If
that becomes a real source of bugs, the cost calculation flips.

---
