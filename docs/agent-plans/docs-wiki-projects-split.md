# Plan: docs / wiki / Projects three-tier split

A roadmap for a follow-up agent session. Migrates today's mostly-flat
`docs/` folder into a three-tier system: GitHub Projects for live
roadmap state, GitHub Issues for tasks, and a docs/wiki split for
narrative content. Replaces the current scattered backlogs (free-form
"future work" sections inside `*-roadmap.md` files) with a queryable
single source of truth.

---

## Index — orient before doing anything

### Repositories you'll touch

| What | Path / URL | Notes |
|---|---|---|
| Code repo (this) | [`i:\src\project gorgon`](../../.) | Where this plan lives. PR target is `main`. |
| Wiki repo | `https://github.com/arthur-conde/project-gorgon.wiki.git` | Already initialised with a placeholder `Home.md`. Clone to a sibling path (e.g. `i:\src\project-gorgon.wiki`). Pushes go straight to the wiki — no PR review. |
| Issues | `https://github.com/arthur-conde/project-gorgon/issues` | Currently 7 open (see snapshot below). |
| Projects v2 | `https://github.com/users/arthur-conde/projects` | None exist yet for this repo. |

### Current `docs/` folder snapshot

| File | Lines | Proposed home | Reason |
|---|---:|---|---|
| [`cdn-reference-data.md`](../cdn-reference-data.md) | 111 | **wiki** | Stable reference; doesn't change with code. |
| [`releasing.md`](../releasing.md) | 157 | **wiki** | Process doc; broad audience. |
| [`icon-prompts.md`](../icon-prompts.md) | 131 | **wiki** | Stable reference (Leonardo prompts). |
| [`treasure-system.md`](../treasure-system.md) | 205 | **wiki** | Stable reference. |
| [`words-of-power-log-signals.md`](../words-of-power-log-signals.md) | 165 | **wiki** | Stable reference. |
| [`user-guide/arwen.md`](../user-guide/arwen.md) | 157 | **wiki** as `User-Guide-Arwen` | User-facing guide. |
| [`user-guide/legolas.md`](../user-guide/legolas.md) | 180 | **wiki** as `User-Guide-Legolas` | User-facing guide. |
| [`celebrimbor-roadmap.md`](../celebrimbor-roadmap.md) | 200 | **stays in `docs/`**, trimmed | Narrative + design rationale only; backlog → Issues. |
| [`smaug-roadmap.md`](../smaug-roadmap.md) | 91 | **stays in `docs/`**, trimmed | Same pattern. |
| [`mithril-reference-roadmap.md`](../mithril-reference-roadmap.md) | 257 | **stays in `docs/`** as historical record | Phases 0–6 done; Phase 6 follow-ups → Issues. |
| [`mithril-reference-shape-quirks.md`](../mithril-reference-shape-quirks.md) | 273 | **stays in `docs/`** | Design notebook; co-evolves with code. |
| [`gandalf-quest-timers.md`](../gandalf-quest-timers.md) | 55 | **decision needed** — ask user | Active design vs stable reference unclear. |
| [`agent-plans/inventory-replay.md`](inventory-replay.md) | (unread) | **stays in `docs/agent-plans/`** | Implementation spec; tracked by issue #7. |
| [`agent-plans/refresh-and-validate-tool.md`](refresh-and-validate-tool.md) | 328 | **stays in `docs/agent-plans/`** | Implementation spec; needs new tracking issue (see backlog below). |
| `agent-plans/docs-wiki-projects-split.md` | (this file) | **stays in `docs/agent-plans/`** | This plan; deleted on completion. |

### Current open Issues (snapshot at plan-write time)

```
#12  Typed QuestRequirement records + repeatable-quest-timer module
#9   ReferenceDataService.RefreshAllAsync runs sequentially; parallelize with Task.WhenAll
#8   Celebrimbor: augment-pool view ignores power.Slots, over-counting eligible rolls
#5   Close the last Shell→Module compile-time leaks
#4   The surveyor UI is dated
#3   Fresh clone running the tests generates errors
#2   GandalfSplitMigrationTests... flakes on CI
```

