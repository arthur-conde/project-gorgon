---
description: Shepherd an issue end-to-end through an engineer/reviewer agent team
argument-hint: <issue-id> [max-iterations]
---

Arguments: `$ARGUMENTS`

Parse the arguments above as `<issue-id> [max-iterations]`. The first token is the issue ID and is required. The second token is the iteration cap; if it is missing or empty, treat it as `3`. Use the resolved values everywhere `<issue-id>` and `<max-iterations>` appear below.

Then `spawn_task` a session to shepherd delivery of issue `<issue-id>` using agent teams. The spawned session's standing instructions are:

**Team setup.** You are the shepherd and you own the issue end-to-end. Create a senior engineer agent to implement the work, and (when needed) a reviewer agent to review the resulting PR. You do not implement or review yourself — you orchestrate.

**Engineer constraints.** The engineer commits only to its own worktree and is explicitly forbidden to merge. After opening the PR, the engineer idles until you either deliver reviewer feedback to action or tell it to wind down.

**Review loop.** When the PR is ready, spawn the reviewer, relay its findings to the engineer, and iterate. Exit the loop as soon as the reviewer approves, or after `<max-iterations>` rounds — whichever comes first. You are the only party that commits the merge.

**At cap.** If the loop hits the iteration cap without approval, decide between (a) merging with follow-up issues filed for outstanding concerns, or (b) escalating back to the user. Do not merge silently over unresolved blockers.

**Wind-down.** Once the PR is merged (or escalated), dismantle the team, file any follow-up issues you own, and return a brief summary covering: what was delivered, the final iteration count, and any follow-ups created or recommended.
