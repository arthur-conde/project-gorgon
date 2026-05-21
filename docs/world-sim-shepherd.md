# World-sim shepherd — design notebook

A per-PR babysitter agent the world-sim orchestrator hands a PR to. The shepherd reviews the PR, dispatches workers to address findings, and signals back to the orchestrator when the PR is ready to merge or needs human attention.

**Pairs with:**
- [`world-simulator.md`](world-simulator.md) — the migration whose PRs this reviews
- [`world-simulator-orchestration-plan.md`](world-simulator-orchestration-plan.md) — the orchestrator whose dispatch loop this slots into
- [`world-sim-migration-audit.md`](world-sim-migration-audit.md) — ground truth the specialist reviewer cross-checks PRs against

**Status:** design notebook, not implementation spec. The finalized implementation plan lives in the GitHub issue filed against the world-sim umbrella (#601).

---

## Why this exists

Before this design landed, the orchestration plan defined the dispatch loop for workers but left review undefined. Its Stop Condition #2 referenced the generic `code-review` skill as one input, but didn't say who ran it, when, or how the orchestrator consumed the result. The plan also didn't model the iterative "review → fix → re-review" cycle the orchestrator otherwise expected a human to drive.

The shepherd fills that gap. It owns one PR end-to-end: runs reviewers, dispatches workers to address findings, escalates to a human only when the PR can't progress hands-free.

---

## Core shape

Two new subagents and one set of edits to the orchestration plan.

**`world-sim-shepherd`** — the per-PR babysitter. Owns the review-fix-rereview loop for one PR. Tools: `Read`, `Grep`, `Glob`, `Bash` (constrained to `gh`), `Agent`.

**`world-sim-reviewer`** — the world-sim specialist reviewer the shepherd dispatches each iteration. Takes a PR# + phase context, returns structured findings against four specializations:

1. **Principle adherence** — checks against the 13 numbered principles in [`world-simulator.md`](world-simulator.md). No cross-source services (principle 3); no `_time.GetUtcNow()` leaks in state-decision paths (principle 12); folder/composer/producer kinds respected (principle 10); frames stamped at event-time, not synthesis-time (principle 1); etc.
2. **Phase-aware migration checks** — knows which phase the PR's issue belongs to via the orchestration plan's dependency graph and verifies preconditions. A Phase 3 PR should consume the view, not the pre-split service; a Phase 4 PR should not introduce new `_time.GetUtcNow()` in state paths.
3. **Replay-determinism inspection** — static-analysis-style sweep for non-determinism sources: `DateTime.UtcNow`/`Stopwatch` in state-decision paths, dictionary-iteration-order assumptions, `Task.Run` / `Task.WhenAll` that could reorder side effects, etc.
4. **Audit cross-reference** — cross-checks the PR's changed files against [`world-sim-migration-audit.md`](world-sim-migration-audit.md). If a file is listed as "needs behavioural change" and this PR is supposed to land that change, verify the change happened; if "sleeper blocker," verify it was addressed.

**Generic review.** The shepherd also dispatches the existing `code-reviewer` subagent from `pr-review-toolkit` in parallel with `world-sim-reviewer` each iteration. The pr-review-toolkit reviewer covers generic bug-scanning, CLAUDE.md adherence, git-history context — work the specialist doesn't need to reproduce.

---

## The shepherd loop

```
state = { iterations: 0, last_head_sha: null, last_verdict: null }

loop {
  read PR state (gh pr view --json …)
  if PR closed/merged                       → exit verdict matching state
  if new human review comment since last    → exit needs-human
                                              (never bulldoze human input)

  run reviewers in parallel:
    - code-reviewer (pr-review-toolkit, generic)
    - world-sim-reviewer (specialist)
  post combined review comment on PR

  if both reviews clean                     → exit ready-to-merge

  iterations++
  if iterations > MAX_ITERATIONS (3)        → exit needs-human
  if same class of issue as last iteration  → exit needs-human
                                              (worker isn't addressing feedback)

  dispatch worker via Agent tool with review feedback as input
    (Agent call blocks until worker returns — worker bounds its own time)

  if PR head SHA unchanged after worker     → exit needs-human
                                              (worker claimed done, didn't push)
}
```

**No CI polling.** PR-level CI doesn't exist in this repo (CI runs at release-cut). The worker's own local `dotnet build` + `dotnet test` (a hard gate per the orchestration plan's Global Rules) is the only build/test gate before push. If the worker pushes broken code, the next review iteration catches it.

**No inter-commit timeout.** The shepherd's call to `Agent` to dispatch the worker is synchronous — the worker runs to completion, returns, then the shepherd verifies via `gh pr view` that new commits actually landed. The worker bounds its own time.

**"Same class of issue" detection.** Two cheap string-matching heuristics, either triggers escalation:
- A file:line range from iteration N's review appears again in iteration N+1's review
- A specific principle number (e.g., "principle 12 — wall-clock leak") cited in two consecutive iterations

---

## Termination policy

The shepherd exits in exactly one of these states. The verdict is structured (see Output contract); the human-readable summary accompanies it.

| Verdict | Triggers |
|---|---|
| `ready-to-merge` | Both reviewers clean, no open human comments, PR open and not closed |
| `needs-human` | `max_iterations` exceeded; `human_review` comment posted; `same_issue_class` detected; `worker_no_progress` (head SHA unchanged after worker dispatch); `closed_without_merge` (PR closed by a human without merging) |
| `conflict` | Merge conflict against base that auto-rebase can't resolve |

`MAX_ITERATIONS = 3` by default. The orchestrator can override per dispatch (e.g., higher for risk-high PRs). Three rounds of review-fix matches the empirical pattern that workers either converge fast or are stuck.

---

## Output contract

The shepherd's final message includes a fenced JSON block the orchestrator parses:

```json
{
  "verdict": "ready-to-merge | needs-human | conflict",
  "pr": 612,
  "issue": 611,
  "head_sha": "abc1234...",
  "iterations": 2,
  "escalation_reason": "max_iterations | human_review | same_issue_class | worker_no_progress | merge_conflict | closed_without_merge | null",
  "summary": "1-2 sentences for the orchestrator log"
}
```

Plus human-readable prose the agent emits as its return value (which the orchestrator surfaces verbatim in its escalation message when `verdict == needs-human`).

**Per-iteration PR comment trail.** Each iteration posts a comment on the PR shaped like:

```
### Shepherd iteration N — review verdict
Generic review (pr-review-toolkit/code-reviewer): [inline or link]
World-sim specialist (world-sim-reviewer): [inline or link]
Verdict: dispatching worker | ready-to-merge | needs-human
```

So a human reading the PR later sees the full review history without needing the orchestrator's logs.

---

## Invocation surface

The orchestrator dispatches the shepherd via the `Agent` tool with:

```
subagent_type: world-sim-shepherd
prompt: |
  Babysit PR #NNN. Issue: #MMM. Phase: 2. Risk: high.
  Worker dispatch template: <verbatim text orchestrator would pass to a fresh worker>.
  MAX_ITERATIONS: 3.
```

The shepherd reads the issue body itself — the orchestration plan's `spawned_session_handoff_self_contained` convention means issue bodies are already fully self-contained for cold sessions, so no spec pre-digestion is needed. The orchestrator hands the shepherd just the PR + issue numbers + a worker-dispatch template the shepherd can re-use when re-spawning workers.

---

## Files this design produced

1. `.claude/agents/world-sim-shepherd.md` — shepherd subagent definition
2. `.claude/agents/world-sim-reviewer.md` — specialist reviewer subagent
3. Edits to [`world-simulator-orchestration-plan.md`](world-simulator-orchestration-plan.md):
   - **Dispatch flow.** New step between "worker opens PR" and "orchestrator dispatches next task": orchestrator hands off to the shepherd.
   - **Verification gates.** Shepherd review is now §Tier 2.5, sitting between Tier 2 (Test) and Tier 3 (System).
   - **Stop conditions.** Stop Condition #2 references the shepherd's `needs-human` verdict; the generic `code-review` skill reference is gone.

Optional follow-on (separate PR):
- `tests/Mithril.WorldSim.Shepherd.Tests` — pure-function unit tests over a `ShepherdState` decision function (loop logic extracted into a plain C# library). The Agent/GitHub plumbing isn't unit-testable; the decision logic is.

---

## Open considerations

1. **Generic reviewer wart.** The existing `/code-review` is a slash command, not a callable subagent. The shepherd uses the `code-reviewer` subagent from the `pr-review-toolkit` plugin instead, which is `Agent`-dispatchable. If `pr-review-toolkit` ever changes that subagent's contract, the shepherd's invocation needs to follow.

2. **Concurrent shepherds on overlapping PRs.** If the orchestrator dispatches two shepherds on PRs that touch the same file, the second worker's commit could clobber the first. The shepherd doesn't detect this proactively; it falls back to the `conflict` verdict if a merge conflict appears. The orchestration plan's Stop Condition #6 (conflicting concurrent work → serialize) covers the policy side.

3. **Replay-determinism inspection scope.** The specialist's principle-12 check (no `_time.GetUtcNow()` in state-decision paths) needs to know which call sites count as "state-decision." The specialist's prompt should include the audit's classification — the [`world-sim-migration-audit.md`](world-sim-migration-audit.md) doc already enumerates the 9 sites. The specialist reads the audit per invocation and uses it as ground truth for which sites are still allowed.

---

## What this design does NOT cover

- **Implementation details** for the shepherd's loop or the reviewer's prompt. Those go in the GitHub issue body filed against #601.
- **PR-level CI integration.** If lightweight CI ever runs on every PR push (separate from release-cut CI), the shepherd's loop will need a CI-wait check. Not designing for it now.
- **Multi-PR shepherding by a single agent instance.** Each shepherd dispatch owns exactly one PR. Cross-PR coordination is the orchestrator's job.
- **Auto-merge.** The shepherd signals `ready-to-merge`; the orchestrator (or you) does the actual `gh pr merge`. This matches the orchestration plan's "do NOT auto-merge past stop conditions" guidance.
- **User-action recording for shepherd replay.** The shepherd's decisions aren't deterministic over PR state (the reviewers' outputs aren't deterministic). Replay of a shepherd run isn't a goal.

---

## References

- World-sim architecture umbrella: [#601](https://github.com/moumantai-gg/mithril/issues/601)
- Foundation umbrella: [#614](https://github.com/moumantai-gg/mithril/issues/614)
- Orchestration plan: [`world-simulator-orchestration-plan.md`](world-simulator-orchestration-plan.md)
- Design notebook: [`world-simulator.md`](world-simulator.md)
- Component audit: [`world-sim-migration-audit.md`](world-sim-migration-audit.md)
- Generic reviewer subagent: `pr-review-toolkit/agents/code-reviewer.md`
- Mithril Roadmap Project: <https://github.com/orgs/moumantai-gg/projects/1>
