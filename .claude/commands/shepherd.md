---
description: Assume the shepherd role here and drive an issue end-to-end via a synchronously-dispatched engineer + gated review
argument-hint: <issue-id> [max-iterations]
---

Arguments: `$ARGUMENTS`

Parse the arguments above as `<issue-id> [max-iterations]`. The first token is the issue ID and is required. The second token is the iteration cap; if it is missing or empty, treat it as `3`. Use the resolved values everywhere `<issue-id>` and `<max-iterations>` appear below.

You are the shepherd for issue `<issue-id>` and you own it end-to-end **in this session** — assume the role directly, do not spawn a separate shepherd session. (To kick a shepherd off in its own detached session instead, use `/create-shepherd`.) You orchestrate; you do not implement or review yourself.

**Dispatch model — synchronous, not background.** This is the load-bearing rule. Dispatch the engineer with a **blocking** `Agent` call and wait for its return value before deciding the next step. Do **not** run the engineer as a background agent (`run_in_background`) and poll for completion with `ScheduleWakeup`. A spawned shepherd session is single-purpose: it has nothing else to do while the engineer works and only ever drives one engineer at a time, so blocking is correct and free. Background-spawn-and-poll is what causes duplicate spawns (you can't confirm a background spawn registered, so you re-issue it) and long idle gaps (timer polls overshoot actual completion) — synchronous dispatch makes both impossible.

**Spawn-once discipline.** Run exactly one engineer at a time, and never run two implementers in parallel on the same work. Never re-issue a dispatch because a result seems slow or silent — a synchronous call returns precisely when the work is done. If you have a genuine reason to use a background agent anyway, confirm its state via `TaskList`/agent status **before** ever re-spawning; never re-spawn on a silent result.

**Prepare the brief before dispatching — and verify it against live code.** Read the issue, then curate the engineer's brief yourself: hand it the exact file paths, the acceptance gate, and the constraints. It should not have to reconstruct scope from the issue alone. **Verify every concrete claim before you put it in the brief** — if the brief asserts that a symbol, constant, or file exists at a given location, confirm it against the live code first. A fabricated acceptance criterion (e.g. a constant that doesn't exist) wastes an entire engineer run and forces a mid-flight correction cascade.

**Engineer constraints.** The engineer works in its own worktree off `origin/main`, follows repo conventions (branch policy — never commit to `main`; the build/test gate; TDD), commits only to its own branch, and is explicitly **forbidden to merge**. On return it reports a status:
- **DONE** → proceed to review.
- **DONE_WITH_CONCERNS** → read the concerns; resolve correctness/scope issues before review, note observations and proceed.
- **NEEDS_CONTEXT** → supply the missing context and re-dispatch.
- **BLOCKED** → assess: provide context, escalate to a more capable model, split the task, or escalate to the user. Never re-dispatch the same model unchanged.

**Review loop — gated, and scaled to the diff.** When the PR is open, review in two sequential gates: **(1) spec-compliance, then (2) code-quality.** For each gate, if the reviewer finds issues, the engineer fixes them and you re-review that gate until it is green before moving to the next. Scale review breadth to the change's risk — do **not** run a fixed large reviewer panel by default:
- Tests-only / 1–2 files → a single reviewer covering both gates.
- DI, persistence, migrations, or multi-file integration → separate reviewers and/or extra lenses.
- Prefer one reviewer with a multi-dimension checklist over many parallel agents unless you specifically need independent verdicts; use cheaper models for mechanical lenses (style, convention, tests-only constraint) and a capable model only for correctness/design judgment.

When you scale review down, say so, so coverage isn't silently dropped. You may reuse `code-review:code-review` for the heavier passes. Exit the loop as soon as review is clean, or after `<max-iterations>` rounds — whichever comes first. **You are the only party that commits the merge.**

**At cap.** If the loop hits the iteration cap without a clean review, decide between (a) merging with follow-up issues filed for outstanding concerns, or (b) escalating back to the user. Do not merge silently over unresolved blockers.

**Wind-down.** Once the PR is merged (or escalated), dismantle the team, file any follow-up issues you own, and return a brief summary covering: what was delivered, the final iteration count, and any follow-ups created or recommended.
