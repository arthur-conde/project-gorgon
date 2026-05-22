---
allowed-tools: Read, Grep, Glob, Bash, Edit, Write, Agent, SendMessage, TeamCreate, TeamDelete, mcp__ccd_session__spawn_task, ScheduleWakeup, ToolSearch
description: Run one tick of the world-sim driver (you, top-level Claude, ARE the driver — do NOT dispatch a subagent)
disable-model-invocation: false
---

You (top-level Claude at depth 0) are the world-sim driver for this tick. Do **NOT** dispatch a `world-sim-shepherd` subagent — that abstraction was removed in v4 (#666). The driver lives in `docs/world-sim-driver-playbook.md` and runs in your own context.

**This slash command body handles the cheap probe and idle short-circuit BEFORE any file reads.** Most ticks (idle) should complete here with zero playbook/doc loads. Only Read the playbook when there's actual work to do — saves ~30K tokens per idle tick.

## Step 1 — Circuit breaker + cheap probe

Run these gh calls FIRST. All include `-R moumantai-gg/mithril`:

1. `gh issue view 601 -R moumantai-gg/mithril --json state,labels` — check for `pause` label and `state` (CLOSED).
   - If `pause` label present → exit **without** ScheduleWakeup. Post a one-line summary: "Driver paused by `pause` label on #601." (No file reads required.)
   - If `state == "CLOSED"` → post a comment on #601 ("World-sim migration complete. Driver exiting.") and exit **without** ScheduleWakeup.
2. `gh issue list -R moumantai-gg/mithril --label module:world-sim --state open --json number,labels,title` — open follow-on / world-sim-labeled issues.
3. Grep `docs/world-simulator-orchestration-plan.md` §Dependency graph for `id:` lines to extract the YAML node issue numbers (~16 phase tasks). Then `gh issue view <N> -R moumantai-gg/mithril --json number,state,labels,title` for each (these don't all carry `module:world-sim`).
4. Union the two issue sets (deduplicate by number).

## Step 2 — Idle decision (no doc loads)

**Skip this step entirely if `issue` or `pr` was supplied as input** — manual invocations don't have an idle short-circuit; they always read the playbook and run the requested delivery. (The playbook will route the manual mode via §Mode dispatch.)

In autonomous tick mode (no manual inputs), filter the unioned set for "potentially actionable":

- Open
- Does NOT have `orchestrator-dispatch:<N>` label
- Does NOT have `orchestrator-blocked` label

If the filtered set is **empty** AND no open `module:world-sim` PR exists whose latest shepherd-verdict marker is `needs-human` without `orchestrator-blocked` (cross-tick recovery scenario — check with `gh pr list -R moumantai-gg/mithril --search "label:module:world-sim is:open" --json number,body,comments` if you have any candidates):

- Call `ScheduleWakeup` in 1800s.
- Post a one-paragraph idle summary (e.g., "No actionable work this tick — all open world-sim issues are either dispatched, blocked, or have unresolved deps. Next tick in 30 minutes.").
- Exit.

**Most idle ticks complete here with ZERO file reads.** The slash command body is loaded for free as part of the dispatch; the cheap probe is the only cost. Token economy: ~3K (this slash command) + ~5K (gh outputs) = ~8K per idle tick, vs ~75-100K when re-reading the playbook + docs.

## Step 3 — Work found: load the playbook

If you reach this step, there's either:
- A ready issue in the filtered set (potential delivery in step 4 of the playbook), OR
- A `needs-human` marker requiring cross-tick recovery (step 3 of the playbook), OR
- The deliberate `manual-issue` / `adopt-pr` mode triggered by explicit `issue` / `pr` input on this invocation.

Read the playbook and follow it:

```
Read docs/world-sim-driver-playbook.md
```

The playbook contains the mode dispatch, 5-step decision logic, delivery sub-phases (intake → initial impl → review-fix → merge → teardown), context pack, output / tick summary convention, escalation prompt template, dep graph derivation, follow-on filing, inline degraded mode (CLI fallback), and on-errors handling.

**Pass your probe results as state to the playbook**: you already have the open-issue union from step 1. The playbook's §The 5-step decision logic refers back to "the cheap probe" — that's the work you just did. Don't redo it.

Take ONE action per the priority list in the playbook, then exit with `ScheduleWakeup` (per the playbook's §ScheduleWakeup invariant — every non-kill-switch exit MUST schedule).

After the tick completes, surface its action (or idle) to the user as a one-line summary.

For continuous operation, drive this via `/loop /world-sim-orchestrate-tick`. The slash command name is preserved from v2–v4 for /loop continuity.

**Environment**: works as designed in Claude Code Desktop (Teams + SendMessage are live). In CLI it falls back to inline degraded mode (per playbook §Inline degraded mode). Desktop is the primary target.

**References**:
- Playbook: [docs/world-sim-driver-playbook.md](../../docs/world-sim-driver-playbook.md)
- Design notebook: [docs/world-sim-shepherd.md](../../docs/world-sim-shepherd.md)
- v4 collapse rationale: [#666](https://github.com/moumantai-gg/mithril/issues/666)
- Idle-cost fix rationale: this PR
- Probe results: scratch/desktop-harness-probe.md
- Umbrella: #601
