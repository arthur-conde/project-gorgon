# Silmarillion #404 — Phase 4 dispatch (encode the grammar as shared WPF primitives)

**Tracked in:** #404

> **For the engineering session.** Phases 0–3 and gates G1/G2/G3 are **closed and
> authoritative**. This is the Phase 4 *engineering* dispatch: turn the ratified
> visual grammar into shared, named `Mithril.Shared.Wpf` primitives — **no view
> is migrated in this phase** (that is Phase 5). Phase 4 is not a design or
> maintainer gate; it is reviewed as a normal PR. The ratified visual targets
> are **not yours to change** — re-deciding them reproduces the debt #404 exists
> to pay down.

## Where this sits

G1 (tiers) → Phase 1 (Recipe pilot) → Phase 2 (nine-view sweep) → G2 → Phase 3
(designer) → **G3 ✅ CLEARED**. The ratified grammar is on `main` at
[`docs/silmarillion-visual-grammar.md`](../silmarillion-visual-grammar.md) with
**four binding Phase-4 carry-forwards**. This dispatch produces the primitives;
**Phase 5** (call-site migration, one detail view per PR, each gated by G4
maintainer review) and **Phase 6** (conformance guardrail) are separate
dispatches. Coverage axis (#407, #408) is out of scope.

## Read first (authoritative, in order)

1. [`docs/silmarillion-visual-grammar.md`](../silmarillion-visual-grammar.md) —
   the G3-ratified grammar (Claude Design's verbatim text) **and** its
   "Phase-4 carry-forward" section (4 pins — binding).
2. [`docs/agent-plans/2026-05-16-silmarillion-404-visual-grammar.md`](2026-05-16-silmarillion-404-visual-grammar.md) — the program plan (Phase 4 = "encode the standard as a shared primitive").
3. Issue #404, the **"G1 — CLEARED"**, **"G2 — CLEARED"** (`4468464536`), and
   **"G3 — CLEARED"** (`4468731461`) comments — principles P1/P2, the
   availability corollary, the ratified findings.
4. The specimen harness (`explorations/phase-3-visual-grammar.html` in the
   design-system bundle) — the visual source of truth for pixel parity. It is
   **not** in this repo; treat it as reference, not a file to maintain.

## Deliverable

Shared, named primitives in `Mithril.Shared.Wpf` (with a default style in
`Resources.xaml`), **one per tier**, plus their unit tests and harness parity.
**No detail view edited.** Call sites express *which tier*, never raw pixels.

| Primitive | Tier | Encodes |
|---|---|---|
| `<mithril:Link/>` | Link | V2: 12px **lead** Lucide glyph + gold (`--accent`) name, **no box, no surface at rest**. **Subsumes `EntityChip` and `ItemSourceChip`** (carry-forward — one primitive, not a fork). Optional bits: provenance suffix (italic `--fg-quaternary`), kind label, **degrade flag**. Degrade (G-c, both directions): rest **identical** to shipped (zero schedule-leak); hover swaps nav-tint → neutral tint + trailing `⧉`; click copies canonical name + toast. Type-coded glyph table is in the grammar doc. |
| `<mithril:SetRef/>` | Set-reference | Carry-forward #4: **one shape-flexible** primitive — *summary-form* (`Crystal · 150 →`, count+arrow optional) **and** *tag-form* (bare keyword chip, no count/arrow/glyph). Blue `--info` chip, `--radius-sm`. **Availability corollary:** an unwired tag-form Set-ref still renders blue Set-ref — **never** the inert grey Fact pill (today's StorageVault keyword tags are that forbidden degrade). |
| `<mithril:FactTable/>` | Fact | Carry-forward #3: **one polymorphic** Fact-group — horizontal dot-strip ↔ vertical 2-col grid ↔ **single flat scalar** (`16 slots`), one layout-switchable primitive, not a fork. `Capacity` is polymorphic (table/flat/range/event-gated); decide which shapes it renders vs. plain Fact body lines. Fact-**inert**; **no gold** on values (G-b). |
| `<mithril:FactFooter/>` | Fact | G-a: footer ID strip **below a `--border-faint` divider** (location-not-chassis), no surface at rest. 0/1/2 identifiers (hidden/single/dot-separated). `KEY` (cross-entity ref / InternalName/title) copyable — hover-revealed 11px `copy` glyph + toast; `ROW` (EnvelopeKey-style) **inert** (`cursor:default`, no hover, no glyph). |
| Fact-title / stat / body | Fact | Likely shared **styles**, not controls: title = Cambria 18pt 600 `--accent` (the only gold-text-without-glyph); stat/body/meta per the weight axis. No box, no surface. |
| Structure | Structure | Shared **style**: `--fg-tertiary`/`--fg-quaternary`, tracked uppercase (0.08em), 9–9.5pt, **never gold, no glyph**. Section-label vs inline-prefix variants. Lowest stakes. |
| Control | Control | Already the only tiered chassis in the system — confirm tokens align with the grammar; **no new primitive expected**, just reconcile the shared style. |

## Token reconciliation (a real, explicit step — do not skip)

The grammar is written in design-system tokens (`--accent #D4A847`,
`--info #88B0E0`, `--fg-primary/secondary/tertiary/quaternary`, `--bg-surface`,
`--border-faint/subtle/strong`, `--radius-sm/md`). Phase 4 must map each to the
actual `Mithril.Shared.Wpf/Resources.xaml` brush/resource keys, **adding the
missing ones** (e.g. an explicit `--info` blue and the `--fg-quaternary` ramp
step likely do not exist yet). The design tokens are the source of truth; the
WPF resource set is reconciled to them, not vice-versa. List every added/renamed
resource key in the PR description.

## Binding constraints (from G2/G3 — not re-openable)

- Link = V2; **V5 rejected**; never V1 (always a lead glyph) or V3 (no dotted
  underline). Fact reads **inert**; Set-reference is **blue, not gold**, and
  non-confusable with Link; Structure never gold. P2: weight ⊥ tier.
- The **four carry-forwards are binding**, not advisory: one Link subsuming
  both legacy chips; one polymorphic FactTable incl. flat scalar; one
  shape-flexible SetRef incl. unwired-tag availability; `×N` quantity is
  adjacent Fact (never inside Link); `TSysCraftedEquipment` Effects-stub
  placement is **deferred to #214** — do not implement it here.

## Mithril-specific landmines (verify, don't rediscover)

- A new shared lib/type must be reachable: confirm `Mithril.Shell` (and any
  non-project-ref'd module) resolves the primitives — non-ProjectReference'd
  modules don't get shared-lib deps staged (boot stalls look like a hang).
- `NullToVis` vs `NullOrEmptyToVis`: the latter is **string-only** (`value as
  string`) and silently collapses object/record bindings — bitten repeatedly.
- Bound collection mutations on the UI thread only; `ObservableCollection<T>`
  for bound add/remove; `<Run Text="{Binding…}">` needs explicit
  `Mode=OneWay`; explicit `ContentTemplate` instantiates even for null content.
- "Tests green ≠ shipped": a stale module DLL can survive a rebuild when
  Mithril/VS holds a file lock (`MSB3026/27`), and the installed single-instance
  mutex can mask a worktree build. **Acceptance includes launching the shell,
  watching `boot.log` advance past module load, and eyeballing the primitives
  in the harness parity view** — not just `dotnet test`.
- `RG1000 'same key … *.baml'` on `dotnet build Mithril.slnx` is a known
  stale-obj/parallel flake — confirm via isolated build, don't reflexively
  `dotnet clean`.

## Acceptance

- Primitives + default styles in `Mithril.Shared.Wpf`; **no `*DetailView.xaml`
  changed** (a guard test asserting detail views still bind the legacy controls
  until Phase 5 is acceptable and encouraged).
- `dotnet build Mithril.slnx` clean (warnings-as-errors); `dotnet test
  tests/Silmarillion.Tests` and the `Mithril.Shared.Wpf` tests green.
- Harness-parity check: each primitive matches the ratified specimen at the
  documented tokens; degrade/availability states demonstrated.
- Live-shell smoke per the landmine above.
- PR lists every reconciled/added design-token → WPF resource key.

## Anti-goals (violating these reproduces the debt)

1. **Do not migrate any detail view.** Primitive + tests + harness only.
2. **Do not change a ratified visual target.** G3 is the designer's; settled.
3. **Do not fork.** One primitive per tier — the entire point. Two controls
   doing one tier's job is the exact debt (`EntityChip`/`ItemSourceChip`).
4. **Do not improvise a carry-forward cell** — they are decided in the doc.
5. **Do not touch the coverage axis** (#407/#408) or implement the #214-bound
   Effects-stub placement.

## Pointers

- Primitives + legacy: `src/Mithril.Shared.Wpf/` (`EntityChip`,
  `ItemSourceChip`, `DetailExportHost`, `Resources.xaml`).
- Ratified target: [`docs/silmarillion-visual-grammar.md`](../silmarillion-visual-grammar.md) (+ its Phase-4 carry-forward).
- Phase 3 provenance: [`2026-05-17-silmarillion-404-phase3-design.md`](2026-05-17-silmarillion-404-phase3-design.md) (EXECUTED).
