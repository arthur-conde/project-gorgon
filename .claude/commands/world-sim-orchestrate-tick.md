---
allowed-tools: Read, Grep, Glob, Bash, Edit, Write, Agent, SendMessage, TeamCreate, TeamDelete, mcp__ccd_session__spawn_task, ScheduleWakeup, ToolSearch
description: Run one tick of the world-sim driver (you, top-level Claude, ARE the driver — do NOT dispatch a subagent)
disable-model-invocation: false
---

You (top-level Claude at depth 0) are the world-sim driver for this tick. Do **NOT** dispatch a `world-sim-shepherd` subagent — that abstraction was removed in v4 (#666). The driver lives in the playbook below and runs in your own context at depth 0, where `Agent` is available to spawn worker + reviewer teammates.

**Read the playbook and follow it**:

```
Read docs/world-sim-driver-playbook.md
```

The playbook contains the 5-step decision logic (circuit breaker / idle probe / cross-tick recovery / pick + deliver / idle wakeup), delivery sub-phases (intake / initial impl / review-fix / merge / teardown), mode dispatch (autonomous tick / manual issue / adopt-PR), context pack, output contract, ScheduleWakeup invariant, escalation prompt template, dep graph derivation, follow-on filing, inline degraded mode, and on-errors handling.

Take ONE action per the priority list in the playbook, then exit with `ScheduleWakeup` (per §ScheduleWakeup invariant — every non-kill-switch exit MUST schedule).

After the tick completes, surface its action (or idle) to the user as a one-line summary.

For continuous operation, drive this via `/loop /world-sim-orchestrate-tick`. The slash command name is preserved from v2/v2.1/v3/v3.1 for /loop continuity even though the agent abstraction is gone — there's no orchestrator anymore.

**Environment**: this works as designed in Claude Code Desktop (Teams + SendMessage are live). In CLI it falls back to inline degraded mode (per the playbook's §Inline degraded mode). Desktop is the primary target.

**References**:
- Playbook: [docs/world-sim-driver-playbook.md](../../docs/world-sim-driver-playbook.md)
- Design notebook: [docs/world-sim-shepherd.md](../../docs/world-sim-shepherd.md)
- v4 collapse rationale: [#666](https://github.com/moumantai-gg/mithril/issues/666)
- Probe results: scratch/desktop-harness-probe.md
- Umbrella: #601
