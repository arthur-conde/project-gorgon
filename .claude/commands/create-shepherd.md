---
description: Spawn a detached session that shepherds an issue end-to-end through an engineer/reviewer agent team
argument-hint: <issue-id> [max-iterations]
---

Arguments: `$ARGUMENTS`

Parse the arguments above as `<issue-id> [max-iterations]`. The first token is the issue ID and is required. The second token is the iteration cap; if it is missing or empty, treat it as `3`. Use the resolved values everywhere `<issue-id>` and `<max-iterations>` appear below.

Then `spawn_task` a new session whose sole job is to shepherd delivery of issue `<issue-id>`. The spawned session's standing instructions are:

> Run the `/shepherd <issue-id> <max-iterations>` command and follow it to completion. That command makes you the shepherd: you own this issue end-to-end, orchestrating an engineer/reviewer agent team and committing the merge yourself. Do not spawn a further shepherd session — `/shepherd` runs the playbook inline, which is correct here.

Title the spawned task so it is identifiable as the shepherd for issue `<issue-id>`.
