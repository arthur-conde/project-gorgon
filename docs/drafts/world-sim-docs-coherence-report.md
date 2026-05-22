# World-sim docs coherence report

Audit date: 2026-05-22
Audit basis: `main` at `bc5a608` (PR #654 ratification merged earlier today). Issue spot-checks for #601, #614–#618, #602–#613, #642, #643, #644, #646, #648, #654, #655.

Scope: terminology coherence across the world-sim design corpus — `docs/world-simulator.md`, `docs/world-sim-migration-audit.md`, `docs/module-signal-map.md`, `docs/cross-source-correlation.md`, `docs/world-simulator-orchestration-plan.md`, `docs/world-sim-shepherd.md`, `docs/world-sim-orchestrator.md`, and the three `.claude/agents/world-sim-*.md`.

This is a research deliverable. No edits to existing docs are proposed here; the companion file (`world-sim-glossary-draft.md`) is the draft that a follow-on PR would land. Drift / contradictions are flagged below; resolution is out of scope.

---

## Executive summary

- Corpus is coherent on the load-bearing architectural terms (PlayerWorld / ChatWorld / folder / composer / producer / frame / change event / domain frame / view / WorldMode). `docs/world-simulator.md` is unambiguously the owner; siblings reference it.
- **Producer scope drift** is the most significant finding: principle 10 in `world-simulator.md` narrows producers to *external-input sources only* ("Producers are NOT a mechanism for user-driven scheduling"), but `module-signal-map.md` and `world-sim-migration-audit.md` still describe Gandalf wake-at-T schedulers as "synthetic-frame producers." The audit has a top-level staleness note that gestures at this; `module-signal-map.md` does not.
- **"Tier" is overloaded across three namespaces** — Tier 1–4 in `cross-source-correlation.md` (correlation patterns), Tier 1–3 in `world-simulator-orchestration-plan.md` (verification gates), and Tier 1/2 in `world-sim-orchestrator.md` (ready-set sort). Same word, three meanings, never disambiguated where they meet.
- **"View" is overloaded** between the canonical world-sim sense (a cross-world composer, per `world-simulator.md`) and the legacy persistence wrapper `PerCharacterView<T>` referenced in `module-signal-map.md` and several agent prompts. World-simulator.md notes the legacy `PerCharacterView<T>` "becomes per-session-keyed" but the word "view" without the type suffix is read both ways.
- **PR #654's ratification ("change events flow on the world's bus") landed in `world-simulator.md` only.** The three sibling docs (`module-signal-map.md`, `cross-source-correlation.md`, `world-sim-migration-audit.md`) don't discuss the world bus surface directly and so are not contradicted — but a reader looking up "what does the bus carry" from any sibling would land on a definition only world-simulator.md provides. The reviewer agent (`world-sim-reviewer.md` §Check 1) cites principle 4 by number, not text, which means it implicitly already includes the post-#642 framing.
- **Per-component status doc lag**: PR #648 already retired `IQuestService` → `IPlayerQuestJournalService` and ship-noted the update in `module-signal-map.md`, but `world-sim-migration-audit.md` still references the old `IQuestService` name. The audit advertises itself as a dated snapshot, so this is acknowledged staleness, not undeclared drift.
- **`Mithril.GameReports` scope-row category mismatch** (`module-signal-map.md` table): tagged `reference` (the partition value), but the body text + `world-simulator.md` treat it as one of three top-level data *categories* ("external shared data sources"), distinct from the reference/world/character partition. The same row is doing two jobs: it's both a partition tag and a category tag, and they're not the same axis.
- **Cross-source vs cross-world** are used near-interchangeably across the corpus but mean subtly different things: "cross-source" = raw Player.log + chat fusion (the layer where `cross-source-correlation.md` operates); "cross-world" = PlayerWorld + ChatWorld fusion (the layer where views operate). The distinction is meaningful because Tier 1–4 patterns are *cross-source* even after world-sim lands; views compose *cross-world*, which is a different abstraction layer.
- **`IPlayer*` prefix is acknowledged misnomer for world-scope services** (`module-signal-map.md` §State-holder scope table). World-scope services keep a `Player*` prefix because rename churn is deferred until world-sim lands. No deadline; this will silently become technical debt the moment Phase 2 ships.
- **Shepherd v2.1 + reviewer agent terms are well-scoped to their docs** (verdict enum, escalation_reason enum, context pack, degraded mode, same-issue-class detection, follow-ons, the verdict markers). They don't leak into the architectural docs and the architectural terms don't leak into them; clean separation.

---

## Per-doc terminology inventory

What each doc introduces (and is the canonical owner of). Terms only used (not defined) are not listed here.

### `docs/world-simulator.md` (canonical owner of the architecture)

- **PlayerWorld**, **ChatWorld** (Vocabulary)
- **Frame**, **Frame<T>**, **IFrame** (principle 1, Contracts)
- **Folder**, **Composer**, **Producer** (principle 10) — the three state-machine kinds
- **Change event**, **Domain frame** (Vocabulary; principles 4/10/11)
- **Intra-world composer**, **Cross-world composer** (principle 10) — the latter is the explicit equation with **View**
- **View**, **IViewClock** (Vocabulary; Q5 resolution)
- **World** vs **world runtime** (Vocabulary — declared interchangeable)
- **WorldMode** {`Replaying`, `Live`} (principle 5/12; Vocabulary)
- **Mode == Live** (the gating condition for side-effect-emitting consumers)
- **`CalendarTimeAdvanced(Now, Mode)`**, **`TimeOfDayShift(from, to, at, Mode)`**, **`ModeChanged(from, to, at)`** (principle 13; Vocabulary)
- **Tri-property clock** = `(Now, Frame, Mode)` (principle 5; IWorldClock contract)
- **IWorldClock**, **IWorldEventBus**, **IFolder<TPayload>**, **IComposer**, **IFrameProducer<TPayload>**, **IWorld**, **IPlayerWorld**, **IChatWorld** (Contracts)
- **IInventoryView**, **IWordOfPowerView** (Contracts; worked examples)
- **Three categories of data** — world-derived state, external shared data sources, module-owned adjacent state (Vocabulary; dedicated section)
- **Per-session scope tier** = `(Server, Character)` from `IGameSessionService` (Vocabulary)
- **Sealed output boundary** (principle 2)
- **Per-frame resolution / finite DAG / no merger re-entry** (principle 11)
- **Session-replay from PG-session-start chat banner** (principle 9)
- **WorldClockTickProducer** (migration item #13; Decisions ratified post-#642)
- **Decisions ratified post-#642** (section heading; #643 + #644)

### `docs/world-sim-migration-audit.md`

- **Sleeper blocker** (executive summary; per-component classification)
- **Source spanning** (per-component classification axis)
- **Cross-FSM peek** (the canonical Rule-1 violation pattern)
- **Transition gate** vs **stamp** (the `_time.GetUtcNow()` site classification; 9 transition gates across 4 components)
- **Pure projector** vs reactor / state-bearing leaf
- **Self-pump assumption** (every `BackgroundService` owning its own `Subscribe<T>`)
- **Sleeper components** count + classification table
- **Wake-at-T synthetic-frame producer** ⚠️ STALE — Gandalf wake-at-T schedulers described as becoming producers, contradicted by `world-simulator.md` principle 10 post-revision. Audit's top-of-file staleness note gestures at this; downstream readers may miss it.
- **IFrameHandler<LocalPlayerLogLine>** ⚠️ STALE term — superseded by `IFolder<TPayload>` in current world-simulator.md
- **IQuestService** ⚠️ STALE name — retired to `IPlayerQuestJournalService` in PR #648; audit advertises itself as a dated snapshot

### `docs/module-signal-map.md` (canonical owner of scope vocabulary)

- **Scope** = partition kind: **reference**, **world (per-server)**, **character (per-server-per-character)** (the §Scope vocabulary section)
- **Self-scope independently** (both streams self-scope from their own intra-source banners)
- **Inputs / State machines / Outputs** (per-component shape; intentional template)
- **Pure projector** vs state-bearing component (entry classification)
- **Second event source** ⚠ marker (non-log inputs that mutate state — filesystem reconcile, wake-at-T schedulers, character-switch synthesis)
- **In-scope future signals** (forward-tense section for topology slots not yet wired)
- **Wall-clock TTL** (transition gates guarded by `_time.GetUtcNow()` deltas)
- **PerCharacterView<T>** (legacy persistence wrapper — referenced, not owned by this doc)
- **Wake-at-T synthetic-frame producer** ⚠️ STALE — same drift as the audit. No staleness banner on this doc.
- **`Mithril.GameReports` scope row** ⚠️ category-axis collision (scope-partition row tagged `reference` for what world-simulator.md categorizes as one of three top-level data categories — "external shared data sources")

### `docs/cross-source-correlation.md` (canonical owner of Tier 1–4 / cross-source patterns)

- **Tier 1** — keyed correlation (shared join key, bounded window)
- **Tier 2** — causal protocol state machine (request/response, no shared key)
- **Tier 3** — live read-order tiebreak (cross-source, same-game-second, both streams tailed live)
- **Tier 4** — order-insensitive consumer (the irreducible case)
- **PendingCorrelator<TKey,TReq>** (Tier 1 primitive)
- **DrainStale**, **EvictStale**, **FireUnmatched**, **TryTake**, **Add**
- **Unmatched callback / `onUnmatched`** (Tier 1 contract)
- **Correlation gate / TTL window** (not an ordering oracle)
- **Monotonic-time invariant** (PendingCorrelator's bucket order)
- **L0 ReadMonotonicTicks** (live-only tiebreak source — Tier 3)
- **L1 LogEnvelope<T>.IsReplay** (gate for Tier 3 applicability)
- **Same-game-second** (the tiebreak granularity)
- **#507 condition** (live-tail-falls-behind alarmed state)

### `docs/world-simulator-orchestration-plan.md`

- **Phase 0a / 0b / 1 / 2 / 3 / 4 / parallel** (the phase taxonomy)
- **Ready task** (dep graph node whose `depends_on` is all closed)
- **Dependency graph** (YAML structure)
- **Verification gates** with sub-tiers: **Tier 1 build / Tier 2 test / Tier 2.5 shepherd review / Tier 3 system** ⚠️ "Tier" collision with cross-source-correlation.md's namespace
- **Cross-phase invariants**
- **Stop conditions** (the escalation/pause taxonomy)
- **Worker** (the implementing agent)
- **Replay-determinism test** (system-tier check applicable to specific tasks)
- **Risk: low/medium/high**, **Estimate: S/M/L**, **Completion signal**, **parallel_ok** (per-task metadata fields)
- **Parallel track** (foundation-independent task pool)
- **Rollback / safe-abort** (revert PR pattern)

### `docs/world-sim-shepherd.md`

- **Shepherd** (per-issue delivery agent)
- **Verdict** enum: **`merged`**, **`needs-human`**, **`conflict`**, **`nothing-to-do`**, **`decomposed`**
- **Escalation reason** enum: `max_iterations`, `same_issue_class`, `worker_no_progress`, `human_review`, `merge_conflict`, `closed_without_merge`, `initial_implementation_failed`, `nothing_to_do`, `decomposed`, `needs_input`, `worker_failed`, `merge_command_failed`, `degraded_mode_cannot_iterate`, `shepherd_return_unparseable`
- **Shepherd lifecycle** phases: intake → initial implementation → review-fix loop → merge
- **Context pack** (5–15K token bundle built once at intake)
- **Degraded mode** (TeamCreate-unavailable fallback)
- **Same-issue-class detection** (review-loop escalation signal)
- **Verdict marker** `<!-- shepherd-verdict: ... -->` (machine-readable PR-comment first-line contract)
- **Follow-on** (out-of-scope finding filed as issue) — also defined in orchestrator doc
- **`worker`**, **`generic-reviewer`**, **`specialist-reviewer`** (teammate names)
- **v1 / v2 / v2.1** (shepherd design generations)
- **TeamCreate / TeamDelete / SendMessage** (continuity primitives)
- **outcome:** line (worker's structured return convention)

### `docs/world-sim-orchestrator.md`

- **Orchestrator** (per-project tick-based driver)
- **Tick** (one /loop invocation)
- **Circuit breaker** = the `pause` label on #601 + the umbrella-closed terminal
- **Cross-tick recovery** (step 1 — process a shepherd's marker comment left after a prior tick crashed mid-handler)
- **Ready set** (filtered dep-graph nodes eligible for dispatch)
- **Dep graph derivation** (UNION of YAML + open `module:world-sim` issues + Blocks/Depends edges)
- **Labels**: **`orchestrator-dispatch:<N>`**, **`orchestrator-blocked`**, **`orchestrator-followup`**, **`pause`**
- **ScheduleWakeup invariant** (every non-kill-switch exit must call ScheduleWakeup)
- **Ready-set sort tiers**: **Tier 1** planned migration tasks, **Tier 2** follow-ons ⚠️ "Tier" collision with both other namespaces
- **Cache-window pacing** (60s for active work, 1800s for idle — avoid 300–1200s)
- **Error breadcrumb** (`<!-- orchestrator-error: gh-failure -->` marker pattern, 3-strike escalation)
- **Spawn task chip** (`mcp__ccd_session__spawn_task` invocation pattern)

### `.claude/agents/world-sim-orchestrator.md`

- Operational mirror of `docs/world-sim-orchestrator.md`. Adds the inline §Tick-time reads probe shape, the §Escalation prompt template, the §Concurrency assumption (single-instance), and explicit "you do NOT do" boundaries. Doesn't introduce new architectural terms.

### `.claude/agents/world-sim-shepherd.md`

- Operational mirror of `docs/world-sim-shepherd.md`. Adds the §Generic code review prompt (canonical), the §Spawning named teammates section, the §Inline degraded mode behavior detail. Mirrors the verdict/escalation enums.

### `.claude/agents/world-sim-reviewer.md`

- **The four checks**: Principle adherence (1-13), Phase-aware migration, Replay-determinism inspection, Audit cross-reference
- **Confidence rubric** (75–89 important, 90–100 critical; <75 not reported)
- **`<!-- world-sim-review-verdict: ... -->`** marker (clean | findings)
- Cites principles 3, 10, 11, 12, 13 by number — implicit dependency on `world-simulator.md` numbering staying stable. (Principle 4's post-#642 framing is implicitly covered because the reviewer cites by number.)

---

## Cross-doc consistency findings

Term × doc × treatment matrix. Only rows where the term is meaningfully *used* (not just incidentally name-dropped) appear.

| Term | world-simulator.md | migration-audit.md | module-signal-map.md | cross-source-correlation.md | orchestration-plan.md | shepherd / orch. / reviewer | Verdict |
|---|---|---|---|---|---|---|---|
| **PlayerWorld / ChatWorld** | Defined | Used consistently | Used consistently | n/a | Used (phase preconditions) | Used consistently | Coherent |
| **Folder** | Defined (principle 10) | Used (calls them `IFrameHandler<…>` in places — stale) | Used | n/a | Used | Used (reviewer cites principle 10) | ⚠️ minor stale name in audit |
| **Composer** | Defined | Used | Used | n/a | Used | Used | Coherent |
| **Producer** | Defined — **narrowed to external-input sources** | Uses "wake-at-T synthetic-frame producer" | Uses "wake-at-T synthetic-frame producer" | n/a | Used | Used | ⚠️ **DRIFT — sibling docs predate principle 10's narrowing** |
| **Frame / Frame<T>** | Defined | Used | Used loosely | n/a | n/a | Used | Coherent |
| **Change event** | Defined; post-#642 — flows on bus | n/a | Used implicitly | n/a | n/a | Reviewer cites principle 4 by number (implicitly post-#642 framing) | Resolved in canonical doc; siblings don't contradict but don't reinforce |
| **Domain frame** | Defined (cross-world consumption contract) | n/a | Used implicitly | n/a | n/a | Used in reviewer (principle 11) | Coherent |
| **View** | Defined (= cross-world composer) | Used (worked examples) | Used (cross-source view) — also references **`PerCharacterView<T>`** (legacy persistence wrapper, same word) | n/a | Used (phase 2 invariants) | Used | ⚠️ "view" overloaded with `PerCharacterView<T>` |
| **WorldMode {Replaying, Live}** | Defined (principle 5/12) | n/a (audit predates principle 12) | n/a | n/a | Used (Mode transitions invariant) | Used (reviewer cites principle 12) | Coherent |
| **Mode == Live gate** | Defined | n/a | n/a | n/a | Used | Used | Coherent |
| **CalendarTimeAdvanced / TimeOfDayShift / ModeChanged** | Defined (principle 13) | n/a (audit predates) | Used (Gandalf entry) | n/a | Used (cross-phase invariants) | Used (reviewer principle 13) | Coherent (after audit's staleness banner) |
| **Three categories of data** | Defined | n/a | Used (`Mithril.GameReports` row) — but row also tags it `reference` (partition value) | n/a | n/a | n/a | ⚠️ Category-vs-partition axis collision |
| **Scope: reference / world / character** | principle 6 — uses module-signal-map.md framing | n/a | **Defines this** | n/a | n/a | n/a | Coherent — single owner |
| **Per-session scope tier** | Defined | n/a | Implicit | n/a | n/a | n/a | Coherent |
| **Tier 1–4** | n/a (refers to cross-source-correlation.md) | Used (refers to cross-source-correlation.md) | Used (refers to cross-source-correlation.md) | **Defines this** | n/a (uses Tier 1/2/2.5/3 for verification — different namespace) | n/a (orchestrator uses Tier 1/2 for sort — third namespace) | ⚠️ **"Tier" overloaded across three namespaces** |
| **Cross-source** | Used | Used | Used | Defines | Used (precondition language) | Used | Coherent — but see "cross-source vs cross-world" |
| **Cross-world** | Defined (= composes across PlayerWorld + ChatWorld) | n/a (audit predates "world" terminology) | n/a | n/a | n/a | n/a | Owned by world-simulator.md; not contradicted |
| **Sleeper blocker** | n/a | **Defines this** | n/a | n/a | n/a | n/a | Coherent — single owner |
| **PendingCorrelator<TKey,TReq>** | Referenced (worked example) | Referenced | Referenced | **Defines this** | n/a | n/a | Coherent |
| **L0 / L1 / L2** | n/a | Used (L1 driver) | Used (L1 driver / L1 classified pipe) | **Defines L0 + L1** | n/a | n/a | Coherent — but L0 / L1 / L2 layer numbering not introduced anywhere world-sim docs explicitly; cross-source-correlation.md is the de facto definer |
| **Self-scope** | Used (principle 7) | n/a | **Defines this** | n/a | n/a | n/a | Coherent |
| **Verdict (shepherd)** | n/a | n/a | n/a | n/a | n/a | **Defined in shepherd; used in orchestrator + reviewer** | Coherent |
| **Verdict marker (PR comment)** | n/a | n/a | n/a | n/a | n/a | **Defined in shepherd; mirror in reviewer + orchestrator** | Coherent |
| **Follow-on** | n/a | n/a | n/a | n/a | n/a | **Defined twice — shepherd doc + orchestrator doc** | ⚠️ Definition duplicated; check for drift |
| **Context pack** | n/a | n/a | n/a | n/a | n/a | Defined in shepherd | Coherent |
| **Degraded mode** | n/a | n/a | n/a | n/a | n/a | Defined in shepherd | Coherent |
| **Pause label / Circuit breaker** | n/a | n/a | n/a | n/a | n/a | Defined in orchestrator | Coherent |
| **Worker / Reviewer** | n/a | n/a | n/a | n/a | Used | Defined in shepherd | Coherent |
| **Orchestrator** | n/a | n/a | n/a | n/a | Used (as "the orchestrator," implicit external system) | Defined in orchestrator doc | Coherent — orchestration-plan.md predates the agent's existence, but the gap is acknowledged |
| **Phase 0a/0b/1/2/3/4/parallel** | n/a | n/a | n/a | n/a | **Defines this** | Used | Coherent |

---

## Drift / staleness / undefined-but-used findings

Each finding cites a specific location and what it conflicts with.

### Drift

1. **Wake-at-T schedulers described as producers** — *moderate drift*.
   - `world-sim-migration-audit.md:441-456` (§Migration-plan spot-checks #11) and `module-signal-map.md:441-466` (Gandalf entry) treat Gandalf's `TimerExpirationScheduler` / `ShiftAlarmService` as "synthetic-frame producers" that mint wake frames into the world's merger.
   - `docs/world-simulator.md:60-61` (principle 10) explicitly narrows producers: "Producers are NOT a mechanism for user-driven scheduling — user-side concerns (Gandalf timers, alarm scheduling) consume world domain events and run their own module-internal logic against them; they do not register producers in a world's merger. The world is sealed at its input."
   - The audit doc's top-of-file note (lines 9-19) gestures at this ("the folder/composer/producer taxonomy was narrowed (producers now restricted to external-input sources only, not wake-at-T)"). `module-signal-map.md` has no equivalent staleness banner.
   - Implication: a cold reader of `module-signal-map.md`'s Gandalf entry will infer that the migration target involves producers. The actual target is `CalendarTimeAdvanced` consumption (principle 13 + migration item #12).

2. **`IFrameHandler<LocalPlayerLogLine>`** in `world-sim-migration-audit.md:74,76,468-471`. Superseded by `IFolder<TPayload>` everywhere else in the corpus. Audit's staleness note covers this implicitly but doesn't enumerate it. Same audit also still names the migrated `IQuestService` as a live target (per PR #648 it's now `IPlayerQuestJournalService`).

3. **`PR-comment verdict marker` definition appears twice** — once in `docs/world-sim-shepherd.md:425-454` (Posting the combined review comment) and once in `.claude/agents/world-sim-shepherd.md:425-454`. The two are intentionally synced (the design notebook says "the operational spec is the agent file"). Verified consistent at audit time, but the duplication is a maintenance risk — same-day drift on a future edit is possible.

4. **`Mithril.GameReports` scope-vs-category collision** — `module-signal-map.md:50` puts it in the **scope** table tagged `reference (external shared data; per-character snapshot files, externally sourced)`. The parenthetical caveat is doing all the work — the row implies `reference` is the partition value, but GameReports is per-character (clearly NOT `reference`-partitioned in the same sense as items.json). `world-simulator.md` §Three categories of data places GameReports cleanly as an "external shared data source" (category), with no scope-partition value. Two distinct axes; the scope-table row blurs them.

### Staleness

5. **`IQuestService` references in `world-sim-migration-audit.md:132-153,369-378`.** PR #648 retired this to `IPlayerQuestJournalService`. Audit advertises itself as a dated snapshot; updating in place would defeat the snapshot purpose. The drift is acknowledged.

6. **`IPlayerWeatherTracker` / `IPlayerCelestialStateService` naming**. Per `module-signal-map.md:56-58,63` these are world-scope despite the `IPlayer*` prefix. The rename is "deferred until world-sim lands." No tracking issue; would become silent debt the moment Phase 2 ships.

### Undefined-but-used

7. **`L0` / `L1` / `L2`** as layer numbering. Referenced in `module-signal-map.md` (`L1 driver`, `L1 classified pipe`), `cross-source-correlation.md` (`L0 ReadMonotonicTicks`, `L1 LogEnvelope<T>.IsReplay`), the audit, and several issue bodies — never explicitly defined as a layer hierarchy. A reader can infer (L0 = tail-level, L1 = classified-line-level, L2 = parsed-event-level) but the inference isn't documented anywhere.

8. **`Sequence`** (frame ordering tie-breaker) — used in `world-simulator.md:99,511`, `module-signal-map.md`, and the audit, but defined only as a field on log lines / envelopes, not as a frame-level concept in any glossary.

9. **`world bus` vs `IWorldEventBus`** — both used; `world bus` is the informal name for the typed pub-sub primitive. Not contradictory but worth one entry.

10. **`(Server, Character)` tuple notation** — used consistently as the per-session key. Defined obliquely in `world-simulator.md` Vocabulary ("Per-session scope tier — `(Server, Character)` derived from `IGameSessionService`") and in `module-signal-map.md` §Scope vocabulary, but the *tuple-notation convention* itself isn't owned anywhere.

11. **`module:world-sim` label** — referenced repeatedly in the orchestrator + shepherd docs as the canonical query filter. Not defined in any doc — just appears as a working assumption.

### Duplicate-defined

12. **Follow-on schema** — appears in both `docs/world-sim-orchestrator.md` (§Follow-on handling) and `docs/world-sim-shepherd.md` (output contract §Follow-ons block). Currently consistent (title / files / blocks / body). Two definitions for the same schema = two places to edit if the schema changes.

13. **Verdict marker contract** — three flavors (`shepherd-verdict`, `world-sim-review-verdict`, `generic-review-verdict`) defined across shepherd + reviewer docs. Consistent at audit time, but the contract is split.

14. **Generic-review prompt** — `world-sim-shepherd.md:460-503` says "this is the **canonical** generic-review prompt — no separate `.claude/agents/*` file backs it." Good — explicit single owner.

### Stale-but-acknowledged (intentional)

15. The audit's staleness banner (`world-sim-migration-audit.md:9-19`) acknowledges:
    - folder/composer/producer taxonomy narrowing
    - `WorldMode` addition (principles 12 + 13)
    - `Mithril.GameReports` extraction

    The findings under "needs migration" / "sleeper blocker" remain valid; only terminology + a few item details are stale.

---

## Recommendations on glossary scope

In scope for the follow-on glossary PR (the companion `world-sim-glossary-draft.md`):

- Architectural primitives: PlayerWorld, ChatWorld, World, World runtime, View, Folder, Composer, Producer, Frame, Frame<T>, IFrame, Change event, Domain frame
- Clock + mode: IWorldClock, WorldMode, Replaying, Live, Tri-property clock, CalendarTimeAdvanced, TimeOfDayShift, ModeChanged, IViewClock
- Contracts: IWorld, IPlayerWorld, IChatWorld, IFolder<TPayload>, IComposer, IFrameProducer<TPayload>, IWorldEventBus, IInventoryView, IWordOfPowerView
- Categories + scope: Three categories of data, World-derived state, External shared data sources, Module-owned adjacent state, Scope (reference / world / character), Per-session scope tier, `(Server, Character)`, Self-scope
- Replay + determinism: Replay-determinism, Replay (mode), Live (mode), Mode == Live gate, Sealed output boundary, Per-frame resolution, finite DAG
- Cross-source: Cross-source (vs cross-world), Tier 1, Tier 2, Tier 3, Tier 4, PendingCorrelator<TKey,TReq>, Correlation gate, Same-game-second
- Phase / migration vocabulary: Phase 0a / 0b / 1 / 2 / 3 / 4 / parallel, Ready task, Ready set, Dep graph, Cross-phase invariant, Sleeper blocker, Cross-FSM peek, Transition gate (vs stamp), Wall-clock TTL, Pure projector, Source spanning
- Agent / orchestration vocabulary: Shepherd, Orchestrator, Worker, Reviewer (specialist + generic), Context pack, Verdict, Verdict marker, Escalation reason, Same-issue-class detection, Degraded mode, Follow-on, Tick, Cross-tick recovery, Circuit breaker
- Labels: `module:world-sim`, `orchestrator-dispatch:<N>`, `orchestrator-blocked`, `orchestrator-followup`, `pause`
- Infrastructure: L0, L1, L2, Sequence, `IClassifiedPlayerLogStream`, `IChatLogStream`, world bus

Intentionally **out of scope** (local-to-a-doc terms):

- `world-sim-shepherd.md` v1/v2/v2.1 generation labels (versioning detail; not a vocabulary concept)
- Specific PR comment templates (`<!-- shepherd-verdict: ... -->` exact text — the *concept* "verdict marker" goes in glossary; the specific marker strings are documented in the shepherd agent file)
- Shepherd lifecycle phase names ("intake", "initial implementation", "review-fix loop", "merge") — these are internal lifecycle phases of one doc
- `outcome:` line worker convention — internal contract between shepherd and worker
- Verification-gate Tier 1/2/2.5/3 numbering — this is a sub-namespace of the orchestration plan and the "Tier" overload note in the cross-references is sufficient
- Specific `gh` command shapes
- TeamCreate / TeamDelete / SendMessage — these are harness primitives, not world-sim concepts; the glossary should reference the shepherd doc for "this is the continuity mechanism" without redefining the harness primitives

Drift / contradictions flagged in the glossary entries themselves (with a "⚠ note:" line):

- **Producer** entry should note the principle-10 narrowing and that audit + module-signal-map have residual "synthetic-frame producer" language that predates it.
- **View** entry should disambiguate from `PerCharacterView<T>` (the legacy persistence wrapper).
- **Tier** entry — actually, treat as three separate entries (Tier 1–4 correlation patterns get one block; verification-gate tiers get a brief note in the Verification gate entry; orchestrator ready-set sort tiers stay local to the orchestrator agent file and aren't worth a glossary entry).
- **Cross-source vs cross-world** — both entries, with a "see also: Cross-world" / "see also: Cross-source" mutual pointer + a short note that they refer to different abstraction layers.
- **Change event** — note the post-#642 ratification that it flows on the world bus as a first-class single-world surface (the original "never crosses the world boundary" framing is superseded; see "Decisions ratified post-#642" in world-simulator.md).
- **`Mithril.GameReports`** — entry under "External shared data sources" subsection; note that the `reference` tag in `module-signal-map.md`'s scope table is the partition value, distinct from the data-category value.

Out-of-scope but worth a tracking issue (NOT to be opened in this audit — the user owns issue filing):

- The producer-scope drift in `module-signal-map.md` + the audit. The audit advertises itself stale; the signal-map doesn't.
- The `IPlayer*`-prefix-for-world-scope misnomer (the rename Phase 2 will eventually trip over).
- The Tier-overload reduction (only the cross-source Tier 1–4 is canonical; the orchestration plan + orchestrator use "Tier" for sub-namespaces).

Open design questions the audit surfaces but does NOT resolve:

- Does the post-#642 framing ("change events flow on bus") deserve a backport into `module-signal-map.md`'s entries that mention bus surfaces? Likely yes for the Gandalf entry (which subscribes to `CalendarTimeAdvanced` — a domain event on the bus), but the doc doesn't currently distinguish change events from domain frames in its prose.
- Should the L0 / L1 / L2 layer numbering get a one-paragraph definition somewhere, given how often it's referenced? Likely a paragraph in either `module-signal-map.md` or a new `docs/log-pipeline.md` — out of scope for this glossary.
- Should the audit get a refresh pass (re-classify 15 components against current world-sim.md), or is the dated-snapshot framing intentional and adequate? Out of scope.
