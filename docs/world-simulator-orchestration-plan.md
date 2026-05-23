# World-simulator orchestration plan

> **Vocabulary:** see [`docs/glossary.md`](glossary.md) for definitions of the world-sim terminology used in this doc.

Machine-actionable scheduling plan for the world-simulator migration. Pairs with the design notebook (`docs/world-simulator.md`), the migration audit (`docs/world-sim-migration-audit.md`), and the GitHub issue umbrella (#601) + foundation umbrella (#614).

**Audience:** an orchestrator (Claude Code subagent harness, or external system) scheduling worker agents to land migration items hands-free.

**Status:** orchestrator queue drained 2026-05-23; umbrella [#601](https://github.com/moumantai-gg/mithril/issues/601) closed. This plan remains a reference for any future tick-style work — notably the Phase 5 residuals tracked under [#700](https://github.com/moumantai-gg/mithril/issues/700) (release-cycle-gated [#732](https://github.com/moumantai-gg/mithril/issues/732), opportunistic `IPlayer*` renames). The dependency graph + verification gates below stay accurate as patterns; the phase-by-phase scheduling is historical.

---

## How an orchestrator should use this file

> **For autonomous operation,** see [`.claude/agents/world-sim-orchestrator.md`](../.claude/agents/world-sim-orchestrator.md) — the manual instructions in this section describe what the orchestrator subagent automates. Drive it with `/loop /world-sim-orchestrate-tick`. The instructions below are still the authoritative description of orchestrator behavior; the subagent reads them as required reading each tick.

1. Read [§Dependency graph](#dependency-graph) and current GitHub issue state.
2. Identify **ready tasks**: tasks whose `depends_on` are all closed issues, and which aren't themselves closed/in-progress.
3. For each ready task: spawn the [agent type](#per-task-orchestration-metadata) listed, hand over the issue body verbatim (per `spawned_session_handoff_self_contained` memory convention), and observe. After the worker opens its PR, dispatch the per-PR shepherd (see [§Per-PR shepherding](#per-pr-shepherding)) before advancing to verification.
4. Verify completion using [§Verification gates](#verification-gates) before treating a task as done. The issue's own acceptance criteria are one input; cross-task invariants are another.
5. Honor [§Stop conditions](#stop-conditions): pause for human review under specific failure modes; do not blindly retry.
6. On a clean phase transition, run [§Cross-phase invariants](#cross-phase-invariants) before unlocking the next phase.

The orchestrator does NOT need to read the design notebook to do its job — the issue bodies are self-contained. It DOES need to read this plan + GitHub state.

---

## Global rules

Apply to every worker agent the orchestrator spawns. These derive from the repo's `CLAUDE.md` + memory conventions.

- **Branching.** Worker creates a feature branch off main. **Never** push to main. **Never** force-push to main. Use `gh pr create` to open the PR.
- **Commits.** Prefer new commits over `--amend` (the project rule). Never `--no-verify` unless the user explicitly authorizes.
- **Identity.** `arthur.conde@live.com` is the primary commit author email (per `user_identity` memory). Co-Authored-By trailer with the Claude model identifier.
- **Build verification.** Every task must `dotnet build Mithril.slnx` clean (warnings-as-errors enabled). Hard gate.
- **Test verification.** Every task must `dotnet test Mithril.slnx` clean. Hard gate.
- **Mithril running check.** A `PreToolUse` hook blocks `dotnet build/test/publish/pack` while the shell runs (per `mithril_build_file_lock_silent` memory). Orchestrator should never assume Mithril is running concurrently with worker tasks; spawn worker tasks only when shell is closed.
- **WPF gotchas.** Worker tasks touching `*.xaml` or new views must read `docs/wpf-gotchas.md` first (per `CLAUDE.md`).
- **LSP for C# work.** Worker tasks touching more than one C# type must load the `csharp-lsp` tool first (per `CLAUDE.md`).
- **Cross-source correlation.** Worker tasks adding consumers that fuse Player.log + chat must read `docs/cross-source-correlation.md` first.
- **PR body convention.** Worker tasks include a "Summary" + "Test plan" section per `CLAUDE.md`'s `gh pr create` template, plus the "🤖 Generated with Claude Code" trailer.

---

## Dependency graph

Nodes are GitHub issues. Edges are `(parent depends_on child)`. An edge means: *parent* cannot start until *child* has its PR merged.

```yaml
# Sequenced by phase. Foundation must close first; later phases unlock as their deps close.

nodes:
  # Phase 0a — Foundation contracts (sequential; nothing else runs in this phase)
  - { id: 615, phase: "0a", name: "Mithril.WorldSim.Core contracts" }

  # Phase 0b — World shells (parallel after 0a)
  - { id: 616, phase: "0b", name: "IPlayerWorld concrete shell" }
  - { id: 617, phase: "0b", name: "IChatWorld concrete shell" }

  # Phase 1 — Validation (after 0b)
  - { id: 618, phase: "1", name: "Validate foundation: convert IPlayerSkillStateService to IFolder<T>" }

  # Phase 2 — Splits (parallel after Phase 1)
  - { id: 602, phase: "2", name: "Split IInventoryService" }
  - { id: 603, phase: "2", name: "Split SarumanCodebookService" }

  # Phase 3 — Split-dependents
  - { id: 608, phase: "3", name: "Resolve Arwen TryResolve peek" }
  - { id: 607, phase: "3", name: "Eliminate QuestService synthesis + extract journal" }
  - { id: 606, phase: "3", name: "Retire Legolas chat consumption" }

  # Phase 4 — Wall-clock + scheduling
  - { id: 609, phase: "4", name: "Migrate wall-clock state-decisions to IWorldClock.Now" }
  - { id: 613, phase: "4", name: "Gandalf scheduler collapse" }

  # Parallel track — foundation-independent; can run anytime
  - { id: 610, phase: "parallel", name: "Add ServerCatalogParser" }
  - { id: 611, phase: "parallel", name: "Add ConnectionEventParser" }
  - { id: 612, phase: "parallel", name: "Extract Mithril.GameReports" }
  - { id: 604, phase: "parallel", name: "Motherlode same-source migration" }
  - { id: 605, phase: "parallel", name: "AreaCalibration chat-side cleanup" }

edges:
  # Phase 0b depends on 0a
  - { from: 616, to: 615 }
  - { from: 617, to: 615 }

  # Phase 1 depends on 0b
  - { from: 618, to: 616 }
  - { from: 618, to: 617 }

  # Phase 2 depends on Phase 1
  - { from: 602, to: 618 }
  - { from: 603, to: 618 }

  # Phase 3 depends on Phase 2
  - { from: 608, to: 602 }   # Arwen depends on Inventory split
  - { from: 606, to: 602 }   # Legolas chat retirement needs Inventory split for ItemAdded
  - { from: 606, to: 604 }   # ...and Motherlode migration for distance verb
  - { from: 607, to: 618 }   # Quest synthesis collapse depends on per-character world scope (folder pattern)

  # Phase 4 depends on Phase 2 (worlds exist + clock is real)
  - { from: 609, to: 616 }   # wall-clock migration needs IWorldClock.Now
  - { from: 609, to: 617 }
  - { from: 613, to: 616 }   # Gandalf needs CalendarTimeAdvanced domain event from PlayerWorld

  # Parallel-track internal deps
  - { from: 611, to: 610 }   # ConnectionEvent depends on ServerCatalog for join target

  # Parallel-track has NO deps on phases 0–4. They can run today against main.
```

---

## Per-task orchestration metadata

What the orchestrator needs to dispatch each task. The full spec is in the issue body.

```yaml
tasks:
  # Phase 0a
  - id: 615
    agent: general-purpose
    estimate: S
    parallel_ok: false  # nothing else in phase 0a
    verify_commands:
      - dotnet build Mithril.slnx
    completion_signal: pr_merged
    risk: low
    notes: "Contracts only — no behaviour. Tests not required for this task; the assembly just needs to compile and be reference-able by downstream issues."

  # Phase 0b
  - id: 616
    agent: general-purpose
    estimate: M
    parallel_ok: true   # alongside 617
    verify_commands:
      - dotnet build Mithril.slnx
      - dotnet test tests/Mithril.GameState.Tests
    completion_signal: pr_merged
    risk: medium
    notes: "Concrete world shell + tests for drain/mode-transition/bus-delivery. New tests in tests/Mithril.GameState.Tests or new test project. No folders yet."

  - id: 617
    agent: general-purpose
    estimate: M
    parallel_ok: true   # alongside 616
    verify_commands:
      - dotnet build Mithril.slnx
      - dotnet test tests/Mithril.GameState.Tests
    completion_signal: pr_merged
    risk: medium
    notes: "Same shape as 616 plus session-replay-from-banner. Chat-banner parser may already exist; lift it if so."

  # Phase 1
  - id: 618
    agent: general-purpose
    estimate: M
    parallel_ok: false  # standalone validation
    verify_commands:
      - dotnet build Mithril.slnx
      - dotnet test Mithril.slnx
    completion_signal: pr_merged
    risk: medium
    notes: "Validates the dispatch graph end-to-end. If consumers (Samwise/Smaug/Elrond) regress, do not merge — escalate."

  # Phase 2
  - id: 602
    agent: general-purpose
    estimate: L
    parallel_ok: true   # alongside 603 (same shape, different module)
    verify_commands:
      - dotnet build Mithril.slnx
      - dotnet test Mithril.slnx
    completion_signal: pr_merged
    risk: high
    notes: "Keystone of the migration. Five inputs, six downstream consumers. Run a code-review pass before merge — see Verification gates."

  - id: 603
    agent: general-purpose
    estimate: M
    parallel_ok: true   # alongside 602
    verify_commands:
      - dotnet build Mithril.slnx
      - dotnet test tests/Saruman.Tests
    completion_signal: pr_merged
    risk: medium
    notes: "Smaller surface than Inventory. Shape-different join (key-only, no TTL); don't blindly copy the Inventory split's pattern."

  # Phase 3
  - id: 608
    agent: general-purpose
    estimate: S
    parallel_ok: true
    verify_commands:
      - dotnet build Mithril.slnx
      - dotnet test tests/Arwen.Tests
    completion_signal: pr_merged
    risk: low
    notes: "Mechanical migration once 602 lands. Update Arwen's calibration registration with PlayerWorld dispatch deps; remove the manual TryResolve call."

  - id: 607
    agent: general-purpose
    estimate: M
    parallel_ok: true
    verify_commands:
      - dotnet build Mithril.slnx
      - dotnet test tests/Mithril.GameState.Tests
    completion_signal: pr_merged
    risk: medium
    notes: "Two changes: (a) eliminate OnViewCurrentChanged synthesis; (b) extract IPlayerQuestJournalService from IQuestService. Could split into two PRs if reviewer prefers."

  - id: 606
    agent: general-purpose
    estimate: L
    parallel_ok: true   # after 602 + 604 land
    verify_commands:
      - dotnet build Mithril.slnx
      - dotnet test tests/Legolas.Tests
    completion_signal: pr_merged
    risk: high
    notes: "Five chat verbs migrate to Player.log. Some need new parsers; some leverage existing services. Delete LogIngestionService + ChatLogParser at the end. Don't merge if any of the existing Legolas end-to-end tests regress."

  # Phase 4
  - id: 609
    agent: general-purpose
    estimate: M
    parallel_ok: true
    verify_commands:
      - dotnet build Mithril.slnx
      - dotnet test Mithril.slnx
    completion_signal: pr_merged
    risk: medium
    notes: "9 sites per audit. Search-replace _time.GetUtcNow() → worldClock.Now per site, picking the right world's clock. Stamp-only uses can stay on TimeProvider.System (flag them in comments). Add a replay-determinism test if possible."

  - id: 613
    agent: general-purpose
    estimate: L
    parallel_ok: true
    verify_commands:
      - dotnet build Mithril.slnx
      - dotnet test tests/Gandalf.Tests
    completion_signal: pr_merged
    risk: high
    notes: "Three Gandalf scheduler services retire. Module-side timer ledger stays. Watch for missed-alarms test cases (alarms expired during drain show as expired-but-silent)."

  # Parallel track
  - id: 610
    agent: general-purpose
    estimate: S
    parallel_ok: true
    verify_commands:
      - dotnet build Mithril.slnx
      - dotnet test tests/Mithril.GameState.Tests
    completion_signal: pr_merged
    risk: low
    notes: "Standalone parser. Foundation-independent. Could ship today."

  - id: 611
    agent: general-purpose
    estimate: S
    parallel_ok: true
    verify_commands:
      - dotnet build Mithril.slnx
      - dotnet test tests/Mithril.GameState.Tests
    completion_signal: pr_merged
    risk: low
    notes: "Adds Server field to GameSession; joins against ServerCatalog. Depends on 610."

  - id: 612
    agent: general-purpose
    estimate: M
    parallel_ok: true
    verify_commands:
      - dotnet build Mithril.slnx
      - dotnet test Mithril.slnx
    completion_signal: pr_merged
    risk: medium
    notes: "New assembly. Extract Bilbo.StorageReportLoader → Mithril.GameReports. Rewire Bilbo + Elrond consumers."

  - id: 604
    agent: general-purpose
    estimate: M
    parallel_ok: true
    verify_commands:
      - dotnet build Mithril.slnx
      - dotnet test tests/Legolas.Tests
    completion_signal: pr_merged
    risk: medium
    notes: "Add Player.log ProcessScreenText parser; migrate coordinator. k-th-to-slot-k pairing logic stays the same."

  - id: 605
    agent: general-purpose
    estimate: S
    parallel_ok: true
    verify_commands:
      - dotnet build Mithril.slnx
      - dotnet test tests/Legolas.Tests
    completion_signal: pr_merged
    risk: low
    notes: "Light cleanup. AreaCalibrationService swaps to PlayerAreaTracker.Changed."
```

---

## Per-PR shepherding

After a worker opens its PR, the orchestrator hands off to a per-PR shepherd subagent instead of polling the PR itself. The shepherd babysits one PR end-to-end and returns a structured verdict.

Dispatch the shepherd via the `Agent` tool with:

```
subagent_type: world-sim-shepherd
prompt: |
  Babysit PR #<pr>. Issue: #<issue>. Phase: <phase>. Risk: <risk>.
  Worker dispatch template:
  <verbatim worker template the orchestrator would use for a fresh dispatch>
  max_iterations: 3
```

The shepherd returns one of three verdicts (parseable from a fenced JSON block in its final message):

- `ready-to-merge` — the orchestrator does the actual `gh pr merge` per its merge policy.
- `needs-human` — the orchestrator surfaces the shepherd's escalation reason + summary to the user. This is Stop Condition #2; do not auto-retry.
- `conflict` — merge conflict detected; the orchestrator serializes per Stop Condition #6 (which has the later concurrent task rebase before re-dispatch).

Design rationale: [`world-sim-shepherd.md`](world-sim-shepherd.md).

## Verification gates

Verification has three tiers — `build`, `test`, and `system`. The orchestrator runs all three before marking a task done.

### Tier 1 — Build (every task)

```
dotnet build Mithril.slnx
```

Must succeed with no errors. Warnings-as-errors is enforced; missing XML doc on public types fails compilation.

### Tier 2 — Test (every task)

```
dotnet test <task-specific-test-project>
```

Run the test project listed in the task's `verify_commands`. **Plus**, at phase transitions (any time the orchestrator moves from one phase to the next), run:

```
dotnet test Mithril.slnx
```

All 20-ish test projects, no failures, no skips. This catches cross-project regressions.

### Tier 2.5 — Shepherd review (every task)

After the worker opens its PR and before the orchestrator considers system-tier checks, the per-PR shepherd runs (see [§Per-PR shepherding](#per-pr-shepherding)). The shepherd dispatches the generic code reviewer and the world-sim specialist in parallel each iteration; findings flow back as a structured verdict. The shepherd handles its own iteration: a `needs-human` verdict is the only signal the orchestrator routes on.

### Tier 3 — System (selective tasks, per the task's `risk` level)

For `risk: medium` and `risk: high` tasks, after PR merge:

- **Replay-determinism test** (if applicable): replay a recorded log corpus through the changed component; assert trajectory matches the prior run. Specifically required for: 602, 603, 609.
- **Mithril shell smoke test**: `scripts/start.ps1 -Build` clean launch; boot.log shows no errors; modules light up correctly. Required for: 616, 617, 618, 602, 603, 606, 612, 613.
- **Performance benchmark**: cold-start drain time + per-frame dispatch overhead. Required for: 618 (first folder migration), 602 (largest split). Threshold: no regression beyond 10% vs prior-PR baseline.

If any system-tier check fails, the orchestrator **does not retry**. Treat as a stop condition (see §Stop conditions).

---

## Cross-phase invariants

Run between phases as a checkpoint. These are "is the system still cohesive" assertions, not per-task tests.

### After Phase 0a (after #615 merges)

- [ ] `Mithril.WorldSim.Core` assembly exists and is referenced by nothing yet (it's a contract-only package).
- [ ] Solution builds; no behavioural change anywhere.

### After Phase 0b (after #616 + #617 merge)

- [ ] `IPlayerWorld` runs in isolation (a test instantiates it with no folders and shows that frames drain through to the bus with no consumers).
- [ ] `IChatWorld` runs in isolation likewise.
- [ ] `Mode` transitions correctly observed in both.
- [ ] No regression in existing `Mithril.GameState` services — they still run on their `BackgroundService` shells; the worlds exist alongside but don't yet host any folders.

### After Phase 1 (after #618 merges)

- [ ] `IPlayerSkillStateService` runs as an `IFolder<T>` registered with `IPlayerWorld`.
- [ ] All Skills consumers (Samwise GardeningXp, Smaug vendor ingestion, Elrond planner integration if live) continue to function.
- [ ] Replay-determinism test: replay a Skills-heavy session twice, assert the resulting `PlayerSkillSnapshot` is byte-identical.
- [ ] Cold-start time within 10% of prior baseline.

### After Phase 2 (after #602 + #603 merge)

- [ ] `IInventoryService` is now `IInventoryView` (the composing view); `IPlayerInventoryService` + `IChatInventoryStateMachine` are the splits.
- [ ] All inventory consumers (six per audit) work against the view.
- [ ] `SarumanCodebookService` is the view; discovery + spent are split.
- [ ] No direct chat `await foreach` exists outside `IChatWorld`.

### After Phase 3

- [ ] Arwen's `TryResolve` peek is now a coherent dispatch-order read.
- [ ] `QuestService` reload synthesis path is gone.
- [ ] `IPlayerQuestJournalService` extracted.
- [ ] `Legolas.Services.LogIngestionService` deleted entirely; `IChatLogStream` has zero direct consumers in the codebase.

### After Phase 4

- [ ] No `_time.GetUtcNow()` in state-decision paths (record stamping is the only allowed remaining use).
- [ ] `Gandalf.TimerExpirationScheduler` + `ShiftAlarmService` + `TimerProgressService.CheckExpirations` retire; replaced by `CalendarTimeAdvanced` consumption.

### Final acceptance (entire migration done)

- [ ] All 18 tracked issues closed.
- [ ] `docs/cross-source-correlation.md` has no in-repo Tier-1 / Tier-2 reference implementations (only the pattern catalog).
- [ ] `docs/world-sim-migration-audit.md` either updated or marked superseded by a final post-migration audit.
- [ ] Architecture umbrella (#601) closed.

---

## Stop conditions

The orchestrator pauses for human review under any of these conditions. Do NOT auto-retry; do NOT auto-merge past them.

1. **CI failure on a task PR.** Worker may attempt one fix-up commit if the cause is obviously a flake (e.g., `RG1000 BAML dup-key` per `mithril_rg1000_baml_dupkey_flake` memory — known build flake). If failure repeats or is non-trivial, escalate.
2. **Shepherd returns `needs-human`.** The per-PR shepherd (see [§Per-PR shepherding](#per-pr-shepherding)) encapsulates review-and-iterate. Its `needs-human` verdict — fired by max_iterations exceeded, human review comment, same-issue-class detection, or worker_no_progress — is a hard pause. The shepherd's prose summary explains which specific reason triggered.
3. **Cross-phase invariant fails.** If running the post-phase checks reveals a regression, do not unlock the next phase. Escalate.
4. **Performance regression beyond threshold.** If a Tier-3 benchmark exceeds 10% regression, escalate. Don't merge.
5. **Replay-determinism test fails.** If a replay test shows non-identical trajectories where it should match, escalate.
6. **Conflicting concurrent work.** If two parallel tasks touch the same file and produce merge conflicts, the orchestrator serializes them (later one rebases) but does not blindly resolve merge conflicts itself.
7. **Worker agent error.** If the worker agent reports an error it can't resolve (compile error after multiple iterations, test failures it can't diagnose, etc.), escalate.
8. **PR open longer than the orchestrator's stale-PR threshold** (suggested: 7 days). Escalate so the user can decide whether to close, reassign, or keep waiting.

---

## Rollback / safe-abort

If a merged task is later discovered to have broken something downstream, the orchestrator's escalation path is:

1. **Identify the breaking task.** Use `git bisect` against the relevant test if needed.
2. **Open a `revert` PR.** `gh pr create` with a body explaining what broke and what regressed. Do NOT auto-merge.
3. **Mark all dependent tasks blocked.** Annotate them in the project board.
4. **Notify the user** with a summary.

The orchestrator does not automatically revert merged commits without human review.

---

## Parallel-track handling

The "parallel" phase items (#610, #611, #612, #604, #605) have no dependency on Phases 0–4. The orchestrator can interleave them with foundation work freely. They're useful immediately on landing (server identity, vault data, Motherlode reliability, AreaCalibration cleanup) and don't change shape post-foundation, so they're safe parallel work.

Rule: the orchestrator may always dispatch parallel-track items if a worker capacity is available. They count toward worker-capacity limits but not toward phase-blocking.

---

## Worker-capacity assumption

The orchestration plan assumes the orchestrator has access to N worker capacity slots (Claude Code subagents, distinct dev environments, or similar). With N=1, the plan executes serially. With N≥2, parallelism kicks in per the `parallel_ok` field. The dependency graph + `parallel_ok` flags are the source of truth for what may run concurrently.

Worker scheduling priority (if multiple tasks are eligible):

1. Phase 0–4 blocking-path tasks (longer chain → higher priority)
2. Parallel-track items
3. Risk-medium / risk-high tasks earlier in the phase (more validation needed)

---

## What this plan does NOT cover

- **Implementation details per task.** Those live in the issue body. The orchestrator passes the issue body verbatim to the worker as context.
- **Test corpus.** Replay-determinism tests need a stable set of Player.log + chat captures. Sourcing those is a separate concern (memory `gorgon_calibration_repo` notes a sibling repo exists; whether it's appropriate for replay corpora is a separate design conversation).
- **User-action recording for full session replay.** Explicitly out of scope per the design notebook's "What this doc does NOT cover" section.
- **Cross-repo coordination.** Wiki, calibration repo, etc. — separate artifacts.

---

## References

- Architecture umbrella: #601
- Foundation umbrella: #614
- Design notebook: `docs/world-simulator.md` (in #600, merged to main)
- Component audit: `docs/world-sim-migration-audit.md`
- Topology snapshot: `docs/module-signal-map.md`
- Source-correlation pattern catalog: `docs/cross-source-correlation.md`
- Mithril Roadmap Project: <https://github.com/orgs/moumantai-gg/projects/1>
