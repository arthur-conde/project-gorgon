---
allowed-tools: Agent
description: Run one tick of the world-sim orchestrator
disable-model-invocation: false
---

Dispatch the world-sim orchestrator subagent for one tick.

Use the `Agent` tool with:

```
subagent_type: world-sim-orchestrator
prompt: |
  Run one tick of the world-sim migration orchestrator.
  Read GitHub state, take ONE action per the 5-step decision logic in
  .claude/agents/world-sim-orchestrator.md, then exit with ScheduleWakeup
  to schedule the next tick.
```

After the orchestrator returns, surface its action (or idle) to the user as a one-line summary.

For continuous operation, drive this via `/loop /world-sim-orchestrate-tick`.
