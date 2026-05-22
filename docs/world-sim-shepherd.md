# World-sim shepherd — design notebook

> **Vocabulary:** see [`docs/glossary.md`](glossary.md) for definitions of the world-sim terminology used in this doc.

A single agent at /loop depth that drives the world-sim migration umbrella (#601) end-to-end. Per tick: pick the next ready issue from the dep graph, dispatch a worker + reviewers as named teammates via Teams + SendMessage, iterate the review-fix loop, merge the PR itself, file follow-ons, and schedule the next tick. Returns a structured verdict via JSON each tick (delivery outcome or `idle` / `no-action` / `circuit-breaker`).

**Pairs with:**
- [`world-simulator.md`](world-simulator.md) — the migration whose issues this delivers
- [`world-simulator-orchestration-plan.md`](world-simulator-orchestration-plan.md) — phase preconditions, dep graph, global rules
- [`world-sim-migration-audit.md`](world-sim-migration-audit.md) — ground truth the specialist reviewer cross-checks

**Status:** design notebook + rationale, not implementation spec. The operational spec is the playbook [`world-sim-driver-playbook.md`](world-sim-driver-playbook.md) that top-level Claude follows when `/world-sim-orchestrate-tick` fires. (v3.1 and earlier had the operational spec in `.claude/agents/world-sim-shepherd.md` as a subagent definition; v4 deleted that file and moved the playbook content here in `docs/`.)

**Version:** v4.

- **v1** (PR #627): per-PR babysitter dispatched after a PR existed. Tight scope; no autonomous queue management.
- **v2** (#646 / PR #647): expanded the shepherd to own from issue pickup through merge. Two layers: orchestrator at /loop depth dispatching shepherd at depth 2. Used `SendMessage(to: <agentId>)` for continuity — but that API doesn't work; `SendMessage` requires a named teammate in a Team scope.
- **v2.1** (#652 / PR #653): fixed the API to use Teams primitives correctly. Two layers preserved.
- **v3** (#656 / PR #658): collapsed orchestrator + shepherd into one agent at /loop depth. The v2.1 architecture needed `Agent` at depth 2 (shepherd dispatches worker), but the harness only reliably exposes `Agent` at depth 0. v3 shipped on a wrong premise (claimed depth 1) and the first live tick failed (#663) when the depth-1 shepherd tried to spawn a worker and got "Agent is not available inside subagents."
- **v3.1** (#660 / PR #662): added two resume modes — manual issue dispatch and adopt-PR. Same depth-1 architecture as v3; same depth-1 Agent restriction made it fail in the same way.
- **v4** (#666 / this file): collapsed the driver into top-level Claude itself. The slash command body no longer dispatches a subagent — it tells top-level Claude to follow the playbook directly. Workers and reviewers are spawned by top-level Claude at depth 0 (where Agent works) and land at depth 1 (where they have file/shell/MCP/Teams/SendMessage but not Agent, which they don't need). Shutdown handshake is now load-bearing before TeamDelete (empirically verified — TeamDelete with active teammates errors with `active_members`). Primary target: Desktop harness (Teams + SendMessage are live). CLI degrades to cold-spawn per iteration.

The notebook uses "shepherd" and "driver" interchangeably for the v3 merged agent — "shepherd" is the agent file name (kept for less churn); "driver" is what it does (drives the whole migration).

---

## Why this exists

The world-sim migration umbrella (#601) has 15+ tasks across 5 phases. Each task → an issue → an implementation PR → a review → maybe more reviews → a merge. Doing this serially with a human in the loop is slow; doing it without per-PR judgment is reckless.

The driver brings judgment to the loop without requiring a human at every step. Per tick it can:

- Pick the next ready issue from the dep graph (YAML phase tasks + dynamically-filed follow-ons)
- Read the issue body and decide whether implementation, decomposition, "nothing to do," or "I need clarification" is the right move
- Dispatch a worker as a named teammate to implement, hold the worker alive across review iterations via `SendMessage` (the worker accumulates context — it remembers why it made each prior decision)
- Run specialist + generic reviewers as teammates; iterate review-fix cycles up to a ceiling
- Merge the PR itself when convergence is reached
- File any out-of-scope findings as follow-on issues
- Escalate honestly via `spawn_task` chips when convergence isn't possible
- Schedule the next tick via `ScheduleWakeup`

v3 collapses what v2/v2.1 split across two layers (orchestrator + shepherd) into one layer at /loop depth. The collapse is what makes the architecture actually run in this harness — see the v3 entry in the version notes above.

---

## v3 vs v2.1 — what changed

| Concern | v2.1 | v3 |
|---|---|---|
| Layers | Two: orchestrator at /loop depth, shepherd at depth 2 | One: driver at /loop depth |
| Agent location | Shepherd needed `Agent` at depth 2 to spawn worker | All `Agent` calls happen at /loop depth (depth 1) |
| Orchestrator agent file | `world-sim-orchestrator.md` (separate) | Deleted; logic folded into shepherd's tick |
| Orchestrator notebook | `docs/world-sim-orchestrator.md` (separate) | Deleted; notes folded into this file |
| Slash command body | Dispatched `world-sim-orchestrator` | Dispatches `world-sim-shepherd` (the merged driver) |
| Slash command name | `world-sim-orchestrate-tick` | `world-sim-orchestrate-tick` (unchanged — preserved for /loop continuity) |
| Per-tick logic | Split across orchestrator (steps 0-3) and shepherd (lifecycle phases 1-4) | Unified 5-step decision logic in one agent file |
| Failure mode at depth 2 | Shepherd had Teams + SendMessage but no Agent → clean escalation, no delivery | Doesn't apply — there's no depth 2 |

The v3 collapse is mechanical, not architectural. Everything the orchestrator did (circuit breaker, idle probe, cross-tick recovery, dep-graph + ready-set, escalation chip routing, follow-on filing) is small enough to live as steps 1-5 of the shepherd's tick. The delivery sub-phases (intake / initial implementation / review-fix / merge / terminal teardown) are unchanged from v2.1.

The single load-bearing constraint that forced v2/v2.1's shape — per https://code.claude.com/docs/en/sub-agents, "Subagents work within a single session," so `SendMessage` requires a long-lived parent — is also what makes v3 work. The driver's tick IS the long-lived parent for one delivery; it lives for as long as that delivery takes (potentially hours).

---

## v2 vs v1 (historical)

| Concern | v1 shepherd | v2 shepherd |
|---|---|---|
| Scope | After PR exists, review-fix-rereview | Issue pickup → initial impl → PR → review-fix → merge |
| Inputs | `pr`, `issue`, `phase`, `worker_template` | `issue`, `phase` (much smaller) |
| Worker lifecycle | Fresh `Agent` dispatch per iteration (cold) | One `Agent` initial dispatch, then `SendMessage` continuations (warm) |
| Reviewer lifecycle | Fresh `Agent` per iteration | One `Agent` per reviewer, then `SendMessage` (reviewers remember prior findings naturally) |
| Merge | Shepherd signals `ready-to-merge`; orchestrator runs `gh pr merge` | Shepherd runs `gh pr merge` itself; reports `verdict: merged` |
| Verdict enum | `ready-to-merge` / `needs-human` / `conflict` | `merged` / `needs-human` / `conflict` / `nothing-to-do` / `decomposed` |
| Follow-on transport | Parsed from PR comment by orchestrator | Carried in shepherd's return JSON |

v2 → v2.1 was an API fix (SendMessage-by-id → Teams); v2.1 → v3 is a layer collapse.

---

## Core shape

Two subagents:

**`world-sim-shepherd`** — the driver. Runs at /loop depth. Tools: `Read`, `Grep`, `Glob`, `Bash` (constrained to `gh`), `Agent`, `SendMessage`, `TeamCreate`, `TeamDelete`, `mcp__ccd_session__spawn_task`, `ScheduleWakeup`, `ToolSearch`. No `Edit`/`Write` — the driver never touches code; it dispatches teammates to do so.

**`world-sim-reviewer`** — the world-sim specialist reviewer the driver dispatches once per PR via `Agent` (as a teammate) and resumes via `SendMessage` on subsequent review iterations. Four checks: principle adherence (1-13), phase-aware migration preconditions, replay-determinism inspection, audit cross-reference.

**Generic reviewer (inlined)** — the driver dispatches a `general-purpose` subagent with an inlined code-review prompt (canonical version in the agent file, §Generic code review prompt). Same lifecycle: one `Agent` per PR (as teammate), then `SendMessage`.

---

## The driver lifecycle (delivery sub-phases)

The pseudocode below is **step 4's delivery work** in the v3 5-step tick. The outer tick is documented in the agent file (`§The 5-step decision logic`); the delivery sub-phases below (intake / initial implementation / review-fix loop / merge / terminal teardown) are what happens once a ready issue has been picked. The structure is unchanged from v2.1; only the surrounding orchestration moved from a separate orchestrator agent into the driver's own tick.

```
phase 1: intake
  read CLAUDE.md, the three required docs, the issue body, the phase slice
  build the shepherd context pack (5-15K tokens)
  TeamCreate({team_name: "shepherd-issue-<N>", description: ...})
    if TeamCreate errors → enter degraded mode (see §Inline degraded mode in agent file)

phase 2: initial implementation
  Agent({subagent_type: "general-purpose", team_name, name: "worker", prompt: context_pack + "..."})
    (degraded mode: plain Agent dispatch without team_name — fire-and-forget)
  parse `outcome:` line from worker return
    - success    → verify PR opened, fall through to phase 3
    - nothing-to-do → terminal(verdict: "nothing-to-do")
    - decomposed → terminal(verdict: "decomposed", follow_ons: filed sub-issues)
    - needs-input → terminal(verdict: "needs-human", reason: "needs_input")
    - failed     → terminal(verdict: "needs-human", reason: "worker_failed")

phase 3: review-fix loop
  loop:
    pr_state = gh pr view
    short-circuit: MERGED/CLOSED/CONFLICTING → terminal(...)
    human-comment guard: any non-bot comment since last iteration → terminal("needs-human")

    if first iteration:
      review_results = parallel(
        Agent({subagent_type: "general-purpose", team_name, name: "generic-reviewer", ...}),
        Agent({subagent_type: "world-sim-reviewer", team_name, name: "specialist-reviewer", ...})
      )
    else:
      review_results = parallel(
        SendMessage({to: "generic-reviewer", message: "re-review at SHA X"}),
        SendMessage({to: "specialist-reviewer", message: "re-review at SHA X"})
      )

    parse <!-- generic-review-verdict: -->, <!-- world-sim-review-verdict: -->
    decide posted_verdict: ready-to-merge | dispatching worker | needs-human
      (escalation reasons: max_iterations, same_issue_class, worker_no_progress,
       degraded_mode_cannot_iterate, unparseable reviewer output)

    gh pr comment <pr> --body-file <verdict marker + combined review prose + follow-ons>

    if posted_verdict == ready-to-merge → phase 4
    if posted_verdict == needs-human → terminal("needs-human", reason)

    SendMessage({to: "worker", message: review feedback delta})
      (degraded mode forces needs-human here; cannot iterate without SendMessage)
    verify head_sha advanced → if not, terminal("needs-human", "worker_no_progress")

phase 4: merge
  gh pr merge <pr> --squash --delete-branch
  verify issue auto-closed; if not, comment + manual close (log anomaly)
  terminal("merged", merged_sha, follow_ons, anomalies)

terminal(verdict, ...):
  shutdown_request each spawned teammate via SendMessage
  TeamDelete()
  return verdict JSON
```

**No PR-level CI.** This repo's CI runs at release-cut, not per-PR. The worker's local `dotnet build` + `dotnet test` (a hard gate per the orchestration plan's Global Rules) is the only build/test gate before push. If the worker pushes broken code, the next review iteration catches it.

**No inter-commit timeout.** Worker and reviewer `Agent`/`SendMessage` calls are synchronous — the subagent bounds its own time. Verification of "did anything change" happens via `gh pr view --json headRefOid` after the call returns.

**Teams + SendMessage is the v2.1 architectural commitment.** The v2 shipped `SendMessage(to: <agentId>)` — but `SendMessage` doesn't work that way: it requires a named teammate in a Team scope. v2.1 fixes the API: `TeamCreate` at intake, `Agent({team_name, name})` to spawn teammates, `SendMessage({to: "<name>"})` to address them. Per Claude Code docs ("Subagents work within a single session"), the team — and all its teammates — live only as long as the shepherd is alive. The whole point of the long-lived per-issue shepherd is to keep that window open across all review iterations. Spawning fresh `Agent`s per iteration (without team membership) would be the v1 mistake; spawning teammates and reaching for `SendMessage(to: <id>)` was the v2 mistake. The Teams primitive in v2.1 is the actual mechanism.

---

## Termination policy

The shepherd exits in exactly one of these states. The verdict is structured (see §Output contract); a human-readable summary accompanies it.

| Verdict | Triggers |
|---|---|
| `merged` | Both reviewers clean, shepherd successfully called `gh pr merge`. Happy path. |
| `nothing-to-do` | Worker concluded the issue is already resolved or scope is obsolete. Orchestrator closes the issue. |
| `decomposed` | Worker filed sub-issues; the shepherd surfaces them in `follow_ons` with `blocks: [<this issue>]`. Orchestrator records and moves on. |
| `needs-human` | Review-fix loop hit a ceiling: `max_iterations`, `same_issue_class`, `human_review`, `worker_no_progress`, `initial_implementation_failed`, `needs_input`, `worker_failed`, `closed_without_merge`. Orchestrator adds `orchestrator-blocked` label + spawns task chip. |
| `conflict` | Merge conflict against base couldn't auto-resolve. Orchestrator escalates with a rebase-instruction chip. |

`max_iterations` defaults to 3. Three rounds of review-fix matches the empirical pattern that workers either converge fast or are stuck.

---

## Output contract

Final message includes a fenced JSON block the orchestrator parses (full schema in the agent file §Output contract). Key shape:

```json
{
  "verdict": "merged" | "needs-human" | "conflict" | "nothing-to-do" | "decomposed",
  "issue": <int>,
  "pr": <int> | null,
  "merged_sha": "<sha>" | null,
  "iterations": <int>,
  "escalation_reason": <enum> | null,
  "follow_ons": [{ "title": "...", "files": "...", "blocks": [<int>...], "body": "..." }],
  "anomalies": ["<one-line>", ...],
  "summary": "<1-2 sentences>"
}
```

Plus human-readable prose after the JSON block (which the orchestrator surfaces verbatim when escalating).

**Per-iteration PR comment trail.** Each iteration posts a comment on the PR with a first-line `<!-- shepherd-verdict: ... -->` marker. The marker is the contract for the orchestrator's cross-tick recovery (step 1). Each comment also includes the verbatim reviewer outputs and a `## Follow-ons` section (for **human visibility** — the orchestrator parses follow-ons from the return JSON, not the PR comment).

---

## Invocation surface — three modes (v3.1)

The driver supports three entry modes, all dispatched via the `Agent` tool with `subagent_type: world-sim-shepherd`. The mode is determined by which inputs are supplied.

### Autonomous tick (the /loop path)

```
subagent_type: world-sim-shepherd
prompt: |
  Run one tick of the world-sim migration driver.
  (No issue specified — the driver picks the next ready one from the dep graph.)
```

Used by `/loop /world-sim-orchestrate-tick`. Cheap probe → cross-tick recovery → dep-graph pick → full lifecycle (Phases 1-4).

### Manual issue dispatch

```
subagent_type: world-sim-shepherd
prompt: |
  issue: <issue#>
  phase: <phase from orchestration plan>
  max_iterations: 3
```

Skip the dep-graph picking; deliver this specific issue. Full Phases 1-4. Useful when a human wants to force the driver to work on something out of normal phase order.

### Adopt-PR (v3.1)

```
subagent_type: world-sim-shepherd
prompt: |
  pr: <pr#>
```

Skip the dep-graph picking AND skip Phase 2 (initial implementation). Fetch the PR, resolve the linked issue from the PR body (`Closes #N`), build the context pack against that issue, jump to Phase 3 (review-fix loop). Phase 4 (merge) runs as normal.

The worker teammate is **lazily spawned** at the first fix iteration — if the first review is clean, no worker is needed and the driver goes straight to merge. The lazy worker's dispatch prompt includes both the standard context pack AND an "adopt PR" framing telling them to `gh pr view` / `gh pr diff` / `git checkout <branch>` to familiarize themselves before applying fixes.

If the PR's body has no `Closes #N` reference, the driver escalates with `escalation_reason: pr_has_no_linked_issue`. If the PR is already MERGED or CLOSED, behavior matches the corresponding tick-mode terminals.

`pr` and `issue` are mutually exclusive. If both supplied, the driver asks for clarification.

The driver builds its own context pack from the issue body + CLAUDE.md + the orchestration plan slice. No `worker_template` is needed from the caller — the driver owns the worker lifecycle.

---

## Files this design produced (v4)

1. `.claude/agents/world-sim-shepherd.md` — **DELETED**. The driver is no longer a subagent; it's top-level Claude following the playbook.
2. `.claude/agents/world-sim-orchestrator.md` — DELETED in v3. Stayed deleted.
3. `.claude/agents/world-sim-reviewer.md` — unchanged from v2.1. Still a subagent, dispatched as a teammate by top-level Claude.
4. `.claude/commands/world-sim-orchestrate-tick.md` — rewritten in v4. Now instructs top-level Claude to follow the playbook directly; no subagent dispatch.
5. `docs/world-sim-shepherd.md` — this file (v4 banner + history).
6. `docs/world-sim-driver-playbook.md` — **NEW**. The operational spec top-level Claude reads each tick. Contains the 5-step decision logic + delivery sub-phases + mode dispatch + context pack + degraded mode + output contract + on-errors. ~915 lines.
7. `docs/world-sim-orchestrator.md` — DELETED in v3. Stayed deleted.

Filed via: GitHub issue #666.

---

## Open considerations

1. **Concurrent driver ticks on overlapping PRs.** If /loop ever supports concurrent ticks, two drivers running simultaneously could double-dispatch the same ready issue. The `orchestrator-dispatch:<N>` label is idempotent, but Agent calls aren't. No mutex today; protection lives at the /loop layer.
2. **Worker checkout drift across SendMessage gaps.** The worker may be idle for hours between SendMessages (during reviewer runs). Other actors could mutate the worktree state. The fix-message template tells the worker to `git pull` before editing — convention, not a guarantee.
3. **SendMessage cost vs Agent cost.** SendMessage continuations re-page-in the subagent's full prior context on each call. Marginal cost is small relative to a fresh Agent call (which would re-read CLAUDE.md + docs), but isn't zero. Worth measuring over a real session.
4. **Long-tick wall-clock.** A delivery in step 4 can run 1-4 hours. The driver's parent context sits idle during subagent runs — idle wait is zero-token, but /loop ticks become long-tail. If /loop has a timeout, drivers may be killed mid-delivery; need to confirm /loop's tick timeout policy.

---

## What this design does NOT cover

- **Multi-issue delivery in one tick.** Each tick delivers exactly one issue. The dep-graph filter ensures the next tick picks the next ready issue (which may be a follow-on the just-merged delivery filed).
- **User-action recording for driver replay.** Reviewers' outputs aren't deterministic. Driver runs aren't replayable.
- **PR-level CI integration.** If lightweight CI ever runs on every PR push, the review loop needs a CI-wait check. Not designing for it now.
- **Cross-tick teammate persistence.** When the driver's tick returns, all teammates die (per Claude Code's "subagents work within a single session"). Each tick is self-contained.

---

## References

- World-sim architecture umbrella: [#601](https://github.com/moumantai-gg/mithril/issues/601)
- v3 collapse issue: [#656](https://github.com/moumantai-gg/mithril/issues/656)
- v2.1 robustness pass: [#652](https://github.com/moumantai-gg/mithril/issues/652) / PR [#653](https://github.com/moumantai-gg/mithril/pull/653)
- v2 redesign issue: [#646](https://github.com/moumantai-gg/mithril/issues/646) / PR [#647](https://github.com/moumantai-gg/mithril/pull/647)
- v1 contract-bug fix: PR [#645](https://github.com/moumantai-gg/mithril/pull/645) / commit a830456
- Foundation umbrella: [#614](https://github.com/moumantai-gg/mithril/issues/614)
- Orchestration plan: [`world-simulator-orchestration-plan.md`](world-simulator-orchestration-plan.md)
- Component audit: [`world-sim-migration-audit.md`](world-sim-migration-audit.md)
- Subagent lifecycle docs: <https://code.claude.com/docs/en/sub-agents>
- Mithril Roadmap Project: <https://github.com/orgs/moumantai-gg/projects/1>
