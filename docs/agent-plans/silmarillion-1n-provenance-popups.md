# Silmarillion: 1:N relationships → provenance popups

**Tracked in:** #318

**Supersedes:** the navigable-summary-chip section of [silmarillion-tab-cookbook.md](../silmarillion-tab-cookbook.md) (introduced #312, consolidated #314). Do not hand-edit the cookbook ahead of this work — the cookbook is updated *as part of executing* this refactor so it never describes a not-yet-existing state.

## The bug class this dissolves

Reverse-lookup / fan-out surfaces share a **dual-derivation** fault:

1. The set is computed once by an index union in `ReferenceDataService` (e.g. `AbilitiesByEffectKeyword` = `EffectKeywordReqs ∪ EffectKeywordsIndicatingEnabled ∪ TargetEffectKeywordReq`, built in `BuildEffectAbilityCrossLinkIndices`).
2. The detail-pane chip cluster and its "View all N" count read that index — correct.
3. The "View all N" chip deep-links via a synthetic kind target that **re-derives** the set as a query string (`AbilityByEffectKeywordKindTarget` emits `EffectKeywordReqs CONTAINS "<tag>"` — one field of the three the index unioned).

Two independent derivations of the same set. They diverge silently. Quantified on bundled data: of 24 distinct effect-keyword tags, `EffectKeywordReqs` carries **1**; `EffectKeywordsIndicatingEnabled` carries 21; `TargetEffectKeywordReq` carries 2. **23 of 24 tags** produce a chip that says "View all N" and deep-links to an empty filtered list. Pre-existing since #298; made universally visible by #315 (the summary chip went from overflow-only to always-shown).

Every prior proposed fix (broaden the query to all three fields, `InternalName IN (...)`, a structured `(column,op,const)` predicate threaded through `EntityRef`) was an attempt to keep derivation #3 faithful to derivation #1. That is a *discipline*, not a guarantee — it must be re-established correctly for every synthetic kind, forever, and silently rots when an index gains a field.

## The resolution

One rule, all tabs:

| Relationship | Surface |
|---|---|
| **1:1 direct reference** (this recipe, this NPC, this area) | navigable `EntityChip`, `EntityRef.X(internalName)` — unchanged |
| **1:N** (abilities required by this effect, recipes consuming this item, NPCs in this area) | **provenance popup, fed the index collection directly** |

A bespoke popup has no query language between the set and the screen. The expressiveness mismatch that *caused* the bug is simply absent. The set is materialized exactly once (the index); the popup is a view over that object; there is no second derivation to drift.

### The invariant (this is the actual fix; "popup" is its consequence)

> The set is materialized exactly once, in the index, **and the index retains why each member qualified**. The popup renders membership *and provenance*. The optional per-section **"To Query"** button emits a labeled, explicitly-lossy projection into the destination tab's query box — and is **never** the source of a displayed count or membership claim.

If a future popup ever populates itself by re-running a query, the second derivation is back and so is the bug. The invariant — not the word "popup" — is what must be enforced in review.

### Why this is strictly better, not a workaround

A generic browse tab can only show *which* rows matched, never *why*. The popup can section by inclusion reason: "15 abilities relate to this effect — 6 **require** it (`EffectKeywordReqs`), 7 are **enabled by** it (`EffectKeywordsIndicatingEnabled`), 2 **target** it (`TargetEffectKeywordReq`)." The bug's existence was evidence the relationship was richer than one predicate; the popup is the first surface that can tell the truth about it.

### Why "To Query" defuses rather than reintroduces the second derivation

The query is no longer load-bearing for correctness. The user sees the true set in the popup (from the index) *before* clicking. "To Query" is user-initiated, post-hoc, and visibly checkable against what they just saw. Lossiness becomes a named, scoped, opt-in projection ("Refine the EffectKeywordReqs subset →") rather than a silent truncation. Silent divergence was the disease; labeled-and-chosen lossiness is fine. It is *defanged*, not *gone* — state it that way in review, don't pretend the second derivation vanished.

## Execution plan

### 1. Provenance-retaining index shape

Today the union flattens to a dedup'd `HashSet<Ability>` — *which field matched* is discarded at accumulation (`BuildEffectAbilityCrossLinkIndices`, ~`ReferenceDataService.cs:1227-1259`). Change the index value shape to carry per-member match-reason tags. Concrete design points the executing agent must decide and document:

- Reason enum per relationship (e.g. `EffectAbilityMatchReason { Requires, EnabledBy, Targets }`).
- A member that qualifies via multiple fields: render **once with multiple reason tags** (recommended — dedup intent preserved, provenance complete) vs once per section (double-counts the "View all N"). Pick once-with-tags unless a surface has a reason to differ; document the call.
- The "View all N" count = distinct members (not sum of section sizes), so multi-reason members count once.

### 2. Shared provenance-popup control

One reusable control in `Mithril.Shared.Wpf` (sibling to `EntityChip`/`ActionChip`). Inputs: title, an ordered list of `(sectionLabel, IReadOnlyList<EntityChipVm>)`, optional per-section "To Query" command. Rows are navigable chips (1:1 — the popup is *composed of* direct references, which is consistent with the rule). Virtualize the row lists — high-cardinality precedent is the #259 Massive Tourmaline ~547 case; see #311 for the virtualization discipline.

### 3. Retire the synthetic-kind layer

Delete: `RecipeIngredientKeyword`, `ItemKeyword`, `RecipeIngredientItem`, `NpcByArea`, `AbilityByEffectKeyword` synthetic `EntityKind` values + their kind targets + factories. Each currently backs a `mithril://silmarillion/<kind>/...` route via the generic `SilmarillionDeepLinkHandler` and participates in navigator back/forward history (#229). Implications to handle explicitly, not discover mid-refactor:

- These were dubious as hand-typed URLs anyway; relationship URLs go away, **entity URLs (the 1:1 chips) remain**.
- Navigator history: a popup is not a navigation in the back/forward sense — opening it must not push history (mirror `TryOpenInWindow`'s non-navigating contract).
- Audit every call site that constructs these `EntityRef`s; they become popup invocations.

### 4. Cookbook supersession

Replace the navigable-summary-chip section (and the `ActionChip`-for-shortcuts note) with the chip-vs-popup rule + the invariant. `ActionChip` itself may survive as the "To Query" affordance — decide during execution. Done *in the refactor PR*, not before.

### 5. Migrate shipped surfaces + build new on the rule

Existing 1:N surfaces to convert: Items "Used in", Effects "Required by abilities", Areas "NPCs in this area". Build **#247 Lorebooks and every subsequent tab on this rule from day one** — do not ship more synthetic-kind surfaces. The #247 handoff's "bestowing-items" section must be re-spec'd to popup-from-index before that session starts (flagged here so it isn't missed).

## Discipline

Verbose-*capable* ≠ verbose-by-default. Show provenance because it aids understanding ("this isn't a hard requirement, it's a target-keyword"), not because the popup can hold arbitrary text. A provenance section with one trivial reason is noise — collapse to a flat list when there's only one reason.

## Sequencing

Large refactor; slice it:

1. Provenance index shape for **one** relationship (effect→abilities — the surfaced bug) + tests.
2. Shared popup control + that one surface migrated end-to-end (chip→popup, kind target deleted, deep-link route retired, history contract verified).
3. Cookbook supersession in the same PR as (2) so docs never lead or lag code.
4. Remaining surfaces (Items, Areas) one PR each, reusing the now-proven control.
5. #247+ built natively on the rule.

Each slice is independently reviewable and leaves the tree shippable. Do not bundle all surfaces into one PR — the #298/#310 feedback established slice-as-review-unit as the working discipline here.

## Verification

The invariant is the test target. Per migrated surface, a test that asserts: popup membership == index collection (same objects), "View all N" == distinct index members, and — critically — a regression test that would have caught the original bug: a tag present *only* in a non-primary field still appears in the popup with the correct provenance section. Real-bundled-data walk per the cookbook verification ladder (the #298 feedback: this is load-bearing, the bugs here are the ones synthetic tests miss).

## Why this is the terminal shape

Earlier framing was "co-locate the relationship definition so the index and the predicate can't drift." This is stronger: there is no predicate. The relationship is defined once, in the index; the popup renders that definition *and its reasons*; "To Query" is a labeled lossy projection that never sources a count. The system explains itself instead of requiring two derivations to agree. Further verbal refinement past this point has diminishing returns — the open decisions are now implementation calls (reason-enum shape, multi-reason rendering) for the executing session, recorded above.

---

*Drafted by Claude (Opus 4.7), filed by @arthur-conde via Claude Code on 2026-05-15.*
