# World-sim shepherd — design notebook

A per-issue delivery agent that owns one world-sim migration issue end-to-end: spawns the initial-implementation worker, opens the PR, runs the review-fix loop, and merges. The orchestrator dispatches exactly one shepherd per ready issue and walks away; the shepherd reports a structured terminal verdict via JSON when it returns.

**Pairs with:**
- [`world-simulator.md`](world-simulator.md) — the migration whose issues this delivers
- [`world-simulator-orchestration-plan.md`](world-simulator-orchestration-plan.md) — phase preconditions, dep graph, global rules
- [`world-sim-orchestrator.md`](world-sim-orchestrator.md) — the orchestrator that dispatches shepherds
- [`world-sim-migration-audit.md`](world-sim-migration-audit.md) — ground truth the specialist reviewer cross-checks

**Status:** design notebook + rationale, not implementation spec. The operational spec is the agent file [`../.claude/agents/world-sim-shepherd.md`](../.claude/agents/world-sim-shepherd.md). The v2 redesign rationale is in GitHub issue #646.

**Version:** v2 (this rewrite). The v1 shepherd was a per-PR babysitter dispatched after a PR existed; v2 owns from issue pickup through merge. The contract bugs in the original v1 design were closed by PR #645 (commit a830456); v2 (issue #646) addresses the architectural gap that PR did not touch: cold-spawned workers and reviewers every iteration.

---

## Why this exists

