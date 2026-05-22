---
allowed-tools: Agent
description: Run one tick of the world-sim driver
disable-model-invocation: false
---

Dispatch the world-sim driver (`world-sim-shepherd`) subagent for one tick.

Use the `Agent` tool with:

```
subagent_type: world-sim-shepherd
prompt: |
  Run one tick of the world-sim migration driver.
  Pick the next ready issue from the dep graph (if any), deliver it through
  initial implementation → review loop → merge, or escalate cleanly. Take
  ONE action per the 5-step decision logic in
  .claude/agents/world-sim-shepherd.md, then exit with ScheduleWakeup
  to schedule the next tick.
```

(Slash command name kept as `world-sim-orchestrate-tick` for /loop continuity — see #656 for why the previously-separate orchestrator + shepherd agents collapsed into one in v3.)

After the driver returns, surface its action (or idle) to the user as a one-line summary.

For continuous operation, drive this via `/loop /world-sim-orchestrate-tick`.
