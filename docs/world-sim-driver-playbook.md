> **Vocabulary:** see [`glossary.md`](glossary.md) for definitions of the world-sim terminology used in this doc.

# World-sim driver playbook (v4)

> **Status (2026-05-23):** umbrella [#601](https://github.com/moumantai-gg/mithril/issues/601) closed; the migration drained the orchestrator's queue. This playbook remains operational for any future world-sim work tracked under [#700](https://github.com/moumantai-gg/mithril/issues/700) — notably the release-cycle-gated [#732](https://github.com/moumantai-gg/mithril/issues/732) and any opportunistic `IPlayer*` rename work. The cheap-probe short-circuit in the slash command body keeps the cost of idle ticks negligible.

This is the playbook **you (top-level Claude at depth 0)** follow when `/world-sim-orchestrate-tick` finds work and reads you. You are the driver. You read GitHub state, pick the next ready issue (if any), deliver it from initial implementation through merge, file follow-ons, and call `ScheduleWakeup` for the next tick.

**You are NOT a subagent.** v4 collapsed the driver into top-level Claude itself (depth 0, where `Agent` works). Earlier versions wrapped this work in a subagent at depth 1 where `Agent` doesn't work. Design rationale lives in [`world-sim-shepherd.md`](world-sim-shepherd.md) (the design notebook, kept under the historical filename) and `scratch/desktop-harness-probe.md`. This playbook is operational only.

**You communicate with worker/reviewer teammates as "team-lead".** That is your name in the team scope, regardless of what the team itself is called. When you instruct a teammate to send you a message, the recipient is always `team-lead` — never "shepherd", "world-sim-shepherd", "driver", or any other label. Teammates that send to the wrong recipient name silent-succeed into a dead-letter and the driver never sees their message.

**Environment**: Desktop is the primary target (Teams + SendMessage provide live continuity). CLI degrades to cold-spawn per iteration — see §Inline degraded mode.

You do NOT edit code in the driver tick — the worker teammate does. You DO call `gh pr merge` when convergence is reached.

## How this playbook is reached

The slash command body (`.claude/commands/world-sim-orchestrate-tick.md`) handles the cheap probe and idle short-circuit. If the probe finds no actionable work, the slash command body schedules the next tick and exits — this playbook is **never read**, saving ~30K tokens per idle tick.

You're reading this only because the slash command body found one of:
- A ready issue in the dep graph (step 4 — pick + deliver)
- A `needs-human` marker on an open PR without `orchestrator-blocked` (step 3 — cross-tick recovery)
- An explicit `issue` or `pr` input on this invocation (manual-issue or adopt-pr mode)

The slash command body has already run the cheap probe (open-issue union of `module:world-sim` labels + YAML phase-task IDs). **You inherit that probe's results as state — don't re-run it.**

## Inputs

When `/world-sim-orchestrate-tick` fires you, no specific issue is supplied — you pick one each tick from the dep graph. Three optional inputs control the entry mode:

- (none) — **autonomous tick mode.** Pick the next ready issue from the dep graph and deliver it. This is what /loop dispatches.
- `issue` — **manual issue dispatch mode.** Skip the dep-graph picking; deliver this specific issue. For manual debugging or human-directed work.
- `phase` (optional in all modes) — phase classification override. If omitted, the driver looks up the phase from `docs/world-simulator-orchestration-plan.md` §Dependency graph by issue number. Issues not in the YAML (follow-ons with `orchestrator-followup` label, ad-hoc issues) have no phase — pass `null` to the context pack.
- `pr` — **adopt-PR mode.** Skip the dep-graph picking AND skip Phase 2 (initial implementation). Fetch the PR, resolve the linked issue from the PR body (`Closes #N`), build the context pack against that issue, jump to Phase 3 (review-fix loop). For taking over PRs opened by humans, crashed prior driver ticks, or external processes.
- `max_iterations` (optional, default `3`) — review-fix cycle ceiling. Applies to all modes.

`pr` and `issue` are mutually exclusive. If both supplied, ask once and wait.

## Required reads (only the docs the picked work needs)

You arrive at this playbook with the cheap probe already done. To proceed with a delivery (step 4), Read these in order — once per dispatch:

1. `CLAUDE.md` (root) — project conventions, tooling rules, identity, build commands
2. `docs/wpf-gotchas.md` — for the §Tooling rules block of the context pack
3. `docs/cross-source-correlation.md` — same: inline the rule
4. `docs/world-simulator.md` — needed by the specialist reviewer; you read it to extract the principle slice for the phase
5. `docs/world-simulator-orchestration-plan.md` — phase preconditions for the picked issue
6. `gh issue view <picked> -R moumantai-gg/mithril --json body,title,labels` — issue body for the context pack

Cross-tick recovery (step 3) and the inputs-handling sections of this playbook need **no** further reads beyond what the slash command body's probe already gathered.

## Mode dispatch (top of every invocation)

Before running the 5-step decision logic below, inspect the inputs and decide the mode:

```
if inputs.pr is set and inputs.issue is set:
  return terminal_dispatch("needs-human", reason: "ambiguous_inputs",
                           summary: "Both `pr` and `issue` were supplied; ask the caller.")

if inputs.pr is set:
  mode = "adopt-pr"
  # Resolve the linked issue from the PR body.
  pr_view = gh pr view <inputs.pr> -R moumantai-gg/mithril --json body,state,headRefOid,baseRefOid
  if pr_view.state == "MERGED":
    return terminal_dispatch("merged", merged_sha: <head>,
                             anomalies: ["PR was already merged when adoption requested"])
  if pr_view.state == "CLOSED":
    return terminal_dispatch("needs-human", reason: "closed_without_merge",
                             summary: "PR was closed (not merged) before adoption could start.")
  resolved_issue = parse_closes_keyword(pr_view.body)  # case-insensitive: Closes/Fixes/Resolves #N
  if resolved_issue is null:
    return terminal_dispatch("needs-human", reason: "pr_has_no_linked_issue",
                             summary: "PR #<inputs.pr> body has no Closes/Fixes/Resolves #N reference.")
  state.mode = "adopt-pr"
  state.pr = inputs.pr
  state.issue = resolved_issue
  state.phase = look_up_phase_from_orchestration_plan(state.issue)
  # Skip the 5-step decision logic — jump straight to step 4 with this pre-populated state.

elif inputs.issue is set:
  if inputs.phase is null:
    ask once, wait
  state.mode = "manual-issue"
  state.issue = inputs.issue
  state.phase = inputs.phase
  # Skip steps 1-3, jump to step 4 with this pre-populated state.
  # (Step 4 will still run the dispatch-label check and dep-graph filter, but
  # picks the pre-supplied issue rather than searching the ready set.)

else:
  state.mode = "tick"
  # Run the full 5-step decision logic below.
```

The 5-step decision logic that follows is the **tick-mode entry path**. In `manual-issue` and `adopt-pr` modes, jump directly to step 4 with `state.issue` (and `state.pr` for adopt-pr) pre-populated; steps 1-3 don't apply because the caller has already named the target.

(Step 4's blocked-issue limit and `orchestrator-dispatch:<N>` label additions still apply — the manual modes don't bypass the queue's safety rails. They just bypass the *picking* logic.)

## The 5-step decision logic (tick-mode path)

Run this priority list each tick. Take the FIRST applicable action, then exit per §ScheduleWakeup invariant.

> **Steps 1 and 2 are primarily handled by the slash command body** (`.claude/commands/world-sim-orchestrate-tick.md`) before you read this playbook. They're documented here for completeness and as defense-in-depth — if the slash command body's pre-flight somehow missed an idle case (e.g., race condition where a new label landed between probe and decision), the playbook re-checks.

### 1. Circuit breaker

If the umbrella issue (#601) has the `pause` label:
- Exit with NO `ScheduleWakeup`. The /loop terminates until the human removes the label and restarts.
- Print: "Driver paused by `pause` label on #601. Remove the label and restart /loop to resume."

If the umbrella issue (#601) is CLOSED:
- Post a comment on #601 (use `--body-file` with a temp file): "World-sim migration complete. Driver exiting."
- Exit with NO `ScheduleWakeup`. Terminal "project done" condition.

### 2. Idle probe

After the cheap probe, if the unioned open-issue set has no entry that could be ready (e.g., all open issues have `orchestrator-dispatch:<N>` or `orchestrator-blocked`), short-circuit to idle:

- In **autonomous mode**: call `ScheduleWakeup` in 1800s, print "No actionable work this tick. Next tick in 30 minutes.", exit.
- In **manual mode** (`issue` or `pr` was supplied): this branch is unreachable — manual modes don't hit the idle probe because they skip steps 1-3.

### 3. Cross-tick recovery

The happy path is that step 4 catches a delivery's terminal verdict inline in the same tick. Step 3 only fires when step 4 didn't complete — e.g., a prior tick crashed mid-flight and left a `<!-- shepherd-verdict: needs-human -->` marker on an open PR whose issue lacks `orchestrator-blocked`.

For each open PR linked to a world-sim task issue (via `Closes #N` in PR body):

- Skip PRs whose linked issue already has `orchestrator-blocked` OR is closed (recovery already happened or PR is post-merge).
- Fetch the latest comment on the PR with `gh pr view <PR> -R moumantai-gg/mithril --json comments,state`. Find the latest comment authored by the driver. Identify by the first-line marker `<!-- shepherd-verdict: ... -->`.
- If `pr_state.state == "MERGED"` AND the linked issue is OPEN: auto-close didn't fire after a prior tick's merge. Post a comment on the issue ("Auto-close did not fire after PR #<PR> merged; closing manually."), close the issue, call `ScheduleWakeup` in 60s, exit.
- Parse the marker. If it reads `needs-human` AND the linked issue doesn't already have `orchestrator-blocked`:
  - Add `orchestrator-blocked` label via `gh issue edit <issue#> -R moumantai-gg/mithril --add-label orchestrator-blocked`.
  - Parse the prose `**Escalation reason:**` line from the same comment.
  - Post a comment on the umbrella (#601):
    ```
    Task #<issue#> blocked. PR #<PR>. Escalation reason: <reason from marker comment>.
    Driver summary: <prose from final comment>.
    Spawned task chip for resolution.
    ```
  - Call `mcp__ccd_session__spawn_task` per §Escalation prompt template.
  - Call `ScheduleWakeup` in 60s, exit.

Cross-tick recovery is rare in practice. Most ticks skip directly to step 4.

### 4. Pick winner + deliver

If no cross-tick recovery is needed, pick the next ready issue and deliver it.

**Pick:**

**Pick** (tick mode only — manual-issue and adopt-pr modes skip directly to Deliver with `state.issue` already set):

- Build the dep graph as the UNION of YAML nodes + open `module:world-sim` issues (already gathered in the cheap probe). See §Dep graph derivation below.
- Filter to the ready set:
  - Skip if the issue is closed.
  - Skip if the issue has `orchestrator-dispatch:<issue#>` label (already in flight or in cross-tick limbo).
  - Skip if the issue has `orchestrator-blocked` label.
  - Skip if any of its incoming "depends-on" edges point to an open issue.
- Sort the ready set in two tiers:
  - **Tier 1 — planned migration tasks** (in the YAML): by phase order `0a` → `0b` → `1` → `2` → `3` → `4` → `parallel`, then by issue number ascending.
  - **Tier 2 — follow-on issues** (have `orchestrator-followup` label, not in YAML): by issue number ascending.
  - Pick the first eligible entry from tier 1; only fall through to tier 2 when tier 1 is empty.
- If no winner exists, fall through to step 5 (idle).
- Look up the picked issue's phase from `docs/world-simulator-orchestration-plan.md` §Dependency graph (load this doc now — it's needed for both the phase lookup and the context-pack phase slice).

**Safety rails (all modes):**

- Check the blocked-issue limit: if more than 3 task issues have `orchestrator-blocked`, SKIP dispatch. Print "Dispatch limit hit: <N> blocked issues. Sleeping 30 minutes." Call `ScheduleWakeup` in 1800s and exit.
- Add `orchestrator-dispatch:<state.issue>` label (idempotent — re-add is a no-op for manual-issue / adopt-pr modes that may target an already-labeled issue).

**Deliver** (the actual issue delivery — sub-phases 4.1–4.5 below).

#### 4.1 Intake

```
state = {
  mode: <"tick" | "manual-issue" | "adopt-pr">,  # set by mode dispatch above
  issue: <state.issue from mode dispatch, OR picked in §Pick>,
  phase: <state.phase from mode dispatch, OR looked-up in §Pick>,
  max_iterations: inputs.max_iterations OR 3,
  team_name: "driver-issue-<issue#>",
  team_created: false,
  workers_spawned: [],
  reviewers_spawned: [],
  pr: <state.pr from mode dispatch (adopt-pr only), OR null>,
  iterations: 0,
  last_head_sha: null,
  last_iteration_at: now,
  last_review: null,
  accumulated_follow_ons: [],
  anomalies: [],
  degraded_mode: false,
}

# Build the context pack from the docs already loaded + the issue body.
# Adopt-pr mode inlines an additional "you are adopting existing PR #N"
# framing — see §Building the context pack §Adopt-pr addendum.
context_pack = build_context_pack(
  issue_body, phase_preconditions, tooling_rules, workflow_rules,
  adopt_pr = state.pr if state.mode == "adopt-pr" else null
)

# Establish the team scope so worker/reviewers can be spawned as named teammates.
try:
  TeamCreate({
    team_name: state.team_name,
    description: "Delivering issue #<state.issue> for world-sim migration"
  })
  state.team_created = true
except (tool unavailable, dispatch refused, etc.):
  state.degraded_mode = true
  state.anomalies.append("TeamCreate unavailable; operating in inline degraded mode")
  # See §Inline degraded mode.

# Adopt-pr mode: snapshot the current PR head so we can detect the worker's
# first push later. No need to spawn a worker yet — the lazy spawn happens
# in §4.3 when (if) the first review surfaces findings.
if state.mode == "adopt-pr":
  state.last_head_sha = gh pr view <state.pr> -R moumantai-gg/mithril --json headRefOid
```

#### 4.2 Initial implementation

**Skipped entirely in adopt-pr mode** — the PR already exists; `state.pr` and `state.last_head_sha` were populated in §4.1. Jump directly to §4.3 (review-fix loop).

For tick and manual-issue modes:

```
initial_prompt = build_worker_prompt(
  context_pack,
  task = "Implement this issue. Open a PR with `Closes #<issue>` in the body."
)

if not state.degraded_mode:
  worker_result = Agent({
    subagent_type: "general-purpose",
    team_name: state.team_name,
    name: "worker",
    prompt: initial_prompt
  })
  state.workers_spawned.append("worker")
else:
  worker_result = Agent({
    subagent_type: "general-purpose",
    prompt: initial_prompt
  })  # fire-and-forget — no team continuity

outcome = parse_outcome_line(worker_result.text)
match outcome:
  "success":
    state.pr = find_pr_for_issue(state.issue)
    if state.pr is null:
      return terminal_dispatch("needs-human", reason: "initial_implementation_failed",
                               summary: "Worker reported success but no PR opened")
  "nothing-to-do":
    return terminal_dispatch("nothing-to-do",
                             reason: "nothing_to_do",
                             summary: worker_result.text)
  "decomposed":
    return terminal_dispatch("decomposed",
                             reason: "decomposed",
                             follow_ons: parse_filed_sub_issues(worker_result.text))
  "needs-input":
    return terminal_dispatch("needs-human", reason: "needs_input",
                             summary: worker_result.text)
  "failed":
    return terminal_dispatch("needs-human", reason: "worker_failed",
                             summary: worker_result.text)
  null:
    return terminal_dispatch("needs-human", reason: "worker_failed",
                             summary: "Worker return text missing outcome: line")

state.last_head_sha = gh pr view <state.pr> -R moumantai-gg/mithril --json headRefOid
```

#### 4.3 Review-fix loop

```
loop:
  pr_state = gh pr view <state.pr> -R moumantai-gg/mithril
             --json state,headRefOid,reviews,comments,mergeable

  if pr_state.state == "MERGED":
    state.anomalies.append("PR was merged externally; driver did not call gh pr merge")
    return terminal_dispatch("merged", merged_sha: pr_state.mergeCommit?.sha)
  if pr_state.state == "CLOSED":
    return terminal_dispatch("needs-human", reason: "closed_without_merge")
  if pr_state.mergeable == "CONFLICTING":
    return terminal_dispatch("conflict", reason: "merge_conflict")

  # Human-comment guard — do not bulldoze human input.
  if any review or comment with created_at > state.last_iteration_at by a non-bot account:
    return terminal_dispatch("needs-human", reason: "human_review")

  # Dispatch reviewers.
  if state.degraded_mode:
    review_results = inline_review(state.pr, state.issue, state.phase)
  elif "generic-reviewer" not in state.reviewers_spawned:
    # First review iteration — parallel Agent calls in a single message,
    # both as teammates so subsequent iterations can SendMessage them.
    review_results = parallel(
      Agent({
        subagent_type: "general-purpose",
        team_name: state.team_name,
        name: "generic-reviewer",
        prompt: <generic-review template with pr=<state.pr>>
      }),
      Agent({
        subagent_type: "world-sim-reviewer",
        team_name: state.team_name,
        name: "specialist-reviewer",
        prompt: <pr=<state.pr>, issue=<state.issue>, phase=<state.phase>>
      })
    )
    state.reviewers_spawned.extend(["generic-reviewer", "specialist-reviewer"])
  else:
    review_results = parallel(
      SendMessage({
        to: "generic-reviewer",
        summary: "Re-review iteration <state.iterations + 1>",
        message: "PR #<state.pr> updated to <new_head_sha>. Re-review against the new diff."
      }),
      SendMessage({
        to: "specialist-reviewer",
        summary: "Re-review iteration <state.iterations + 1>",
        message: "PR #<state.pr> updated to <new_head_sha>. Re-review against the new diff."
      })
    )

  generic_verdict = first regex match against review_results.generic.text:
                    `<!--\s*generic-review-verdict:\s*(clean|findings)\s*-->`
  specialist_verdict = first regex match against review_results.specialist.text:
                      `<!--\s*world-sim-review-verdict:\s*(clean|findings)\s*-->`

  # parse_follow_ons extracts the "## Follow-ons" YAML-block-scalar section
  # from EACH reviewer's SendMessage body (both generic and specialist) and
  # returns a flat list of {title, files, blocks, body} entries. Section
  # delimited by `## Follow-ons` heading; entries delimited by lines starting
  # with `- title:`; `body:` uses `|` block scalar (preserve newlines).
  # Missing section means zero entries — don't error. See §Follow-on filing
  # for the file-out shape; see the reviewer agent file and §Generic code
  # review prompt for the section's authored shape.
  state.accumulated_follow_ons.extend(parse_follow_ons(review_results))

  if generic_verdict is null or specialist_verdict is null:
    posted_verdict = "needs-human"
    escalation_reason = "worker_no_progress"
  elif generic_verdict == "clean" and specialist_verdict == "clean":
    posted_verdict = "ready-to-merge"
    escalation_reason = null
  else:
    posted_verdict = "dispatching worker"
    escalation_reason = null

  if posted_verdict == "dispatching worker"
     and state.last_review is not null
     and same_issue_class(state.last_review, review_results):
    posted_verdict = "needs-human"
    escalation_reason = "same_issue_class"

  if posted_verdict == "dispatching worker"
     and state.iterations + 1 > state.max_iterations:
    posted_verdict = "needs-human"
    escalation_reason = "max_iterations"

  if state.degraded_mode and posted_verdict == "dispatching worker":
    posted_verdict = "needs-human"
    escalation_reason = "degraded_mode_cannot_iterate"

  gh pr comment <state.pr> -R moumantai-gg/mithril --body-file <temp-file>

  if posted_verdict == "ready-to-merge":
    # fall through to phase 4.4 (merge)
    break
  if posted_verdict == "needs-human":
    return terminal_dispatch("needs-human", reason: escalation_reason)

  # Otherwise dispatch worker fix.
  state.iterations += 1
  state.last_review = review_results

  fix_message = build_worker_fix_message(pr=<state.pr>, feedback=review_results)

  if state.degraded_mode:
    return terminal_dispatch("needs-human", reason: "degraded_mode_cannot_iterate")

  if state.mode == "adopt-pr" and "worker" not in state.workers_spawned:
    # Lazy worker spawn — adopt-pr skipped Phase 2 (no initial implementation),
    # so the worker teammate doesn't exist yet. Spawn now, briefed on the
    # existing PR state PLUS the review feedback.
    adopt_prompt = context_pack
                 + "\n\n### Adopting existing PR #" + state.pr + "\n"
                 + "You did NOT open this PR. The branch already exists. Run\n"
                 + "  gh pr view " + state.pr + " -R moumantai-gg/mithril\n"
                 + "  gh pr diff " + state.pr + " -R moumantai-gg/mithril\n"
                 + "  git fetch && git checkout <pr-branch>\n"
                 + "to familiarize yourself with the current state. Then address\n"
                 + "the review feedback below.\n\n"
                 + fix_message
    worker_result = Agent({
      subagent_type: "general-purpose",
      team_name: state.team_name,
      name: "worker",
      prompt: adopt_prompt
    })
    state.workers_spawned.append("worker")
  else:
    # Standard SendMessage continuation — worker teammate already exists.
    SendMessage({
      to: "worker",
      summary: "Address review feedback iteration <state.iterations>",
      message: fix_message
    })

  new_head = gh pr view <state.pr> -R moumantai-gg/mithril --json headRefOid
  if new_head == state.last_head_sha:
    gh pr comment <state.pr> -R moumantai-gg/mithril --body-file <temp-file>
    return terminal_dispatch("needs-human", reason: "worker_no_progress")

  state.last_head_sha = new_head
  state.last_iteration_at = now
  # loop iterates
```

#### 4.4 Merge

```
merge_result = gh pr merge <state.pr> -R moumantai-gg/mithril --squash --delete-branch
if merge_result.failed:
  return terminal_dispatch("needs-human", reason: "merge_command_failed",
                           summary: merge_result.stderr)

merged_sha = gh pr view <state.pr> -R moumantai-gg/mithril --json mergeCommit
             → .mergeCommit.oid

# Verify auto-close fired.
issue_state = gh issue view <state.issue> -R moumantai-gg/mithril --json state
if issue_state == "OPEN":
  state.anomalies.append("issue did not auto-close after merge; closing manually")
  gh issue comment <state.issue> -R moumantai-gg/mithril --body-file <temp-file>
  gh issue close <state.issue> -R moumantai-gg/mithril

return terminal_dispatch("merged", merged_sha: merged_sha,
                         follow_ons: state.accumulated_follow_ons,
                         anomalies: state.anomalies)
```

#### 4.5 terminal_dispatch helper

Every terminal_dispatch invocation (merge, escalation, conflict, nothing-to-do, decomposed) MUST:

1. **Tear down the team via the shutdown handshake** (if `state.team_created`):
   - For each spawned teammate (workers + reviewers): `SendMessage({to: name, message: {type: "shutdown_request", reason: "delivery complete"}})`.
   - Wait for `shutdown_approved` reply from each (timeout 30s per teammate).
   - The shutdown handshake is **load-bearing**, not a courtesy. Empirical Desktop probe (#666 verification, `scratch/desktop-harness-probe.md`) confirmed: `TeamDelete()` with active teammates fails with `Cannot cleanup team with N active member(s): <name>. Use requestShutdown to gracefully terminate teammates first`. Skipping the handshake means `TeamDelete` errors and `~/.claude/teams/<team_name>/` leaks.
   - After all teammates approve (or timeout), call `TeamDelete()` to remove `~/.claude/teams/<team_name>/` and the shared task list.
   - If a teammate fails to approve within timeout, append to `state.anomalies` (`"teammate <name> did not approve shutdown within 30s"`) and proceed — call `TeamDelete()` anyway. If it errors, log to stdout and exit; the OS will eventually GC the team dir, and re-running the driver on the same issue will hit a fresh team_name (per-tick UUID or per-issue scope).
2. **Apply the verdict's GitHub-state side effects:**
   - `merged`: remove `orchestrator-dispatch:<issue#>` label (issue should already be closed). File `state.accumulated_follow_ons` per §Follow-on filing. Post roll-up comment on the merged PR.
   - `nothing-to-do`: post the worker's summary on the issue, `gh issue close <issue#>`, remove `orchestrator-dispatch:<issue#>` label.
   - `decomposed`: file `state.accumulated_follow_ons` (the sub-issues) per §Follow-on filing. Remove `orchestrator-dispatch:<issue#>` label so the parent stays in the ready set (its `Depends on: #<sub>` edges will keep it filtered until subs close).
   - `needs-human`: add `orchestrator-blocked` label. Post a comment on #601 with the escalation summary. Call `mcp__ccd_session__spawn_task` per §Escalation prompt template, passing the verdict, escalation reason, and any review-comment trail context.
   - `conflict`: add `orchestrator-blocked` label. Call `mcp__ccd_session__spawn_task` with title "Resolve world-sim PR #<PR> merge conflict", a rebase-instruction prompt body.
3. **Schedule next tick** (autonomous mode only): `ScheduleWakeup` in 60s (work just completed; next ready issue may unlock). In manual mode (`issue` or `pr` was supplied as input), **skip this step** — manual invocations are one-shot.
4. **Return** — post the prose tick summary (see §Tick summary).

### 5. Idle

If steps 1-4 all found nothing actionable:

- Call `ScheduleWakeup` in 1800s.
- Print: "No actionable work this tick. Next tick in 30 minutes."
- Exit.

## ScheduleWakeup invariant

`ScheduleWakeup` is required only in **autonomous tick mode** (no `issue` or `pr` input). Manual invocations (`issue: #N` or `pr: #N` supplied) are one-shot: do the work, post the prose tick summary, exit without scheduling. Calling ScheduleWakeup from a manual invocation is either a no-op (outside /loop) or surprising re-firing behavior (inside /loop) — never desired.

**Autonomous mode rule:** every tick MUST call `ScheduleWakeup` before exiting, with exactly three exceptions:

1. Step 1 pause-label kill switch (human's only way to stop /loop)
2. Step 1 umbrella-closed terminal (project done)
3. §On errors 3-strike escalation (after the spawn_task chip for driver-down has been emitted)

In every other autonomous-mode path — successful delivery, fallback chip emission, idle, error breadcrumb + retry, unparseable subagent return — `ScheduleWakeup` is required. /loop relies on it to schedule the next tick. Missing the call silently kills the /loop chain.

**Manual mode rule:** never call `ScheduleWakeup`. The invocation is human-driven; the human will dispatch the next manual invoke themselves if they want one. Post the prose tick summary and exit.

**If in doubt about mode**: the inputs decide. `issue` or `pr` supplied → manual. Neither supplied → autonomous. The mode dispatch at the top of this playbook already sets `state.mode` accordingly.

If an autonomous tick reaches a code path that doesn't obviously match one of the documented outcomes, default to: call `ScheduleWakeup` in 1800s and exit. The worst case is a wasted 30-minute sleep — the alternative (silent /loop death) is far worse.

When in doubt in autonomous mode, schedule. When in doubt in manual mode, do not schedule.

**`ScheduleWakeup` vs `CronCreate` equivalence.** Both schedulers operate at depth -1 (harness layer) and fire fresh top-level Claude sessions at depth 0 when they trigger. `ScheduleWakeup` is the /loop self-pacing primitive — used inside /loop's dynamic mode (no fixed interval). `CronCreate` is a session-scoped cron entry that fires independently of /loop. Either works for chaining ticks; the driver uses `ScheduleWakeup` by default because /loop is the expected invocation surface. If `ScheduleWakeup` is unavailable in a given harness's depth-0 context, fall back to `CronCreate` with a short delay (e.g., `recurring: false`, fire at `now + 60s`).

## Building the context pack

Built once per delivered issue at intake, passed inline to the initial worker dispatch. Reviewers receive the same pack in their first-iteration Agent prompts. Subsequent SendMessages add only the iteration-specific delta.

Shape:

```
=== WORLD-SIM DRIVER CONTEXT PACK — issue #<N>, phase <P> ===

### Issue spec
<verbatim issue body from `gh issue view`>

### Phase preconditions (from orchestration plan)
<extracted slice from docs/world-simulator-orchestration-plan.md §Dependency graph
 for this issue's phase, plus relevant §Global rules>

### Tooling rules (non-negotiable)
- For C# work touching >1 type, FIRST load LSP via `ToolSearch query:
  "select:LSP"` — then use it for go-to-def, find-refs, type info. Grep alone
  misses partial classes, source-generated members ([ObservableProperty]
  setters, JSON contexts), and overload signatures.
- For any *.xaml edit or new view, FIRST read docs/wpf-gotchas.md.
- For new consumers fusing Player.log + chat, FIRST read
  docs/cross-source-correlation.md.
- The PreToolUse hook blocks dotnet build/test/publish/pack while Mithril
  shell runs — close it before pushing.

### Workflow rules
- Feature branch off main. Never push directly to main. Never force-push.
- Commits: prefer new commits over --amend. Never --no-verify.
- Identity: arthur.conde@live.com (already configured; do not modify).
- Build verification: dotnet build Mithril.slnx must be clean before push.
- Test verification: dotnet test Mithril.slnx must be clean before push.
- Co-Authored-By trailer: Claude Opus 4.7 (1M context) <noreply@anthropic.com>
- PR open: gh pr create -R moumantai-gg/mithril against main with title
  prefix matching the issue's scope (feat/, fix/, refactor/, etc.).
- PR body MUST include "Closes #<issue>" and the "🤖 Generated with Claude
  Code" trailer.
- Do NOT merge the PR. Stop after `gh pr create` and report `outcome:
  success` to team-lead. The driver runs reviews and calls `gh pr merge`;
  if you merge yourself, the driver's review-iteration gate is silently
  bypassed.

### Structured outcome reporting (CRITICAL — read carefully)

When you reach a terminal outcome (you've finished — either you opened a PR, concluded nothing-to-do, decomposed into sub-issues, hit a wall, or failed), you MUST report the outcome to the team-lead via `SendMessage`. Putting `outcome:` in your plain text output is NOT enough — your text output ends a turn and you go idle, but the team-lead does not see your turn output unless you explicitly send a message.

**The recipient name is literally `"team-lead"`.** Not `"shepherd"`, not `"world-sim-shepherd"`, not `"driver"`, not `"orchestrator"`, not the team name. Just the four-character string `team-lead`. The harness's `SendMessage` returns `success: true` silent-success for any nonexistent recipient name (no error, no warning), so a typo means your message goes to a dead-letter inbox and the team-lead never sees it. You can verify the lead's name by reading `~/.claude/teams/<team_name>/config.json` — the lead's `name` field is `team-lead`.

**Required final action:**

```
SendMessage({
  to: "team-lead",
  summary: "<outcome>: <one-line>",     # e.g., "success: PR #710 opened"
  message: "outcome: <enum-value>\n\n<details — PR number / rationale / questions / symptom>"
})
```

Then go idle. The team-lead waits for this SendMessage; without it, the driver doesn't know you're done and the delivery stalls until iteration ceilings escalate.

**Outcome enum values** (use the `outcome: <X>` line as the literal first line of your SendMessage body):

  outcome: success
    A PR has been opened. Report the PR number and a one-paragraph summary.
  outcome: nothing-to-do
    Reading the issue + current repo state, no work is needed.
  outcome: decomposed
    Scope too large or latent sub-tasks; you've filed sub-issues. List each on
    its own `Filed #N` line.
  outcome: needs-input
    Clarifying-question wall. List questions.
  outcome: failed
    Build/test failure you couldn't resolve, external constraint, contradicting
    requirement. Report symptom.

If your SendMessage to team-lead is missing or lacks an `outcome:` line, the driver treats your dispatch as `failed`.

**Fallback for fire-and-forget mode** (no `team_name` was set on your invocation — degraded CLI mode): if `SendMessage` to "team-lead" errors with "no team context" or similar, fall back to including the `outcome:` line as the first line of your plain text final output. The driver's degraded-mode handler parses text output instead.

=== END CONTEXT PACK ===
```

Target size: 5-15K tokens depending on issue body + phase slice size.

### Adopt-pr addendum

When `state.mode == "adopt-pr"`, the context pack inlines an additional framing block before §Structured outcome reporting:

```
### Adopting existing PR #<state.pr>

You did NOT open this PR. The branch <pr-branch> already exists and contains
prior commits. Your job is to take over from the PR's current state, address
any review findings, and push fixes to the same branch (do NOT open a new PR).

Run these on your first turn to familiarize yourself:
- gh pr view <state.pr> -R moumantai-gg/mithril --json title,body,headRefOid,baseRefOid,files
- gh pr diff <state.pr> -R moumantai-gg/mithril
- git fetch && git checkout <pr-branch>

If this is your first dispatch and you've received review feedback in the
same prompt, address the feedback now. Otherwise just familiarize yourself
and report `outcome: ready` (a no-op outcome — the driver will SendMessage
you with the first round of review feedback when reviewers complete).
```

(The "report `outcome: ready` if no feedback yet" path applies only if the driver dispatches the worker proactively before review — currently the driver uses lazy worker spawn instead, so the worker is only dispatched AFTER the first review surfaces findings, and the feedback is always in the dispatch prompt. The "outcome: ready" path is documented for future flexibility.)

## Building the worker fix message (SendMessage)

The worker teammate already has the context pack and the prior PR diff in its context from when it pushed. Re-sending the pack is redundant. The fix message is minimal:

```
### Review feedback for PR #<state.pr>, iteration <state.iterations + 1>

The reviewers flagged the following findings against your most recent push:

<inlined verbatim review_results.generic.text>

<inlined verbatim review_results.specialist.text>

Action:
- `git pull` on your PR branch before editing (other actors may have touched
  the worktree).
- Address the findings above.
- Run dotnet build + dotnet test; both must be clean.
- Push to the same PR branch (do NOT open a new PR).
- Report completion via `SendMessage({to: "team-lead", summary: "outcome:
  <X>: <one-line>", message: "outcome: <X>\n\n<details>"})`. Same outcome
  enum and same reporting requirement as the initial implementation (see
  context pack §Structured outcome reporting). Plain text output alone is
  NOT enough — the lead doesn't see your turn output, only your SendMessages.
```

## Spawning named teammates

The `Agent` tool accepts `team_name` and `name` parameters when spawning into an existing team scope. The combination establishes a persistent, addressable teammate:

```
Agent({
  subagent_type: <type>,
  team_name: state.team_name,
  name: "<role>",       # e.g., "worker", "generic-reviewer", "specialist-reviewer"
  prompt: <prompt>
})
```

After spawn, the teammate goes idle when its first turn ends. Subsequent communication is via `SendMessage({to: "<role>"})` — messages auto-deliver as conversation turns to the teammate; no inbox polling.

`SendMessage` only works while the recipient is a teammate in your active team scope. When you `TeamDelete()`, all teammates die.

## Inline degraded mode (CLI fallback)

Two scenarios trigger degraded mode:

1. **`TeamCreate` errors** at intake (tool missing from your toolset). Rare — `TeamCreate` was callable in both CLI and Desktop probes.
2. **Teams is dead-letter** (CLI scenario). `TeamCreate` and `Agent({team_name, name})` both succeed, but subagents at depth 1 don't stay alive between turns — they exit after their first prompt. SendMessage writes to disk but no live process reads the inbox. The Desktop probe (`scratch/desktop-harness-probe.md`) confirms this is CLI-specific; the Desktop harness keeps teammates alive in-process.

Detection: in CLI you can't pre-detect cheaply. The signal arrives at the first SendMessage-to-worker that should produce a push: if `gh pr view --json headRefOid` shows no advance after the SendMessage round-trip, AND the worker's inbox shows `read: false` (dead letter), Teams is dead-letter in this harness.

Set `state.degraded_mode = true` and `state.anomalies.append("teams-not-live; using cold-spawn")` (or `"operated in inline mode — Teams unavailable"` for the rarer `TeamCreate` errors).

Degraded mode behavior:

- **First-encounter mid-delivery**: tear down the (now-useless) team via shutdown handshake + `TeamDelete()`. Continue the delivery in cold-spawn mode.
- **Worker dispatch**: spawn via `Agent({subagent_type: "general-purpose", prompt: <context pack + task>})` WITHOUT `team_name`. Fire-and-forget. The worker opens the PR (initial implementation) or addresses review feedback (fix iteration) and exits.
- **Fix iterations**: each iteration spawns a FRESH worker with the full context pack + current PR diff + new review feedback. Cold spawn each time. Token-expensive but functional.
- **Review iterations**: same — spawn fresh reviewers each iteration. Or, if even that's too expensive, perform the review analytically inline (single LLM pass applying the two-reviewer rubric).
- **Inline review disclosure**: PR comment posted in inline-review degraded mode MUST disclose:
  ```
  Note: this iteration was performed inline by the driver rather than via
  dispatched reviewers because the harness used by this driver run does not
  expose Teams live continuity. The two-reviewer rubric was applied
  analytically against the diff + design notebook + orchestration plan;
  findings are functionally equivalent to a properly-dispatched parallel pair.
  ```
- **Merge**: same as primary mode — `gh pr merge` yourself when convergence is reached.

This mode is a recovery path, not a goal. /loop should run in Desktop for primary deployment. Repeated `teams-not-live; using cold-spawn` anomalies in CLI are expected and informational, not a bug.

## "Same class of issue" detection

Two cheap heuristics, either triggers escalation:

1. A file:line range from iteration N's review appears in iteration N+1's review (tolerate ±5 lines for fix-up shifts).
2. A principle number (e.g., "principle 12") cited in two consecutive iterations' findings.

In practice, SendMessage-resumed reviewers naturally surface this as a finding in their text ("I flagged this last round and the worker shifted the code without fixing the root cause") — making the string-matching heuristic a backup signal rather than the primary detection. Keep both.

## Posting the combined review comment

Use `gh pr comment <pr> -R moumantai-gg/mithril --body-file <path>`. The body shape is fixed; the **first line MUST be the verdict marker** so cross-tick recovery (step 3) can parse it without grepping prose:

```
<!-- shepherd-verdict: ready-to-merge | dispatching worker | needs-human -->
### Driver iteration N — review verdict

**Generic review**:
<verbatim generic-review output, indented one level>

**World-sim specialist** (`world-sim-reviewer`):
<verbatim specialist output, indented one level>

**Verdict:** ready-to-merge | dispatching worker | needs-human
**Escalation reason:** <max_iterations | same_issue_class | worker_no_progress | degraded_mode_cannot_iterate>
                       (omit this line unless Verdict is needs-human)

## Follow-ons (out-of-scope findings — accumulated during review iterations,
                filed as GitHub issues at merge time per §Follow-on filing)

- title: <one-line summary>
  files: <comma-separated file:line refs>
  blocks: [<comma-separated issue numbers, or empty>]
  body: |
    <multi-line prose body>

- title: ...
  ...

— posted by world-sim driver
```

In degraded mode, append the disclosure note (see §Inline degraded mode) inside the iteration comment so readers know which mode produced the findings.

Use a temp file for the body (per `bash_tool_is_posix_not_powershell` memory: `gh ... --body` with multiline trips Bash quoting).

## Generic code review prompt

This is the **canonical** generic-review prompt — no separate `.claude/agents/*` file backs it. If you need to update the rubric, edit it here.

When dispatching the generic reviewer via `Agent` on the first iteration (with `team_name` + `name: "generic-reviewer"`), use this template:

```
You are doing a generic code review of a single PR.

PR: #<N>

Read (first iteration only — on subsequent SendMessage continuations, this
context is already loaded):
- `gh pr view <N> -R moumantai-gg/mithril --json title,body,files,headRefOid,baseRefOid`
- `gh pr diff <N> -R moumantai-gg/mithril`
- The root CLAUDE.md and any CLAUDE.md files in directories the PR touches

Check:
- Bugs (logic errors, null handling, race conditions, off-by-one)
- CLAUDE.md compliance (project conventions, import patterns, error handling, naming)
- Significant code-quality issues (duplication, missing critical error handling)

Filter aggressively — confidence ≥ 80 only. Standard false-positive filters apply:
- Pre-existing issues in main, not in this diff
- Linter / typechecker / compiler concerns (CI catches these)
- Lines the PR did not modify
- Issues silenced explicitly in code (e.g., lint-ignore comments with justification)

Output format (FIRST line MUST be the machine-readable marker):
<!-- generic-review-verdict: clean | findings -->
### Generic code review — PR #N

**Verdict:** clean | findings

[For each issue: file:line range, confidence score, one-line citation from CLAUDE.md if applicable, suggested fix]

**Summary:** <one or two sentences>

## Follow-ons (out-of-scope, for future tracking)

Zero or more entries. Use for findings worth a future issue but NOT blocking
for this PR — adjacent code smells, refactor opportunities, sleeper concerns
nearby. The driver parses this section at merge time and files each as a
GitHub issue with `module:world-sim,orchestrator-followup` labels.

Same shape as the specialist reviewer's follow-ons block:

- title: <one-line summary>
  files: <comma-separated file:line refs>
  blocks: [<comma-separated issue numbers, or empty>]
  body: |
    <multi-line prose body for the issue>

Omit this section entirely if you have no follow-ons. Do NOT pad with
low-confidence noise — apply the same ≥80 confidence rubric, just
acknowledge it's out-of-scope for THIS PR.

If you are receiving this prompt via SendMessage (i.e., this is iteration ≥ 2):
- The above "Read" steps are NOT needed — your prior context already has them.
- Just `gh pr diff <N>` again to see the updated diff and re-review.

Report your verdict to team-lead via `SendMessage({to: "team-lead", summary: "generic review: <clean|findings>", message: <your full output above including the verdict marker, the verdict line, findings, follow-ons, and summary>})`. The recipient name is literally `"team-lead"` — not `"shepherd"`, `"driver"`, or any other label. Plain text output alone is NOT enough — the lead doesn't see your turn output, only your SendMessages.

Do NOT run `dotnet build` or `dotnet test`. Do NOT post PR comments. Do NOT edit code.
```

## Escalation prompt template

Used by step 3 (cross-tick recovery) and step 4 (terminal_dispatch for `needs-human` / `conflict`). Use this prompt body for the `mcp__ccd_session__spawn_task` chip:

```
A world-sim migration delivery escalated. Resolve.

Issue: #<M>
PR: #<N or null if no PR opened>
Phase: <P>
Escalation reason: <max_iterations | human_review | same_issue_class
                   | worker_no_progress | merge_conflict | closed_without_merge
                   | initial_implementation_failed | needs_input | worker_failed
                   | merge_command_failed | shepherd_return_unparseable
                   | degraded_mode_cannot_iterate | no_dispatch_tools>

Driver tick summary:
<paste the driver's tick summary — what happened, which PR if any, what the reviewers said, why it escalated>

To resolve:
- Read the PR diff (if a PR exists), the driver's review-comment trail, and
  the linked issue body
- If the worker can't address the review feedback, rewrite the worker's
  expectations in the issue body OR address the feedback yourself
- If the review findings are off-target, push back via PR comment
- For `merge_conflict`: rebase the PR branch onto main, resolve conflicts,
  force-push, and remove the `orchestrator-blocked` label
- For `no_dispatch_tools` / `degraded_mode_cannot_iterate`: investigate the
  harness's tool exposure for /loop-dispatched agents; the driver couldn't
  spawn worker/reviewer teammates from its dispatch context
- Once resolved, remove the `orchestrator-blocked` label from the issue —
  the driver will resume processing on its next tick

References:
- Umbrella: #601
- Driver playbook: docs/world-sim-driver-playbook.md
- Orchestration plan: docs/world-simulator-orchestration-plan.md
```

## Dep graph derivation

Compute the dep graph as the UNION of YAML nodes + open GitHub issues each tick:

```
nodes = (open issues from the cheap probe union — module:world-sim labeled
         + YAML-grepped phase tasks)

edges = (YAML edges from the orchestration plan)
      ∪ {parse issue body for "Blocks: #N" → edge from N to this issue
                              "Depends on: #N" → edge from this issue to N}

ready set = {n ∈ nodes : open
                       ∧ no orchestrator-dispatch:<n> label
                       ∧ no orchestrator-blocked label
                       ∧ all incoming "depends-on" edges point to closed issues}
```

The orchestration plan YAML stays canonical for the planned migration chain (phase order, named tasks). Follow-on issues arrive dynamically and become first-class graph nodes via their GitHub presence — no YAML edit needed.

Edge-parsing patterns (line-anchored, case-sensitive):
- `Blocks: #N` or `Blocks: #N, #M`
- `Depends on: #N` or `Depends on: #N, #M`

## Follow-on filing

On `verdict: merged` or `verdict: decomposed`, file each entry in `state.accumulated_follow_ons` as a GitHub issue:

```
gh issue create -R moumantai-gg/mithril \
  --title "<entry.title>" \
  --label "module:world-sim,orchestrator-followup" \
  --body-file <temp-file-with-entry-body-and-trailer>
```

The trailer (appended to `entry.body` in the temp file):

```
---
Surfaced by PR #<merged-PR or "decomposed parent #<issue>">.
Files affected: <entry.files>
<if entry.blocks non-empty>Blocks: #N, #M, ...</if>
```

After filing, post a roll-up comment on the merged PR (or the parent issue for `decomposed`) via `gh pr comment <PR>` or `gh issue comment <parent>` using `--body-file`:

```
Follow-on filing for merged PR:

Filed: #X, #Y, #Z
Skipped (unparseable entry): 1 — <first ~80 chars of the failing entry text>
Skipped (gh create failure): 1 — <gh stderr first line>
```

Omit any "Skipped" line whose count is zero. If `gh issue create` fails for one entry, continue with the next — never block the merge handling. Skip the entire filing step (no roll-up comment either) if `state.accumulated_follow_ons` is empty.

## Tools you use (depth 0)

As top-level Claude at depth 0, you have access to:

- `Read`, `Grep`, `Glob` — read CLAUDE.md, the three required docs, the orchestration plan, any code referenced by review findings
- `Bash` (with `gh`) — `gh issue list/view/comment/close/create/edit`, `gh pr view/comment/diff/merge`. Always pass `-R moumantai-gg/mithril`.
- `TeamCreate` — establish the team scope at intake. Required for `Agent` to spawn named teammates.
- `Agent` — dispatch teammates with `team_name` + `name` params (`general-purpose` for worker + generic reviewer; `world-sim-reviewer` for specialist). Available ONLY at depth 0.
- `SendMessage` — resume the worker and reviewers across iterations by name. Same restriction: works for live teammates only (Desktop). In CLI, SendMessage is dead-letter.
- `TeamDelete` — tear down the team scope after the shutdown handshake completes.
- `mcp__ccd_session__spawn_task` — emit escalation chips on `needs-human` / `conflict` terminal_dispatches; emit driver-down chip on 3-strike error escalation.
- `ScheduleWakeup` — schedule the next /loop tick (see §ScheduleWakeup invariant). Available at depth 0; depth-1 teammates use `CronCreate` if they need scheduling.
- `ToolSearch` — load deferred-tool schemas (`SendMessage`, `TeamCreate`, `TeamDelete` may need loading via `ToolSearch query: "select:SendMessage,TeamCreate,TeamDelete"`).
- `Edit`, `Write` — available, but you should NOT use them during a driver tick. The worker teammate writes code; you orchestrate. (The only exception: writing temp files for `--body-file` arguments to `gh`.)

**Depth-1 teammates' tools (informational — verified by the Desktop probe):** workers and reviewers spawned at depth 1 have file/shell/MCP/Read/Edit/Write, TeamCreate/TeamDelete/SendMessage, `mcp__ccd_session__spawn_task` (regular tool, not deferred), `CronCreate` (scheduling analog). They do NOT have `Agent` (cannot recurse) or `ScheduleWakeup` under that name.

## What you do NOT do

- Do NOT spawn a fresh worker via `Agent` after the initial implementation (except in degraded mode where no team exists). SendMessage the existing worker teammate by name.
- Do NOT spawn fresh reviewers per iteration. SendMessage them by name.
- Do NOT skip the `TeamCreate` at intake (unless it errors, in which case enter degraded mode explicitly — do not silently fall back to fire-and-forget without disclosing).
- Do NOT skip the `TeamDelete` on terminal_dispatch. Teams persist; without cleanup, `~/.claude/teams/` accumulates stale dirs.
- Do NOT force-merge with `--admin` or similar. If `gh pr merge --squash` fails, escalate.
- Do NOT post a review approval (`gh pr review --approve`). Your comment trail is sufficient signal.
- Do NOT loop past `max_iterations`. Escalate honestly.
- Do NOT silence or override human review comments. The first non-bot review/comment newer than your last iteration is a hard escalation.
- Do NOT edit code or push commits directly. That is the worker's job.
- Do NOT dispatch two deliveries per tick. The pick + deliver in step 4 is the tick's one piece of real work.
- Do NOT auto-retry past a `needs-human` verdict. Once `orchestrator-blocked` is on an issue, it sits until a human removes the label.
- Do NOT skip the `pause` and "umbrella closed" checks in step 1. They're the human's only kill switch.

## Concurrency assumption

The driver assumes **single-instance** execution. /loop is expected to serialize ticks: only one tick runs at a time. The delivery in step 4 can run 1-4 hours (initial implementation worker + 1-3 review iterations + merge), so /loop's tick interval MUST be longer than the longest delivery to avoid two driver instances racing.

If two driver ticks ever run concurrently, both will read the same GitHub state and may double-dispatch a delivery for the same ready issue. The `orchestrator-dispatch:<N>` label is idempotent (a re-add is a no-op), but the `Agent` call is not — two workers would race on the same branch.

No mutex label is acquired at tick start, so this protection lives at the /loop layer.

## Tick summary

At the end of every tick, post a brief prose summary as your final user-visible message. One paragraph, plain English, no JSON. Examples:

- *Merged delivery*: "Delivered #707 (Phase 2 — split AbilityCalendarStateService). Worker opened PR #710, reviewers converged on iteration 2, merged with merge_sha abc1234. Filed 2 follow-on issues (#711 — same-class refactor opportunity in PlannerService, #712 — missing test for Mode == Replay branch). Next tick in 60s."
- *Idle*: "No actionable work this tick — all open world-sim issues are either dispatched, blocked, or have unresolved deps. Next tick in 30 minutes."
- *Escalation*: "Task #707 escalated as `needs-human / same_issue_class` — workers couldn't resolve principle-12 finding in PlannerService.cs:142 across 3 review iterations. `orchestrator-blocked` label applied, spawn_task chip emitted. Next tick in 60s."
- *Cross-tick recovery*: "Cross-tick recovery picked up PR #709's `needs-human` marker from a prior crashed tick. Applied `orchestrator-blocked` label to #708 and emitted escalation chip. Next tick in 60s."
- *Circuit breaker*: "Driver paused — `pause` label is on #601. Remove the label and restart /loop to resume." (no ScheduleWakeup)

**Why prose, not JSON.** v3.1 emitted a structured JSON return for the orchestrator subagent to parse. In v4 there's no orchestrator — the driver IS top-level Claude, and there's no consumer for a JSON return. All inter-tick state lives in GitHub (labels, PR comment markers, filed issues, umbrella comment trail). Prose serves the only remaining consumer (the human reading the conversation).

If a future tooling consumer ever needs structured output, the relevant fields are already persisted on GitHub: `gh issue view <N>` and `gh pr view <PR>` carry the labels, comment markers, and state needed to reconstruct what any tick did.

## On errors

If a `gh` command fails with a network, rate-limit, or auth error:

- **Post an error breadcrumb on #601** so the 3-strike counter has a source. Use `gh issue comment 601 -R moumantai-gg/mithril --body-file <temp-file>` with body shape:
  ```
  <!-- orchestrator-error: gh-failure -->
  Driver gh failure at <ISO-8601 UTC>.

  Failed command: <cmd>
  Error text: <first ~500 chars of stderr>

  Retrying in 300s.
  ```
  The HTML-comment marker is what the counter greps for. If posting the breadcrumb ALSO fails, log to stdout and continue — never enter a posting loop.
- Call `ScheduleWakeup` in 300s and exit (5-minute retry interval — within cache TTL).
- Track failures across consecutive ticks via `gh issue view 601 -R moumantai-gg/mithril --json comments` at the start of each tick: count the trailing run of orchestrator-authored comments matching `<!-- orchestrator-error: gh-failure -->` with no intervening non-error orchestrator comment. If that run is ≥ 3, treat as "3 consecutive failures."
- After 3 consecutive failures, call `mcp__ccd_session__spawn_task` with title "World-sim driver: GitHub unreachable", tldr "World-sim driver has failed 3 consecutive ticks on GitHub API errors. Investigate connectivity / auth.", and a prompt that includes the most recent error message verbatim. Then exit with NO `ScheduleWakeup`.

If `Agent` is missing from your toolset entirely (the harness doesn't expose it at /loop depth):

- This is the same "can't dispatch" condition. Post a breadcrumb with marker `<!-- orchestrator-error: no-agent -->` and the same 3-strike pattern. After 3 strikes, spawn a chip with title "World-sim driver: Agent tool unavailable", explaining the harness's tool exposure needs investigation. Exit without `ScheduleWakeup`.

If `Agent` IS available but `TeamCreate` is not, enter degraded mode for the current delivery — that's per-delivery, not per-tick. The tick still completes; only that delivery degrades.

Other unexpected errors (file read errors, JSON parse errors on subagent output) follow the same breadcrumb + retry + 3-strike pattern with a different marker value.
