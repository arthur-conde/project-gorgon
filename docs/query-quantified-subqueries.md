# Quantified subqueries (`WITH ANY|ALL`) — design note

**Tracked in:** [#349](https://github.com/moumantai-gg/mithril/issues/349)
(umbrella), [#350](https://github.com/moumantai-gg/mithril/issues/350)
(canonical `StoreCapIncrease`, PR-1),
[#351](https://github.com/moumantai-gg/mithril/issues/351) (engine, PR-2),
[#352](https://github.com/moumantai-gg/mithril/issues/352) (consumer wiring,
PR-3). Companion to [`query-system.md`](query-system.md) (user-facing
behaviour); this file is the *why*.

## The problem

Silmarillion queries reference data, and almost every dataset it owns is a
nested repeating group: recipes→ingredients, abilities→dots,
quests→requirements, NPCs→services/cap-increases. The query engine is flat —
only top-level row properties are queryable, plus a string-collection
`CONTAINS`. The originating question, *"which NPCs sell into a given keyword,
optionally at a tier / under a gold cap?"*, cannot be expressed: the data
lives in `StoreService.CapIncreases` (colon-packed strings) inside the
polymorphic `Npc.Services` collection, and `CONTAINS` can only test one
flattened token at a time — it cannot correlate *tier* and *gold cap* on the
**same** cap-increase row.

## A vs B: why an engine quantifier, not per-tab pivots

- **Option A — per-tab pivot grids / bespoke filters.** Each tab that needs
  nested filtering grows its own UI and its own ad-hoc predicate plumbing.
  Fast for the first tab, then N divergent idioms; the query box (the thing
  users already know) still can't express the question; every new dataset
  re-litigates it.
- **Option B — one engine quantifier** (chosen). `WITH ANY|ALL` is the
  structured-record analogue of the existing `IQueryStringValue` `CONTAINS`
  path: same grammar, same completion/highlighter, every tab gets it for
  free, and the correlation is *accurate* because the inner predicate is
  evaluated per element. Cost: the engine has to understand element schemas
  and polymorphism. We pay that once, centrally, with heavy unit coverage.

Locked corollaries (from the #349/#351 discussion):

- **No flat ride-along projection.** A duplicated flattened column would be a
  second idiom and double the data; the originating question is gated behind
  the engine track with no interim shortcut.
- **Ship the full engine up front (Option-2 scope):** both `ANY` and `ALL`,
  the build-time per-hierarchy collision classifier, and the
  mandatory-narrowing path — even though the only v1-wired consumer (store
  cap-increases) is homogeneous. Consumer wiring stays incremental (PR-3).
- **Grammar is column-first:** `<column> WITH ANY (<pred>)` /
  `<column> WITH ALL (<pred>)`, mirroring every other predicate; negation via
  the existing prefix `NOT ( … )`. This supersedes the `ANY … WHERE` form
  sketched in early #351/#352 comments.

## Semantics

- `ANY` ⇒ at least one element satisfies `<pred>` (short-circuits).
- `ALL` ⇒ every element satisfies it, **vacuously true over an empty
  collection**; a null collection is false for both (matches the
  `CONTAINS`-over-null-collection convention).
- `<pred>` binds against the **element** sub-schema, not the outer row, and
  is evaluated per element — so `WITH ANY (Tier='Despised' AND GoldCap>1000)`
  is true only if *one* element is both, never a column-wise OR across the
  collection. This is the headline correctness property.
- Negation is the existing prefix `NOT ( … )`; there is no inline `NOT WITH`.
- String / `IQueryStringValue` collections are rejected — they keep using
  `CONTAINS`, the single way to match a flat keyword list.

## Per-hierarchy narrowing contract

Element schema construction:

- **Homogeneous element** (a plain record like `StoreCapIncrease`) →
  `ColumnBindingHelper.BuildFromProperties` (the slice-1 path).
- **Polymorphic element** (a discriminated-union base registered in
  `Mithril.Reference`'s `DiscriminatorRegistry`) → the **union** of every
  concrete subtype's public properties plus the discriminator pseudo-column.
  Each binding reflects on the element's **runtime** subtype: a property the
  subtype doesn't declare yields the `QueryAbsent` sentinel — **distinct from
  present-but-null**. Every leaf predicate treats absent as an unconditional
  non-match (absent ≠ null; even `IS NULL` is false), so naming a
  sibling-subtype field skips that element rather than throwing. Wrong-subtype
  vs present-null is decided by reflecting on the concrete type, *never* by a
  swallowed reflection exception.

Narrowing mode is **per hierarchy, schema-derived** (not a global toggle).
`PolymorphicSchemaClassifier` unions the subtype property types:

- A property name with **one consistent type** across subtypes → no narrowing
  needed.
- A property name with **>1 distinct type** across subtypes (e.g.
  `QuestRequirement.Level` — `string?` on `MinSkillLevelRequirement`,
  `int?` on `MinCombatSkillLevelRequirement`) makes the hierarchy
  **Mandatory**: a reference to that property is a compile error unless an
  in-conjunction `<discriminator> = '<literal>'` equality scopes it (the
  guard resolves the colliding property to a single concrete type for that
  scope). A `!=` is **not** a guard; a guard inside a `NOT` subtree does not
  count (it isn't a positive narrowing).
- No collisions → **Optional**: a property declared on only one subtype, used
  without a guard, emits a non-fatal `QueryDiagnostic` *warning* (it can only
  match that subtype's elements) — it never throws.

The classifier walks the inner AST **per conjunctive scope**: split on `OR`
(each arm is a fresh scope), descend `AND` (same scope, DNF cross-product), a
`NOT` subtree contributes no positive guard but its references still need a
type. Today only `QuestRequirement` is Mandatory and it is **not
consumer-wired** (PR-3 wires the homogeneous `StoreCapIncrease`; `NpcService`
is Optional), so the mandatory path is exercised by unit tests only in v1.

### v1 limitations (documented, intentional)

The inner predicate of a quantifier compiles **once**, so a single flat
element sub-schema can give a colliding property exactly one
`ColumnBinding.ValueType`. Therefore:

- A colliding property that resolves to **different** types across the
  OR-branches of one quantifier (e.g. `int?` in one arm, `string?` in
  another) is rejected with a `QueryException` telling the user to split the
  query. Single-type-per-query is fine; divergent multi-scope is the v1 wall.
- The `QueryDiagnostic` soft-warning channel is plumbed end-to-end
  (`QueryCompiler.Compile(node, columns, ICollection<QueryDiagnostic>
  warnings, caseSensitive)` — additive overload; the existing public API is
  unchanged) but **UI surfacing is out of v1 scope**.

These are acceptable because the only Mandatory hierarchy is test-only in v1;
the shipped consumer is homogeneous and `NpcService` is Optional.

## Sequencing

Linear **PR-1 (#350) → PR-2 (#351 engine) → PR-3 (#351 wiring)**. #350
(canonical `StoreCapIncrease` record + parser) is independent of PR-2 and
unblocks PR-3. PR-2 is large, internal, zero-UX, high test density — it bakes
before any consumer is exposed. PR-3 is small and UX-facing: it surfaces
`CapIncreases` on the Silmarillion NPC list-row projection and plumbs the
warning collector into that tab.

## Key files

| File | Role |
|---|---|
| [`QueryAst.cs`](../src/Mithril.Shared.Wpf/Query/QueryAst.cs) | `QuantifiedNode`, `Quantifier`, `QueryDiagnostic` |
| [`QueryParser.cs`](../src/Mithril.Shared.Wpf/Query/QueryParser.cs) | `WITH ANY|ALL (…)` lexing + parse |
| [`QueryCompiler.cs`](../src/Mithril.Shared.Wpf/Query/QueryCompiler.cs) | `CompileQuantified`, `EnforceNarrowing`, conjunctive-scope expansion, `QueryAbsent` leaf guards |
| [`ColumnBindingHelper.cs`](../src/Mithril.Shared.Wpf/Query/ColumnBindingHelper.cs) | `BuildElementSchema`, `TryGetElementSchema` |
| [`PolymorphicSchemaClassifier.cs`](../src/Mithril.Shared.Wpf/Query/PolymorphicSchemaClassifier.cs) | per-hierarchy collision classification (cached) |
| [`QueryAbsent.cs`](../src/Mithril.Shared.Wpf/Query/QueryAbsent.cs) | absent-on-subtype sentinel (≠ null) |
| `Mithril.Reference/Serialization/Discriminators/` | `DiscriminatorRegistry`, `PolymorphicHierarchy`, per-family `*Discriminators` |
