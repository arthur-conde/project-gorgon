# Silmarillion #424 — migrate the shared `ItemDetailView` to the #404 visual grammar

**Tracked in:** #424

> **Cold-start dispatch.** You are taking over a single, well-scoped follow-up
> to the **completed and merged** #404 Silmarillion visual-grammar program.
> Everything you need is in this doc + the authoritative sources it points to.
> Do **not** reconstruct any prior conversation — it is fully captured on #424
> and in-repo. The #404 program (Phase 4 primitives · Phase 5 nine-view
> fan-out · Phase 6 guardrail) is **done and on `main`**; this is the one
> deliberately-deferred surface.

## Read first, in order

1. **#424** — the issue: scope, why-it-matters, definition-of-done, pointers.
2. `docs/silmarillion-visual-grammar.md` — the ratified G3 grammar + amend-1/2
   + carry-forwards + decision log. The visual law. **Do not re-open or
   re-decide it.**
3. `docs/agent-plans/2026-05-17-silmarillion-404-phase5-fanout.md` — **the
   canonical element→primitive pattern table, the binding (ratified, do-not-
   re-litigate) decisions, the anti-goals, and the per-PR cadence /
   verification recipe.** Apply that table verbatim; this task is one more
   application of it.
4. The merged pilot, as the copy-template:
   `src/Silmarillion.Module/Views/RecipeDetailView.xaml` +
   `ViewModels/RecipeDetailViewModel.cs` (+ `RecipesTabViewModel.cs`).
5. The shared primitives you bind (do **not** modify them):
   `src/Mithril.Shared.Wpf/{Link,LinkVm,SetRef,SetRefVm,FactTable,FactTableVm,FactFooter,FactFooterVm,StructureLabelBehavior}.cs`.
6. `docs/silmarillion-field-coverage.md` §"Visual grammar (#404)" — the
   **"Remaining grammar surface (NOT closed)"** bullet *is* this task.

## The target (the whole job)

`src/Mithril.Shared.Wpf/ItemDetailView.xaml` (+ `ItemDetailViewModel.cs`,
`ItemDetailWindow.xaml`/`.cs`, `ItemDetailPresenter.cs` as the projection
needs). It still carries the **pre-#404 boxed-chip grammar**: boxed "Used as"
item-uses chips, a boxed skill-requirement pill (e.g. `Endurance 65`), plain
icon+text effect lines, gold title, mono footer. Migrate every rendered
element to the shared grammar primitives per the canonical pattern table.

## What is different from the Phase-5 fan-out — read carefully