The world-sim migration umbrella (#601) has 15+ tasks across 5 phases. Each task → an issue → an implementation PR → a review → maybe more reviews → a merge. Doing this serially with a human in the loop is slow; doing it without per-PR judgment is reckless.

The shepherd is the per-issue agent that brings judgment to the loop without requiring a human at every step. It can:

- Read the issue body and decide whether implementation, decomposition, "nothing to do," or "I need clarification" is the right move
- Dispatch a worker to implement, hold the worker alive across review iterations via `SendMessage` (the worker accumulates context — it remembers why it made each prior decision)
- Run specialist + generic reviewers; iterate review-fix cycles up to a ceiling
- Merge the PR itself when convergence is reached
- Escalate honestly when convergence isn't possible

The orchestrator above the shepherd is a thin queue manager: pick the next ready issue, dispatch the shepherd, file the follow-ons the shepherd surfaces. The orchestrator never touches code, never calls `gh pr merge`, never spawns a general-purpose worker directly.

---

## v2 vs v1 — what changed

| Concern | v1 shepherd | v2 shepherd |
|---|---|---|
| Scope | After PR exists, review-fix-rereview | Issue pickup → initial impl → PR → review-fix → merge |
| Inputs | `pr`, `issue`, `phase`, `worker_template` | `issue`, `phase` (much smaller) |
| Worker lifecycle | Fresh `Agent` dispatch per iteration (cold) | One `Agent` initial dispatch, then `SendMessage` continuations (warm) |
| Reviewer lifecycle | Fresh `Agent` per iteration | One `Agent` per reviewer, then `SendMessage` (reviewers remember prior findings naturally) |
| Merge | Shepherd signals `ready-to-merge`; orchestrator runs `gh pr merge` | Shepherd runs `gh pr merge` itself; reports `verdict: merged` |
| Verdict enum | `ready-to-merge` / `needs-human` / `conflict` | `merged` / `needs-human` / `conflict` / `nothing-to-do` / `decomposed` |
| Follow-on transport | Parsed from PR comment by orchestrator | Carried in shepherd's return JSON (PR comment is for humans only) |
| Context loading cost | Each spawned subagent reads CLAUDE.md + required docs + issue body cold per iteration | Shepherd builds context pack once at intake, passes inline to first worker dispatch; subsequent `SendMessage`s send only the delta |

The single load-bearing constraint that forced v2's shape: per https://code.claude.com/docs/en/sub-agents, "Subagents work within a single session." `SendMessage` only works while the original parent is alive. A short-lived per-tick shepherd cannot carry subagents across orchestrator ticks. The only path to subagent context continuity is a long-lived per-issue shepherd. Cost of "long-lived": the shepherd's parent context sits idle during subagent runs — but idle wait costs zero tokens (billing is per inference, not wall-clock). So a multi-hour shepherd is cheap.

---

## Core shape

Three subagents:

**`world-sim-shepherd`** — the per-issue delivery agent. Tools: `Read`, `Grep`, `Glob`, `Bash` (constrained to `gh`), `Agent`, `SendMessage`, `ToolSearch`. No `Edit`/`Write` — the shepherd never touches code; it dispatches workers to do so.

**`world-sim-reviewer`** — the world-sim specialist reviewer the shepherd dispatches once per PR via `Agent` and then resumes via `SendMessage` on every subsequent review iteration. Four checks: principle adherence (1-13), phase-aware migration preconditions, replay-determinism inspection, audit cross-reference.

**Generic reviewer (inlined)** — the shepherd dispatches a `general-purpose` subagent with an inlined code-review prompt (canonical version in the shepherd agent file, §Generic code review prompt). Same lifecycle: one `Agent` per PR, then `SendMessage`.

---

## The shepherd lifecycle

```
phase 1: intake
  read CLAUDE.md, the three required docs, the issue body, the phase slice
  build the shepherd context pack (5-15K tokens)

phase 2: initial implementation
  Agent(general-purpose, prompt: context_pack + "implement this issue, open PR")
  capture worker_id from Agent return
  parse `outcome:` line from worker return
    - success    → verify PR opened, fall through to phase 3
    - nothing-to-do → return verdict("nothing-to-do")
    - decomposed → return verdict("decomposed", follow_ons: filed sub-issues)
    - needs-input → return verdict("needs-human", reason: "needs_input")
    - failed     → return verdict("needs-human", reason: "worker_failed")

phase 3: review-fix loop
  loop:
    pr_state = gh pr view
    short-circuit: MERGED/CLOSED/CONFLICTING → return verdict
    human-comment guard: any non-bot comment since last iteration → escalate

    if first iteration:
      review_results = parallel(
        Agent(general-purpose, prompt: generic review template),
        Agent(world-sim-reviewer, prompt: pr/issue/phase)
      )
      capture generic_reviewer_id, specialist_reviewer_id
    else:
      review_results = parallel(
        SendMessage(to: generic_reviewer_id, "re-review at SHA X"),
        SendMessage(to: specialist_reviewer_id, "re-review at SHA X")
      )

    parse <!-- generic-review-verdict: -->, <!-- world-sim-review-verdict: -->
    decide posted_verdict: ready-to-merge | dispatching worker | needs-human
      (escalation reasons: max_iterations, same_issue_class, worker_no_progress,
       unparseable reviewer output)

    gh pr comment <pr> --body-file <verdict marker + combined review prose + follow-ons>

    if posted_verdict == ready-to-merge → phase 4
    if posted_verdict == needs-human → return verdict("needs-human", reason)

    SendMessage(to: worker_id, message: review feedback delta)
    verify head_sha advanced → if not, return verdict("needs-human", "worker_no_progress")

phase 4: merge
  gh pr merge <pr> --squash --delete-branch
  verify issue auto-closed; if not, comment + manual close (log anomaly)
  return verdict("merged", merged_sha, follow_ons, anomalies)
```

**No PR-level CI.** This repo's CI runs at release-cut, not per-PR. The worker's local `dotnet build` + `dotnet test` (a hard gate per the orchestration plan's Global Rules) is the only build/test gate before push. If the worker pushes broken code, the next review iteration catches it.

**No inter-commit timeout.** Worker and reviewer `Agent`/`SendMessage` calls are synchronous — the subagent bounds its own time. Verification of "did anything change" happens via `gh pr view --json headRefOid` after the call returns.

**SendMessage is the v2 architectural commitment.** Per Claude Code docs ("Subagents work within a single session"), `SendMessage` only works while the parent (= the shepherd) is alive. The whole point of the long-lived shepherd is to keep that window open across all review iterations. Spawning a fresh `Agent` per iteration would be the v1 mistake.

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

## Invocation surface

The orchestrator dispatches the shepherd via the `Agent` tool with:

```
subagent_type: world-sim-shepherd
prompt: |
  issue: <issue#>
  phase: <phase from orchestration plan>
  max_iterations: 3
```

Minimal prompt. The shepherd builds its own context pack from the issue body + CLAUDE.md + the orchestration plan slice. The v1 design pre-assembled a `worker_template` for the orchestrator to hand in — v2 drops that, since the shepherd owns the worker lifecycle now.

---

## Files this design produced (v2)

1. `.claude/agents/world-sim-shepherd.md` — rewritten for v2 (intake, initial impl, SendMessage continuity, agent-merges)
2. `.claude/agents/world-sim-orchestrator.md` — collapsed from 5 steps to 3 (circuit breaker, cross-tick recovery, dispatch shepherd)
3. `.claude/agents/world-sim-reviewer.md` — small edit documenting SendMessage continuation behavior
4. `docs/world-sim-shepherd.md` — this file (v2 rewrite)
5. `docs/world-sim-orchestrator.md` — v2 status banner + scoped updates

Filed via: GitHub issue #646.

---

## Open considerations

1. **Concurrent shepherds on overlapping PRs.** If the orchestrator dispatches two shepherds for issues whose implementations touch the same file, the second worker's commit could clobber the first. v2's serialization (one shepherd per orchestrator tick, /loop ticks are serialized) makes this hard to hit in practice, but if /loop ever supports concurrent ticks, the orchestrator needs a mutex (see §Concurrency in the agent file).
2. **Agent ID format stability.** v2 captures worker/reviewer IDs by regex-matching `agentId:\s*([a-f0-9]+)` against the `Agent` return text. If the harness ever changes that format, the shepherd breaks. Worth a periodic spot-check.
3. **Worker checkout drift across SendMessage gaps.** The worker may be idle for hours between SendMessages (during reviewer runs). Other actors (humans, other workers in other branches) could mutate the worktree state. The fix-message template tells the worker to `git pull` before editing — but this is a convention, not a guarantee. Workers that ignore the convention can push stale commits.
4. **SendMessage cost vs Agent cost.** SendMessage continuations re-page-in the subagent's full prior context on each call. The marginal cost is small relative to a fresh Agent call (which would re-read CLAUDE.md + docs), but it isn't zero. Worth measuring over a real session to confirm the v2 economics hold up.

---

## What this design does NOT cover

- **Multi-PR shepherding by a single agent instance.** Each shepherd dispatch owns exactly one issue → one PR. Cross-PR coordination is the orchestrator's job.
- **User-action recording for shepherd replay.** Reviewers' outputs aren't deterministic. Shepherd runs aren't replayable.
- **PR-level CI integration.** If lightweight CI ever runs on every PR push (separate from release-cut CI), the shepherd's loop will need a CI-wait check. Not designing for it now.
- **Cross-session agent persistence.** Once the shepherd returns, all subagents it spawned die. If a future Claude Code release adds cross-session agent IDs (see the "Agent Teams" docs reference), the orchestrator could in principle keep workers alive across tick boundaries. Not designing for it now.

---

## References

- World-sim architecture umbrella: [#601](https://github.com/moumantai-gg/mithril/issues/601)
- v2 redesign issue: [#646](https://github.com/moumantai-gg/mithril/issues/646)
- v1 contract-bug fix: PR #645 / commit a830456
- Foundation umbrella: [#614](https://github.com/moumantai-gg/mithril/issues/614)
- Orchestration plan: [`world-simulator-orchestration-plan.md`](world-simulator-orchestration-plan.md)
- Component audit: [`world-sim-migration-audit.md`](world-sim-migration-audit.md)
- Subagent lifecycle docs: <https://code.claude.com/docs/en/sub-agents>
- Mithril Roadmap Project: <https://github.com/orgs/moumantai-gg/projects/1>
