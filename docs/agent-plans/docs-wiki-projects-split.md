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
   "Mithril" Project with a `Module` field? Working assumption:
   per-module Projects + a top-level "Mithril Release Planning" board.
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

### Phase 2 — Set up Projects + custom fields *(~45 min)*

For each of: **Mithril.Reference**, **Celebrimbor**, **Smaug**,
**Gandalf**, plus a top-level **Mithril Release Planning**:

```bash
gh project create --owner arthur-conde --title "Mithril.Reference roadmap"
# repeat for the other modules
```

For each Project, add custom fields via `gh project field-create`:

- `Status` (single-select): Todo / In Progress / Blocked / Done
- `Priority` (single-select): P0 / P1 / P2 / P3
- `Effort` (single-select): XS / S / M / L
- `Target Version` (single-select): v1.0 / v1.1 / v1.2 / v2 / Backlog
- `Module` (single-select): Samwise / Pippin / Legolas / Gandalf /
  Elrond / Bilbo / Arwen / Saruman / Smaug / Celebrimbor / Palantir /
  Mithril.Reference / Shell

(`Module` only useful on the cross-cutting Release Planning board, but
applying it everywhere keeps the schema uniform and the cross-board
view filterable.)

**Note:** `gh project field-create` syntax for single-select options
needs a single-line list. Use `--single-select-options "Todo,In
Progress,Blocked,Done"` etc.

**Exit:** five Projects exist with consistent custom fields.

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
- Phase 2: 45 min
- Phase 3: 1.5 hours
- Phase 4: 1 hour
- Phase 5: 15 min
- Buffer for clarifying questions, rate-limit waits, link audit:
  30 min

**Total: ~5 hours.** A long session, but mostly mechanical once the
open questions are resolved.
