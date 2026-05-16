# Orchestrator handoff: #318 (provenance popups) → #247 (Lorebooks)

**Role:** you are an *orchestrator* session — you sequence and dispatch implementation sessions, gate their merges, and keep the dependency graph honest. You do not implement the tabs/refactor yourself; you spin sub-sessions on the specs and review their deliverables against this plan.

**Governing docs (read, don't restate):**
- [silmarillion-1n-provenance-popups.md](silmarillion-1n-provenance-popups.md) — #318 full rationale + 5-slice execution spec + the invariant. This is the technical source of truth.
- [silmarillion-247-lorebooks-tab.md](silmarillion-247-lorebooks-tab.md) — #247 tab spec. **Stale in one section** — see *Gate B* below.
- [silmarillion-tab-cookbook.md](../silmarillion-tab-cookbook.md) — current tab scaffolding. Its navigable-summary-chip section is *being superseded by #318*; do not let a sub-session follow it for new 1:N work.

Both agent-plan docs are intentionally uncommitted (established working pattern this project — agent-plans stay local until actioned).

## Board state (verified 2026-05-15)

- `main` head: `c3a1850`. The navigable-summary-chip pattern (#312/#314/#315) is **merged and live** — and #318 supersedes it. Don't let a sub-session treat the cookbook's summary-chip section as current guidance.
- **Live bug in `main`:** effect→abilities "View all N" deep-links are dead-ends for ~96% of effect-keyword tags (dual-derivation; full analysis in the #318 doc). This is shipped breakage, not just debt.
- Open Bucket-B tabs: #247 Lorebooks, #248 PlayerTitles, #249 StorageVaults.
- Housekeeping: **#245 is still open** though PR #310 shipped the Areas tab (PR said "closes #246" only). Close #245 with a one-line pointer to #310 before dispatching new work — keeps the board honest.
- Adjacent open bugs not in this phase but on the board: #305, #306, #308, #268, #311. **#311 (Areas landmark virtualization) folds into #318** — see *Consolidation* below; close it into #318 rather than actioning separately.

## The dependency graph

```
#318 slice 1 (provenance index shape, effect→abilities)
        └─> slice 2 (shared popup control + effect→abilities migrated end-to-end; fixes the live bug)
                └─> slice 3 (cookbook supersession, IN the slice-2 PR or immediately after)
                        ├─> #247 Lorebooks  (UNBLOCKED here — needs the control + the rule, NOT slice 4)
                        └─> slice 4 (back-migrate Items "Used in" + Areas "NPCs")  ── parallel to #247
                                └─> #248 / #249 built natively on the rule
```

The load-bearing claim: **#247 depends on slices 1–3, not slice 4.** Items/Effects/Areas surfaces already work (only effect→abilities is actually broken, fixed in slice 2). Gating #247 on the full refactor serializes ~3 PRs for no correctness reason.

## Gates (enforce these before dispatching / merging)

**Gate A — before any #318 sub-session:** confirm the executing session has read the #318 doc's *invariant* and *execution plan §1* (provenance-retaining index shape). The two implementation calls left open there (reason-enum shape; multi-reason member rendering — recommended once-with-tags) are theirs to decide and document, not to escalate. Reject a slice-1 PR that flattens the union to a dedup'd set without provenance tags — that reintroduces the exact discardal the bug came from.

**Gate B — before dispatching #247:** the #247 doc's "Items that bestow this book" section still describes the *superseded* navigable-summary-chip pattern (it was amended once already this session, before #318 existed). Re-spec that section to popup-from-index per the #318 invariant **first**, in the #247 doc, then dispatch. This is the single thing most likely to be missed — the #318 doc flags it but the #247 doc doesn't yet self-reference it.

**Gate C — slice 2 merge:** the regression test that would have caught the original bug must exist (a tag present *only* in a non-primary field still appears in the popup with correct provenance). Per the #318 doc's Verification section. No green-without-that-test merge.

**Gate D — slice 3:** cookbook supersession lands *in the slice-2 PR or the immediately following PR*, never lagging. A merged slice-2 with the cookbook still describing summary-chips is a divergence that will mislead the next sub-session. Treat it as part of slice-2 "done."

## Consolidation

**#311 (virtualize Areas landmark groups) → fold into #318.** The #318 shared popup control must virtualize anyway (the ~547-row Massive Tourmaline / #259 precedent). Once Areas "NPCs in this area" migrates in slice 4, the landmark-group surface gets the virtualizing control for free. Close #311 with a pointer to #318 slice 4 rather than running it as a separate ~30-LoC PR — separate work would be thrown away by the migration.

## Dispatch plan

1. **Housekeeping pass** (you, directly — not a sub-session): close #245 → #310; close #311 → #318 with the consolidation rationale; comment on #318 noting #311 folded in.
2. **Dispatch slice 1** on the #318 doc §1. Reviewable unit: index shape + tests, no UI. Review against Gate A.
3. **Dispatch slice 2** (after slice 1 merges) — shared popup control + effect→abilities end-to-end + cookbook supersession (Gate D) + the Gate-C regression test. This PR fixes the live bug; it's the highest-value single merge in the phase.
4. **Branch the graph:**
   - **Re-spec #247 bestowing-items section** (you, directly — small focused edit per Gate B), then dispatch #247 on the corrected doc.
   - **Dispatch slice 4** (Items + Areas back-migration) in parallel — independent surfaces, separate PRs each per the slice-as-review-unit discipline (#298/#310 feedback established this).
5. **After #247 + slice 4 land:** #248/#249 built natively on the rule. Their handoffs don't exist yet — draft them only once the popup control is proven (post-slice-2), so they're written against reality, not the spec.

## What "phase done" looks like

- No synthetic `EntityKind` values remain (`RecipeIngredientKeyword`, `ItemKeyword`, `RecipeIngredientItem`, `NpcByArea`, `AbilityByEffectKeyword` deleted); their `mithril://`/history implications handled per #318 §3.
- Cookbook describes only the chip-vs-popup rule + the invariant; no summary-chip residue.
- Every 1:N surface (Items, Effects, Areas, Lorebooks) is popup-from-index with provenance; the Gate-C regression test exists per surface.
- #245, #311 closed; #318 closed; #247 shipped; #248/#249 handoffs drafted against the shipped pattern.

## Judgment calls reserved for you (don't pre-decide for sub-sessions)

- Whether slice 4 is one PR per surface or one combined (lean per-surface; combined only if the migration is mechanically identical and small — verify against the actual slice-2 control API, don't assume).
- Whether "To Query" ships in slice 2 or is deferred to a fast-follow (the #318 doc treats it as part of the model but low-stakes; if slice 2 is getting large, splitting To-Query out is legitimate — it's not correctness-critical by design).
- #248/#249 sequencing relative to slice 4 — both are downstream of the proven control; order by whichever sub-session capacity frees first.

## Anti-goals

- Do not let #247 (or #248/#249) ship on the superseded summary-chip pattern "to unblock faster" — that recreates exactly the migration debt #318 exists to stop. Gate B is non-negotiable.
- Do not bundle the whole refactor into one PR. The slice boundaries are review units; collapsing them was explicitly the failure mode the #298/#310 feedback warned against.
- Do not hand-edit the cookbook ahead of slice 3. Docs lead-or-lag code is the divergence Gate D prevents.

---

*Drafted by Claude (Opus 4.7), filed by @arthur-conde via Claude Code on 2026-05-15.*