Re-run `gh issue list --state open` at the start of the session to get
fresh state — this snapshot rots.

### Backlog items pending issue creation

Pulled from current `*-roadmap.md` "future work" sections, plus the
just-merged Mithril.Reference work, plus standing memory entries:

**From `mithril-reference-roadmap.md` (Phase 6 optional future work):**

- `tools/RefreshAndValidate` — the agent plan at
  [`docs/agent-plans/refresh-and-validate-tool.md`](refresh-and-validate-tool.md)
  exists but has no issue. File one and link to the plan.
- Field-coverage walker for the validation harness (compare raw `JObject`
  property names against POCO declarations; logs new fields).
- Live-CDN parity test marked `[Trait("Category", "Live")]`, gated CI cron.

**From `mithril-reference-shape-quirks.md`:**

- Live-game spot-check of nested-array Requirements quests
  (vampire/day-of-week pattern near line 40605 of `quests.json`) to
  confirm the AND-flatten interpretation. Currently flagged as
  "Verification owed" in the doc.

**From `smaug-roadmap.md`:**

- v1.1 features: bundle sources_items, Civic Pride from export, Sell
  Planner tab, cap-aware, gold-pool, buy-prices. Read the file for the
  precise list — file one issue per feature.
- v1.2 + v2 backlog → tag with `Target Version: Backlog` initially.

**From `celebrimbor-roadmap.md`:**

- Aggregator first-output bug (also recorded in the user's memory at
  `~/.claude/projects/i--src-project-gorgon/memory/celebrimbor_aggregator_first_result_bug.md`).
- Remaining ResultEffects parser prefixes (memory:
  `celebrimbor_result_effects.md` lists the deferred backlog).

**From standing memory entries** (cross-cutting, not tied to one
roadmap doc):

- GorgonQueryBox shortcuts feature
  (`gorgon_query_box_shortcuts.md`).
- Item rarity color scheme + RarityToBrushConverter
  (`rarity_color_scheme.md`).
- Celebrimbor aggregator first-output bug
  (`celebrimbor_aggregator_first_result_bug.md`). *Confirmed standalone
  bug; not yet duplicated under `celebrimbor-roadmap.md`'s entry above —
  if both surfaces produce an issue candidate, file once and reference
  both.*
- Smaug remaining tabs
  (`smaug_remaining_tabs.md`). *Covered transitively by the
  `smaug-roadmap.md` v1.1/v1.2/v2 backlog above; named here so the
  memory audit (Phase 3.6) doesn't miss the entry.*

> **Footnote on `quest_typed_requirements.md`:** the `Typed
> QuestRequirement records + repeatable-quest-timer module` memory
> entry is **already tracked by issue #12**. *Do not* file a new
> issue. Instead, in Phase 3.5 below, rewrite the memory body to a
> `Tracked in #12` pointer.

---

## Goal

Three-tier system for project knowledge:

| Tier | Home | Holds | Why there |
|---|---|---|---|
| **Live state** | GitHub Projects | Roadmap, prioritisation, board view | Custom fields, milestone views, queryable |
| **Tasks** | GitHub Issues | Pending units of work, bugs, acceptance criteria | State, ownership, discussion |
| **Narrative** | wiki + `docs/` | Design rationale, architecture, conventions, agent specs | Long-form context, code-adjacent review for co-evolving docs |

Within Tier 3:

- **wiki**: stable reference (process docs, user guides, architecture).
- **`docs/`**: co-evolves with code (roadmap *narrative*, design
  notebooks, agent plans).

---

## Open questions to resolve at session start

Block on these before doing irreversible work:

1. **`docs-vs-wiki split confirmed?`** Does the table in the index
   above match Arthur's instinct? Specifically:
   - `gandalf-quest-timers.md` — wiki or `docs/`?
   - Anything else he wants relocated from the proposed split?
2. **`Project shape`** — one Project per active module
   (Mithril.Reference, Celebrimbor, Smaug, Gandalf) or one big
   "Mithril" Project with a `Module` field? **Decision (resolved
   2026-04-30 with Arthur):** single Project (`Mithril Roadmap`) with
   `Module` as a single-select field. Solo dev with ~20 issues — one
   board with field-based filters is simpler than five boards to keep
   in sync. Per-module Projects remain a documented alternative if
   the single board gets crowded later.
