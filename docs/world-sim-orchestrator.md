# World-sim orchestrator — design notebook

An autonomous orchestrator subagent that drives the world-sim migration umbrella (#601) end-to-end: dispatches workers for ready tasks, hands open PRs to shepherds, merges ready-to-merge PRs, and escalates blocked ones via spawn-task chips. Runs as a tick-based /loop; each tick takes one action then exits.

**Pairs with:**
- [`world-simulator.md`](world-simulator.md) — the architecture the migration is delivering
- [`world-simulator-orchestration-plan.md`](world-simulator-orchestration-plan.md) — the dispatch flow this orchestrator automates
- [`world-sim-shepherd.md`](world-sim-shepherd.md) — the per-PR reviewer this orchestrator dispatches
- [`world-sim-migration-audit.md`](world-sim-migration-audit.md) — ground truth the shepherd's specialist reviewer cross-checks

**Status:** design notebook, not implementation spec. The finalized implementation plan lives in the GitHub issue filed against the world-sim umbrella (#601).

---

## Why this exists

The world-sim shepherd (shipped via [#627](https://github.com/moumantai-gg/mithril/pull/627)) owns the per-PR review-fix-rereview loop. Above the shepherd there's still a coordination gap: who dispatches the first worker for a newly-ready task? Who detects that a PR has the shepherd's ready-to-merge verdict and actually runs `gh pr merge`? Who notices that a phase has completed and unlocks the next?

Today: the human. The orchestration plan ([`world-simulator-orchestration-plan.md`](world-simulator-orchestration-plan.md)) reads "the orchestrator dispatches the worker," "the orchestrator hands off to the shepherd," "the orchestrator merges per its merge policy" — implicitly framing a human-in-the-loop or external-system caller as the orchestrator. There is no such system yet.

This design fills that gap: a `world-sim-orchestrator` subagent that runs as a tick-based /loop and automates the dispatch flow the orchestration plan already describes. The shepherd's contract doesn't change; the orchestrator becomes the shepherd's caller and the workers' dispatcher.

---

## Core shape

One new subagent + one slash command + a small note in the orchestration plan.

**`world-sim-orchestrator`** — the per-project orchestrator subagent. Tools: `Read`, `Grep`, `Glob`, `Bash` (constrained to `gh`), `Agent`, `mcp__ccd_session__spawn_task`, `ScheduleWakeup`. No `Edit`/`Write` — the orchestrator never touches code or local files; it operates on GitHub state via `gh` and dispatches subagents to do the work.

**`/world-sim-orchestrate-tick`** — a thin slash command that dispatches the orchestrator subagent via the Agent tool. Exists so `/loop /world-sim-orchestrate-tick` works cleanly; otherwise /loop would have to invoke the Agent tool directly which is awkward.

**Companion patch to the shepherd** — the existing `.claude/agents/world-sim-shepherd.md` (shipped via #627) has a small gap: its worker re-dispatch prompt doesn't include CLAUDE.md's hard tooling rules (LSP for C# work, wpf-gotchas for XAML, cross-source-correlation for fused-source consumers). The orchestrator's worker prompt needs these, so the shepherd's worker prompt should too. Bundled into the same PR.

---

## Per-tick decision logic

Each tick, the orchestrator runs this priority list and takes the FIRST applicable action, then exits with a `ScheduleWakeup`:

```
1. MERGE READY — Is there a PR whose latest shepherd comment is "Verdict: ready-to-merge"?
   → gh pr merge --squash <PR>
   → close linked task issue (auto-closed by Closes #N in PR body, but verify)
   → remove orchestrator-dispatch label from the issue
   → ScheduleWakeup in 60s (new state may unlock downstream tasks)

2. ESCALATE BLOCKED — Is there a PR whose latest shepherd comment is "Verdict: needs-human"?
   → label the issue orchestrator-blocked
   → comment on the umbrella (#601) with the shepherd's escalation reason + summary
   → call mcp__ccd_session__spawn_task with a self-contained prompt
     (PR#, issue#, escalation reason, last verdict JSON, last review findings,
      pointers to design docs) so a fresh session can resolve without
      reading the orchestrator's history
   → continue to step 3 (bookkeeping, doesn't consume the tick's "one action")

3. SHEPHERD A PR — Is there an open PR linked to a world-sim task issue that needs
   review attention? "Needs attention" = no shepherd comment yet, OR PR head SHA
   is newer than the latest shepherd comment.
   → dispatch world-sim-shepherd synchronously via Agent tool
   → shepherd runs its loop (may take 20-60 min worst-case), posts PR comments, returns verdict
   → ScheduleWakeup in 60s — next tick picks up the verdict via step 1 or step 2

4. DISPATCH WORKER — Is there a task issue with no orchestrator-dispatch label,
   no orchestrator-blocked label, and all its dep issues closed?
   → pick the issue with the highest priority (earliest phase per the
     orchestration plan's YAML §Dependency graph; ties broken by lowest issue#)
   → add orchestrator-dispatch:<issue#> label (matches existing convention)
   → dispatch a worker via Agent (subagent_type: general-purpose) with the
     prompt assembled per §Worker dispatch contract below
   → ScheduleWakeup in 60s — worker likely just pushed a PR; next tick may shepherd

5. IDLE — Nothing actionable.
   → ScheduleWakeup in 1800s (30 min) — accept cache miss for the long wait
```

**Tick scope guarantee.** Step 1 (merge), step 2 (escalate), and step 5 (idle) are sub-second bookkeeping. Real work is step 3 (shepherd) or step 4 (worker) — and the orchestrator does at most ONE of these per tick.

**No concurrent shepherds.** Tick-based serialization is enforced by the orchestrator only doing one shepherd dispatch per tick. If a shepherd's PR has unresolved findings and the worker pushes a fix between ticks, the next tick re-dispatches the shepherd for that PR.

**Cache-window pacing.** All real-work ticks schedule 60s wakeups (well inside the 5-min cache TTL). Idle ticks schedule 1800s+ (pay one cache miss, amortize over the long wait). We avoid the 300-1200s "worst-of-both" range entirely.

---

## Worker dispatch contract

When the orchestrator picks a task issue to dispatch (step 4), it assembles a worker prompt and dispatches via `Agent(subagent_type: "general-purpose", prompt: ...)`. The prompt has four parts:

**1. Issue framing.**

```
You are implementing GitHub issue #<N> for the world-sim migration. The
issue body is self-contained per the spawned-session-handoff convention —
read it fully before starting. Working directory: I:\src\project gorgon.
```

**2. Issue body (verbatim).** The orchestrator fetches the issue body via `gh issue view <N> --json body` and pastes it inline. No paraphrasing; cold sessions see what the user wrote.

**3. Hard tooling rules (CLAUDE.md derivatives).**

```
Tooling rules — these are not negotiable:
- For C# work touching >1 type, FIRST load LSP via
  `ToolSearch query: "select:LSP"` — then use it for go-to-def, find-refs,
  type info. Grep alone misses partial classes, source-generated members
  ([ObservableProperty] setters, JSON contexts), and overload signatures.
- For any *.xaml edit or new view, FIRST read docs/wpf-gotchas.md.
- For new consumers fusing Player.log + chat, FIRST read
  docs/cross-source-correlation.md.
- The PreToolUse hook blocks dotnet build/test/publish/pack while Mithril
  shell runs — close it before pushing.
```

**4. Global rules + PR-open instructions.**

```
Workflow rules:
- Feature branch off main. Never push directly to main. Never force-push.
- Commits: prefer new commits over --amend. Never --no-verify.
- Identity: arthur.conde@live.com (already configured; do not modify).
- Build verification: dotnet build Mithril.slnx must be clean.
- Test verification: dotnet test Mithril.slnx must be clean.
- Co-Authored-By: Claude Opus 4.7 (1M context) <noreply@anthropic.com>

When implementation is complete:
- gh pr create against main with title prefix matching the issue's scope
  (feat/, fix/, refactor/, etc.)
- PR body MUST include "Closes #<N>" to auto-close the issue on merge
- PR body MUST include the standard "🤖 Generated with Claude Code" trailer
- Report back to the caller with the PR number and a one-paragraph summary
```

**Worker runs to completion.** The orchestrator's Agent call blocks until the worker returns. The worker either:
- Opens a PR — orchestrator's next tick picks it up via step 3 (shepherd)
- Returns without opening a PR (blocked / failed to implement) — orchestrator detects via the Agent return message, labels the issue `orchestrator-blocked`, spawns a task chip with the failure context, and moves on

---

## Tools the orchestrator subagent has

| Tool | Why |
|---|---|
| `Read`, `Grep`, `Glob` | Read the orchestration plan's YAML dep graph; read issue/PR bodies via filesystem if needed |
| `Bash` (constrained to `gh`) | All GitHub state read/write: issue list/view/comment/edit, pr list/view/merge, label add/remove |
| `Agent` | Dispatch `general-purpose` workers and `world-sim-shepherd` subagent |
| `mcp__ccd_session__spawn_task` | Emit escalation chips for human resolution of `needs-human` verdicts |
| `ScheduleWakeup` | Schedule the next /loop tick at the appropriate cadence |

Explicitly NOT in the tool list:
- `Edit`, `Write` — orchestrator never touches local files or code
- `NotebookEdit` — not applicable
- Web tools — orchestrator stays inside the gh + Agent boundary

---

## Safety / stop conditions

The orchestrator pauses for human review under any of these conditions:

1. **Shepherd `needs-human` verdict.** Handled in step 2 of the decision logic; doesn't terminate the orchestrator but parks the affected task issue.
2. **Worker dispatch returned without opening a PR.** Same escalation path as #1 — label the issue, spawn a task chip, move on.
3. **More than 3 task issues currently `orchestrator-blocked`.** Too many escalations queued; orchestrator pauses dispatching NEW workers (steps 1+3 still run) until the human catches up.
4. **Umbrella issue (#601) acquires a `pause` label.** Manual circuit breaker. Orchestrator detects the label, exits with no `ScheduleWakeup` (loop terminates until you remove the label and restart).
5. **GitHub API errors.** Transient errors trigger a 5-minute retry tick. 3 consecutive failures escalate via spawn_task with "GitHub unreachable" context.
6. **Umbrella issue (#601) is closed.** Terminal "project done" condition. Orchestrator posts a "world-sim migration complete, orchestrator exiting" comment, calls `ScheduleWakeup` with no prompt (terminates the /loop).

**Never auto-retries past a `needs-human` verdict.** Once `orchestrator-blocked` is on an issue, it sits there until a human removes the label.

**Never force-merges.** If `gh pr merge --squash` fails (CI gate, branch protection, conflicts), the orchestrator escalates via spawn_task — never retries with `--admin` or similar.

---

## Files this design produces

1. `.claude/agents/world-sim-orchestrator.md` — the orchestrator subagent definition
2. `.claude/commands/world-sim-orchestrate-tick.md` — slash command for /loop invocation
3. Edits to [`docs/world-simulator-orchestration-plan.md`](world-simulator-orchestration-plan.md):
   - **§How an orchestrator should use this file.** New paragraph at the top pointing at the autonomous orchestrator agent: "for autonomous operation, see `.claude/agents/world-sim-orchestrator.md` — the manual instructions in this section describe what the orchestrator automates."
4. Patch to `.claude/agents/world-sim-shepherd.md`:
   - **Worker dispatch prompt.** Append the same CLAUDE.md tooling rules (LSP/XAML/cross-source) to the shepherd's worker re-dispatch prompt. Currently absent; same gap as the orchestrator's worker prompt before this design.
5. New repo labels (created via `gh label create` as a setup step in the implementation plan):
   - `orchestrator-blocked` — escalation flag the orchestrator sets on task issues
   - `pause` — manual circuit breaker on the umbrella (#601)

   The per-issue `orchestrator-dispatch:<issue#>` labels are created dynamically by the orchestrator at runtime (matching the existing convention for #604, #615).

Optional follow-ons (separate PRs):
- A small dashboard view (Bilbo? Silmarillion-style?) showing per-task orchestrator state for in-shell visibility. Not in v1 — GitHub Project board is sufficient.

---

## Open considerations

1. **Background dispatch vs synchronous dispatch.** The orchestrator dispatches workers and shepherds synchronously within a tick (the Agent call blocks). A tick that includes a step-3 or step-4 action can run for 20-60 minutes. /loop's dynamic pacing handles this — the next tick is scheduled AFTER the current one returns. If `Agent`'s `run_in_background: true` ever becomes durable across /loop tick boundaries, switching to background dispatch would let multiple PRs progress in parallel; today, sequential is the right call.

2. **Label name conventions.** The repo already has `orchestrator-dispatch:<issue#>` labels (saw #604 and #615) — created presumably for prior manual orchestration. The new orchestrator extends this convention (same prefix). The `orchestrator-blocked` label name is new; if a different convention is preferred, it's a one-line change in the orchestrator agent file.

3. **Per-task `worker_template` in the issue body.** Per the orchestration plan §Per-task orchestration metadata, each task issue's body is supposed to be a self-contained spec a cold session can act on. The orchestrator's worker prompt assembly leans on this — if some issue body is NOT self-contained, the worker will fail or produce wrong output. Mitigation: the spawned task chip on a worker failure includes the issue # so a human can fix the body and remove the blocked label.

4. **Cost.** Each shepherd dispatch is expensive (2 parallel reviewer agents per iteration, up to 3 iterations). The orchestrator dispatches shepherds aggressively — every fresh worker push triggers a re-shepherd. If cost becomes prohibitive, a debounce (don't shepherd within N minutes of a previous shepherd verdict) would help, but isn't in v1.

5. **`world-sim-shepherd` companion patch.** Bundling the shepherd's worker-prompt LSP/XAML fix in the same PR as the orchestrator is a scope-expansion. Justification: the orchestrator needs the rule, and re-using the same prompt template across the two callers (orchestrator → worker, shepherd → fix-up worker) keeps them aligned. Alternative: ship the shepherd patch as a tiny separate PR first; orchestrator follows.

---

## What this design does NOT cover

- **Multi-project orchestration.** This orchestrator is world-sim-specific. A generic project orchestrator (configurable umbrella, configurable shepherd) is a future evolution, not v1.
- **Worker model selection.** The orchestrator always dispatches `general-purpose`. If some tasks would benefit from `haiku`-cheap dispatch (small mechanical tasks) or `opus`-rich dispatch (architectural work), per-task model selection is a future enhancement. For now, `general-purpose` with the harness's default model is uniform.
- **Concurrent ticks.** /loop is single-threaded — only one tick runs at a time. If you start /loop twice, you have two orchestrators competing for the same GitHub state. The label-based state is idempotent enough that the second orchestrator would mostly see "nothing to do" each tick, but two `gh pr merge` calls on the same PR would race. Don't start it twice.
- **Decision rationale logging.** The orchestrator's per-tick decisions aren't logged anywhere persistent — they're visible only in the orchestrator's own session output during that tick. If postmortem audit becomes useful, a "tick log" comment on the umbrella issue (one append-only comment per significant action) is a natural addition.
- **Replay-determinism.** The orchestrator is a meta-agent over mutable GitHub state, not a world-sim state machine. Replay-determinism doesn't apply.

---

## References

- World-sim architecture umbrella: [#601](https://github.com/moumantai-gg/mithril/issues/601)
- Foundation umbrella: [#614](https://github.com/moumantai-gg/mithril/issues/614)
- Orchestration plan: [`world-simulator-orchestration-plan.md`](world-simulator-orchestration-plan.md)
- Shepherd design notebook: [`world-sim-shepherd.md`](world-sim-shepherd.md)
- World-sim design notebook: [`world-simulator.md`](world-simulator.md)
- Component audit: [`world-sim-migration-audit.md`](world-sim-migration-audit.md)
- Mithril Roadmap Project: <https://github.com/orgs/moumantai-gg/projects/1>
