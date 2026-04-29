# Mithril.Reference shape quirks

A running log of JSON-shape oddities surfaced while authoring POCO models for
the BundledData files. Each entry captures: where the quirk lives, how the
deserializer copes, and any unverified semantic gambles that might bite a
future consumer.

This file is the place future-Arthur should look first if a quest-eligibility
evaluator (or any other consumer that interprets these models) starts
producing wrong answers — the bugs are likely to be in the *interpretation*
of these quirks rather than in the parse.

---

## Quests: nested-array `Requirements` (interpretation: flatten as AND)

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