3. **`gh project` scope** — Arthur may need to run
   `gh auth refresh -s project` once to grant the Projects v2 scope.
   The session can't do this for him; ask if `gh project list` returns
   a permissions error.
4. **`Backlog tagging`** — issues created from `*-roadmap.md` should
   tag with the current target version. Confirm what the working
   "current version" label is (looks like `v1.0` based on the existing
   memory entries, but verify).

If 1 changes, re-derive Phase 1's wiki target list before executing.
If 2 changes, redo the Project creation in Phase 2.

---

## Phased execution

### Phase 0 — Confirm access *(~15 min)*

1. `gh auth status` — confirm authenticated.
2. `gh project list --owner arthur-conde` — confirm `project` scope. If
   it errors, ask Arthur to run `gh auth refresh -s project`.
3. `git clone https://github.com/arthur-conde/project-gorgon.wiki.git`
   into a sibling path (`i:\src\project-gorgon.wiki` or wherever the
   working directory is configured to allow). Confirm `Home.md` is
   readable and a dummy `git push` works. The wiki is already
   initialised so no UI step is needed.
4. Create a feature branch off `main`:
   `git checkout -b chore/docs-wiki-projects-split`. All `docs/`
   changes flow through one PR.

**Exit:** can read/write wiki, can `gh project create`, branch ready.

### Phase 1 — Migrate stable-reference docs to wiki *(~1 hour)*

For each doc in the "wiki" rows of the index table:

1. Copy the file content into the wiki repo as a new page named per
   the table (PascalCase with hyphens, no `.md` prefix in the URL but
   the file itself is `.md`).
2. **Rewrite intra-doc links.** Every `[X](other-doc.md)` →
   `[X](Other-Doc)`. Wiki links are by page name without the `.md`
   extension. Mass-grep for `.md)` to find them all.
3. **Rewrite source-tree links.** Relative paths into `src/` aren't
   reachable from the wiki — convert to absolute
   `https://github.com/arthur-conde/project-gorgon/blob/main/src/...`.
   Pin to `main` (not a commit hash) so they survive code refactors;
   accept that they may rot if files move.
4. Commit + push the wiki repo for each file (or batch — wiki history
   is shallow and not part of code review, so granularity doesn't
   matter much).
5. **Don't delete from `docs/` yet** — defer to Phase 4 so the deletion
   is reviewable in a single commit.

**`Home.md` rewrite:** turn it into an index linking to the migrated
pages plus a "where does new content go?" section explaining the
three-tier rule. Skeleton:

```markdown
# project-gorgon wiki

Stable reference content for Mithril (the WPF companion app for
Project Gorgon). Operational state lives in
[Projects](https://github.com/users/arthur-conde/projects); active
tasks live in [Issues](https://github.com/arthur-conde/project-gorgon/issues);
design rationale and agent plans live in
[docs/](https://github.com/arthur-conde/project-gorgon/tree/main/docs).

## Reference

- [CDN Reference Data](CDN-Reference-Data)
- [Releasing](Releasing)
- [Icon Prompts](Icon-Prompts)
- [Treasure System](Treasure-System)
- [Words of Power Log Signals](Words-of-Power-Log-Signals)

## User Guides

- [Arwen](User-Guide-Arwen)
- [Legolas](User-Guide-Legolas)

## Where does new content go?

| If you're writing... | Put it... |
|---|---|
| A pending unit of work | A GitHub Issue |
| Roadmap / prioritisation state | A GitHub Project |
| Architecture / process / how-to | The wiki |
| Design rationale that co-evolves with code | `docs/` in the code repo |
| An implementation spec for a follow-up agent | `docs/agent-plans/` |
```

**Exit:** wiki has 7 reference pages + an index; `docs/` originals
still exist (deleted in Phase 4).

### Phase 2 — Set up the Project + custom fields *(~30 min)*