- **There is NO pre-existing #404 Phase-1/2 matrix row for this view** — it
  was outside the nine-view sweep by design (Item has no Silmarillion *tab*
  detail; see `docs/silmarillion-field-coverage.md` §"Modeled but no detail
  view"). You must classify its elements yourself against the five tiers +
  the canonical pattern table — a mini Phase-1. **Enumerate every rendered
  element from the XAML + VM first; classify each into exactly one tier;
  then apply the table.** Pure layout (`Border`/`StackPanel`/`DockPanel`
  scaffolding) is out of audit scope, same as the pilot.
- **This IS a shared `Mithril.Shared.Wpf` primitive** — the exact thing
  Phase 5 anti-goal #3 forbade editing. That prohibition is *inverted* here:
  this view is the explicit target. But it has **cross-module blast radius** —
  `ItemDetailWindow` popups, provenance popups, Bilbo, and cross-link "open
  in window" from every migrated Silmarillion view all consume it. The
  Phase-4 grammar primitives themselves remain off-limits: if a primitive
  genuinely cannot express an element, **flag-and-stop**, do not ad-hoc-edit
  a primitive or `Resources.xaml`.
- **Verification must cover all consumers, not just Silmarillion.**

## Likely element mapping (verify against the live XAML — not a substitute for your own classification)

| Element | → treatment |
|---|---|
| Title + item icon | `FactTitleStyle` + the fixed-40px title-glyph (`FactTitleGlyphStyle`) — items carry an icon |
| Skill-requirement pill (`Endurance 65`) | de-boxed **inert Fact** → `FactTable` (Strip), no box/gold |
| Effect lines (icon + text) | **inert Fact** — `FactBodyStyle` / `FactTable` as the shape fits (G-b: no gold values) |
| "Used as" cluster (distill / glamour / pockets / "View all N") | classify each: a 1:1 entity ref → `Link`; a keyword/filter/"view all N" set → `SetRef` (ratified E4 — keyword chips are Set-reference, **not** Link); apply the canonical table's Set-ref summary/tag rules |
| Description / flavour prose | `FactBodyStyle` |
| Section labels / inline prefixes | `StructureSectionLabelStyle` / `StructureInlinePrefixStyle` / `StructureGroupHeaderStyle` |
| Footer (`PovusSpecial0Overcoat` = InternalName) | `FactFooter` — the Item InternalName is a cross-entity reference key (recipes/quests/NPCs resolve items by it) ⇒ **copyable `KEY`**, not an inert envelope `ROW` |

Out-of-scope records (display/projection records in their own files) are
**wrapped**, not mutated — the pilot's `RecipeRequirementRowVm` idiom.

## Close the loop (part of this PR)

1. Extend the Phase-6 guardrail
   (`tests/Silmarillion.Tests/Views/DetailViewGrammarConformanceTests.cs` —
   today scoped to `src/Silmarillion.Module/Views` by design; add coverage
   for the shared `ItemDetailView` so it cannot silently regress).
2. Flip the **"Remaining grammar surface (NOT closed)"** bullet in
   `docs/silmarillion-field-coverage.md` to resolved, pointing at the grammar.

## Process & verification

1. Branch off latest `main`; this is **one coherent PR** (migration +
   guardrail extension + doc flip) — unlike Phase 5's strict one-view-per-PR,
   this is a single isolated shared surface.
2. Isolated build of `Mithril.Shared.Wpf` (+ a consumer, e.g. Bilbo) for the
   inner loop.
3. Full `dotnet build Mithril.slnx` → **0W/0E** · `XamlResourceLint OK`
   (RG1000 BAML dup-key is a known stale-obj flake — confirm via isolated
   build / `--no-build` test, don't reflexively `dotnet clean`).
4. `dotnet test Mithril.slnx` green — **all** item-detail consumers
   (Mithril.Shared.Wpf.Tests / Bilbo.Tests / Silmarillion.Tests / any
   ItemDetail tests), not just Silmarillion.
5. `scripts/start.ps1 -Build` → `Build 0W/0E` · `XamlResourceLint OK` ·
   shell shown / `=== startup done ===`. **If `MSB3026/3027 "file is locked
   by Mithril (NNNN)"`: that is the known stale-process deploy-lock —
   `taskkill //F //IM Mithril.exe`, rebuild.**
6. **Live eyeball:** open an item from a Silmarillion cross-link ("open in
   window") and confirm the item-detail window now renders in the grammar —
   no break at the navigation boundary #404 created. "tests green ≠ shipped"
   for XAML.
7. One PR, `Tracked in: #424`; G4 acceptance = maintainer consistency-review
   vs the merged pilot.

## Anti-goals (violating these reproduces the #404 debt)

1. Consistency-diff vs the merged pilot, **not** fresh design — per-PR G4 is
   a consistency review, not a redesign.
2. Do not re-open / re-decide the grammar; do not edit the Phase-4 primitives
   or `Resources.xaml` (flag-and-stop if a primitive gap appears).
3. Object/record bindings use `NullToVis`, **never** the string-only
   `NullOrEmptyToVis` (recurring repo footgun).
4. Keyword/filter/"view all N" chips are **Set-reference** (ratified E4), not
   Link; 1:1 entity refs are **Link**; stat/skill badges + effect lines are
   **inert Fact**; the InternalName footer is a copyable-`KEY` `FactFooter`.
5. The now-dead `src/Silmarillion.Module/ViewModels/RequirementChipVmConverter.cs`
   (orphaned by the #422 Quest migration) is a **separate** net-zero cleanup —
   do **not** fold it in.

## Pointers

- Worked reference (the pilot): `RecipeDetailView.xaml` +
  `RecipeDetailViewModel.cs`.
- Grammar: `docs/silmarillion-visual-grammar.md`.
- Canonical pattern table + anti-goals + cadence:
  `docs/agent-plans/2026-05-17-silmarillion-404-phase5-fanout.md`.
- The remaining-surface note + guardrail scope:
  `docs/silmarillion-field-coverage.md` §"Visual grammar (#404)".
- Provenance: the #404 program shipped via PRs #411 (pilot), #415–#422
  (fan-out), #423/#425 (Phase 6 + scoping). This view was explicitly out of
  that scope and is now its own gated effort (#424).