Create one Project (decision: see open question #2):

```bash
gh project create --owner arthur-conde --title "Mithril Roadmap"
```

Add custom fields via `gh project field-create`:

- `Status` (single-select): Todo / In Progress / Blocked / Done
- `Priority` (single-select): P0 / P1 / P2 / P3
- `Effort` (single-select): XS / S / M / L
- `Target Version` (single-select): v1.0 / v1.1 / v1.2 / v2 / Backlog
- `Module` (single-select): Samwise / Pippin / Legolas / Gandalf /
  Elrond / Bilbo / Arwen / Saruman / Smaug / Celebrimbor / Palantir /
  Mithril.Reference / Shell

Use the Project's filter-by-`Module` view to get per-module slices
without maintaining separate boards.

**Note:** `gh project field-create` syntax for single-select options
needs a single-line list. Use `--single-select-options "Todo,In
Progress,Blocked,Done"` etc.

**Exit:** one Project (`Mithril Roadmap`) with the five custom fields
above.

### Phase 2.5 — Label taxonomy + issue templates *(~30 min)*

Stock GitHub labels won't scale. Add three label axes so issue triage
and Project filters stay coherent:

```bash
# module:* — 13 labels, one per module surface
for m in samwise pippin legolas arwen elrond gandalf bilbo smaug \
         celebrimbor palantir saruman mithril.reference shell; do
  gh label create "module:$m" --color BFD4F2 --description "Touches the $m module"
done

# area:* — 5 cross-cutting axes
gh label create "area:parser"  --color C5DEF5 --description "Log parsing / state machines"
gh label create "area:ui"      --color D4C5F9 --description "WPF / XAML / ViewModels"
gh label create "area:ci"      --color BFDADC --description "Build / CI / release"
gh label create "area:docs"    --color C2E0C6 --description "Documentation"
gh label create "area:test"    --color FAD8C7 --description "Test infrastructure / coverage"

# type:* — 3 dispositions
gh label create "type:bug"     --color D73A4A --description "Defect"
gh label create "type:feature" --color A2EEEF --description "New capability"
gh label create "type:chore"   --color CCCCCC --description "Maintenance / refactor / housekeeping"
```

(Stock `bug` / `enhancement` labels can stay or be deleted — the
`type:*` axis supersedes them.)

Add minimal issue templates at `.github/ISSUE_TEMPLATE/`:

- `bug.yml` — title, repro steps, expected/actual, module dropdown,
  area dropdown.
- `feature.yml` — title, motivation, sketch of acceptance criteria,
  module dropdown.

The dropdowns set labels automatically (`labels:` block on each
option), so triage doesn't need a manual label pass for templated
issues.

**Exit:** ~21 labels created; two YAML templates in
`.github/ISSUE_TEMPLATE/`.

### Phase 3 — File backlog issues + populate Projects *(~1.5 hours)*

For each item in the "Backlog items pending issue creation" section of
the index above:

1. `gh issue create --title "..." --body "..."` with a body that
   includes a link to the relevant agent plan / roadmap section /
   memory entry.
2. `gh project item-add {project-num} --owner arthur-conde --url
   {issue-url}` to attach the issue to the right Project.
3. Set `Target Version`, `Priority`, `Effort` via `gh project
   item-edit`. Pull priority from context — anything Arthur explicitly
   prioritised in this session is P0/P1; everything else is P2 by
   default.

For the **already-open issues** (#2, #3, #4, #5, #8, #9, #12), add them
to the relevant Project the same way. Triage each: which Project does
it belong to?

- #2 GandalfSplitMigrationTests flake → Gandalf Project
- #3 Fresh clone test errors → Mithril.Reference (or Mithril Release
  Planning — judgment call)
- #4 Surveyor UI dated → Smaug? actually surveyor is Legolas — file as
  Legolas-tagged on Mithril Release Planning since there's no Legolas
  Project
- #5 Shell→Module compile-time leaks → Mithril Release Planning
- #8 Celebrimbor augment-pool → Celebrimbor
- #9 RefreshAllAsync parallelisation → Mithril.Reference
- #12 Typed QuestRequirements + repeatable-quest-timer → Gandalf
  (timer module is a Gandalf feature)

**Exit:** every pending unit of work is an issue, attached to a Project,
with a Target Version and Priority.

### Phase 3.5 — Rewrite migrated memory entries to pointers *(~30 min)*

For each "to do / future work" memory entry that became (or already
was) a tracked issue, replace the body with a one-line pointer so the
`MEMORY.md` index entry survives but the duplicated content goes away.
*(User decision 2026-04-30: keep memory entries as pointers, don't
delete.)*

**Memory entries → issues:**

| Memory file | Issue | Action |
|---|---|---|
| `quest_typed_requirements.md` | **#12 (existing)** | Rewrite body to pointer; **do not** file a new issue. |
| `gorgon_query_box_shortcuts.md` | new (Phase 3) | Rewrite after issue is filed. |
| `rarity_color_scheme.md` | new (Phase 3) | Rewrite after issue is filed. |
| `celebrimbor_aggregator_first_result_bug.md` | new (Phase 3) | Rewrite after issue is filed. |
| `smaug_remaining_tabs.md` | new (Phase 3, multiple) | Rewrite to point at the parent Project filter for `module:smaug`. |

**Pointer template** (replace whole body):

```markdown
---
name: {{original name}}
description: Tracked in #NN — {{one-line summary}}
type: project
---

Tracked in https://github.com/arthur-conde/project-gorgon/issues/NN.
```

Update the `description:` frontmatter to match. Leave `MEMORY.md`
index entry intact — its existing pointer line still resolves.

**Exit:** every "to do" memory entry is a 1-line pointer; no
duplicated content.

### Phase 3.6 — Audit reference / current-state memory entries *(~30 min)*

For each "Reference / current-state" memory entry, decide: **delete**
(if it duplicates code-derivable state per `CLAUDE.md`'s memory rules)
or **keep** (if it captures non-obvious context).

| Memory file | Action | Why |
|---|---|---|
| `gorgon_architecture.md` | **delete** | Duplicates `CLAUDE.md`'s architecture section. |
| `mithril_reference_design_notebook.md` | **keep**, narrow | Body is a pointer at `docs/mithril-reference-shape-quirks.md`; keep but verify the pointer still resolves after Phase 4. |
| `samwise_parser.md` | **keep** | Captures gardening identification trade-offs not in code. |
| `character_presence_service.md` | **keep** | Cross-module shell coupling note not obvious from code. |
| `celebrimbor_result_effects.md` | **keep** | Tracks the ~686-effect deferred backlog by category. |
| `shell_module_coupling.md` | **keep** | Status doc for the leak audit; non-obvious from code. |
| `gorgon_calibration_repo.md` | **keep** | External-repo pointer; would otherwise need to grep for it. |

Drop the deleted entries from `MEMORY.md`'s "Reference / current-state"
section.

**Exit:** memory's reference section is leaner; no entries duplicate
code or `CLAUDE.md`.

### Phase 3.7 — Audit `~/.claude/plans/` folder *(~1.5 hours)*

46 gorgon-related plan files exist under `C:\Users\arthu\.claude\plans\`
(ripgrep for the module names). Most are spent — the work shipped via
a merged PR — but the folder hasn't been pruned. *(User decision
2026-04-30: audit + delete spent plans; out-of-repo chore.)*

For each plan file:

1. Identify the corresponding work. Try, in order:
   - Grep the plan body for a `gh pr` / `github.com/.../pull/` URL.
   - Match the plan title against `gh pr list --state merged --search "<keyword>"`.
   - Match the filename's keyword against branch names in
     `git -C "i:/src/project gorgon" branch -a --merged main`.
2. **If the work merged:** delete the plan file. The git history is
   the authoritative record.
3. **If unfinished:** file an issue (or attach to an existing one)
   capturing the leftover work, then delete the plan file. Issue
   description references the plan via a quoted excerpt if useful.
4. **If unclear:** leave the file in place and note it in a
   short summary written at session end.

This is a chore that runs **outside** the migration PR (the plans
folder is not in the repo). Run it as housekeeping after the migration
PR merges, so any newly-filed issues land on the populated Project.

**Exit:** the plans folder is meaningfully smaller (target: under 15
gorgon-related files; remainder are genuinely active or unfinished
work tracked in an issue).

### Phase 4 — Trim roadmap docs + delete migrated files *(~1 hour)*

For each of `celebrimbor-roadmap.md`, `smaug-roadmap.md`,
`mithril-reference-roadmap.md`:

1. Replace any backlog/checklist section with a one-line link to the
   relevant Project board:
   `> **Active backlog:** [Mithril.Reference roadmap]({url})`
2. Keep all design narrative — phasing rationale, decisions of record,
   "why we chose X over Y" sections.
3. Add a "## History" section at the bottom listing major milestones
   with PR numbers + dates (gleaned from `git log`).

For migrated wiki docs, **delete them from `docs/`** in this same
commit. Update any remaining `docs/` link that pointed at them to
point at the wiki URL instead. Run `git grep -l '.md)'` over `docs/`
to find stragglers.

Update the *project root* [`CLAUDE.md`](../../CLAUDE.md) with a new
"### Where does new content go?" subsection mirroring the wiki Home
page's table. Future agent sessions read CLAUDE.md first; encoding the
rule there prevents drift.

**Exit:** PR diff shows: deleted migrated docs, trimmed roadmaps with
Project links, updated CLAUDE.md.

### Phase 4.5 — Make the new layout discoverable to agents and humans *(~30 min)*

Beyond `CLAUDE.md` (Phase 4) and the wiki `Home.md` (Phase 1), wire
the four-tier rule into two more surfaces an agent or contributor
might consult first:

1. **Auto-memory standards entry** — write
   `C:\Users\arthu\.claude\projects\i--src-project-gorgon\memory\where_things_live.md`:

   ```markdown
   ---
   name: Where Mithril project knowledge lives
   description: Four-tier rule — Projects (live state) / Issues (tasks) / wiki (reference) / docs/ (design narrative)
   type: feedback
   ---

   When working in the Mithril (Project Gorgon) repo, route new
   content by tier:

   - **Pending unit of work** → GitHub Issue, attach to the
     `Mithril Roadmap` Project.
   - **Roadmap / prioritisation state** → the `Mithril Roadmap`
     Project's custom fields (`Status`, `Priority`, `Target Version`).
   - **Stable reference / process / user guides** → the wiki
     (`https://github.com/arthur-conde/project-gorgon/wiki`).
   - **Design rationale that co-evolves with code** → `docs/` in the
     code repo.
   - **Implementation spec for a follow-up agent** →
     `docs/agent-plans/`. Open with a `**Tracked in:** #NN` line.

   **Why:** confirmed during the docs/wiki/Projects migration on
   2026-04-30; encoding here so a fresh agent has the rule loaded
   before it touches a file.
   ```

   Add to `MEMORY.md` under "Standards & guidelines":

   ```
   - [Where Mithril project knowledge lives](where_things_live.md) — Projects/Issues/wiki/docs four-tier rule
   ```

2. **`README.md`** — add a "## Project knowledge map" section near
   the top (above any installation instructions):

   ```markdown
   ## Project knowledge map

   - **Roadmap & live status** → [Mithril Roadmap Project]({project_url})
   - **Open work / bugs** → [Issues](https://github.com/arthur-conde/project-gorgon/issues)
   - **Stable reference & user guides** → [Wiki](https://github.com/arthur-conde/project-gorgon/wiki)
   - **Design rationale & agent plans** → [`docs/`](docs/)
   ```

   Substitute the actual Project URL from Phase 2.

3. **Plan template convention** — extend `CLAUDE.md`'s "Where does
   new content go?" subsection with a one-line rule:

   > Every new `docs/agent-plans/*.md` opens with a `**Tracked in:** #NN`
   > line (or `_no issue yet_` if pre-issue). Spinning up an agent on
   > a plan gives them the issue context for free.

**Exit:** four discoverability surfaces are in sync (`CLAUDE.md`, wiki
`Home`, auto-memory `where_things_live.md`, repo `README.md`).
Smoke-test by starting a fresh Claude Code session and asking "where
do I file a new bug for the parser?" — the agent should cite Issues +
the `module:samwise` (or whichever) label without grepping.

### Phase 5 — Open the PR + close the agent plan *(~15 min)*

1. `gh pr create` with a body explaining the migration. Highlight the
   workflow rules so future contributors see them at review time.
2. Once merged, **delete this plan**
   ([`docs/agent-plans/docs-wiki-projects-split.md`](docs-wiki-projects-split.md))
   in a follow-up commit — the migration is one-shot, the plan is
   spent.

**Exit:** PR open, ready for review.

---

## Workflow rules to encode

After migration, future-Arthur and future-agents need to know where to
put new content. Encode these in `CLAUDE.md` and the wiki Home page:

1. **Backlog item → Issue first.** Don't add a checkbox to a roadmap
   doc. The doc holds *why*, the issue holds *what*.
2. **Design rationale → `docs/` or wiki.** If it's *why*, it's a doc.
3. **Issue references doc, doc doesn't list issues.** Each issue
   body links to the relevant `docs/` or wiki page for context. Docs
   link to *Projects* (which list the issues), not to individual
   issues, so docs don't rot when issues close.
4. **Anything load-bearing-but-unverified gets a "Verification owed"
   marker** in the design notebook. Filing an issue for the spot-check
   is the *task side*; the doc entry stays for context.

---

## Risks + things to watch

- **Wiki link rewrites are fragile.** Wikis link by page name; a
  typo'd link silently 404s. Do a final pass after Phase 1: clone the
  wiki fresh, click every link in `Home.md`. Tedious but quick.
- **`gh project` rate limits.** Creating 5 Projects + ~20 issues +
  ~20 item-add calls is ~50 API requests. Should be fine but pace if
  you hit a 429.
- **Existing `docs/` links from outside the repo.** I'm not aware of
  any external links into `docs/` files, but if Arthur or pg-data-mcp
  or anywhere else has linked into them, those break on Phase 4
  deletion. Arthur should confirm scope before merging.
- **Wiki has no PR review.** Edits go straight in. Be deliberate
  during Phase 1 — review your own work before pushing each batch.

---

## Pointers for the implementing agent

- **Branch policy** (per
  [memory/branch_policy_no_direct_commits.md](../../../../.claude/projects/i--src-project-gorgon/memory/branch_policy_no_direct_commits.md)):
  feature branch + `gh pr create`. Never push to `main`.
- **Commit identity** (per
  [memory/user_identity.md](../../../../.claude/projects/i--src-project-gorgon/memory/user_identity.md)):
  `Arthur Conde <arthur.conde@live.com>`. Don't commit as the alt.
- **Trust but verify the index table.** The "Lines" column was
  captured at plan-write time; doc sizes drift. Re-run `wc -l docs/*.md
  docs/user-guide/*.md` at session start.
- **Don't migrate `docs/agent-plans/`** to the wiki even though they're
  markdown. Agent plans co-evolve with the code that implements them
  and benefit from PR review.
- **Don't migrate `docs/mithril-reference-roadmap.md` content into
  Issues.** Phases 0–6 are *done*. The doc is historical record now
  + Phase 6 follow-ups list. Only the follow-ups become Issues.
- **The "Active backlog" link in each trimmed roadmap doc points to a
  Project, not a list of issues.** Projects support multiple views;
  hardcoding a filtered issue list URL works less well. Use the
  Project URL.
- **If Arthur changes his mind about the docs-vs-wiki split** during
  the session, only Phase 1's target list is affected — the rest of
  the plan is unchanged.

## Effort estimate

- Phase 0: 15 min
- Phase 1: 1 hour
- Phase 2: 30 min *(was 45 min — single Project, not five)*
- Phase 2.5: 30 min *(labels + issue templates)*
- Phase 3: 1.5 hours
- Phase 3.5: 30 min *(memory pointer rewrite)*
- Phase 3.6: 30 min *(reference-memory audit)*
- Phase 3.7: 1.5 hours *(`~/.claude/plans/` audit)*
- Phase 4: 1 hour
- Phase 4.5: 30 min *(discoverability surfaces)*
- Phase 5: 15 min
- Buffer for clarifying questions, rate-limit waits, link audit:
  30 min

**Total: ~8 hours.** Mostly mechanical once the open questions are
resolved. Phase 3.7 (`~/.claude/plans/` audit) is the most variable —
budget more if many plans need spelunking against `gh pr` history.
