# Module charters

> **Why this exists.** A module's *purpose* (one line in CLAUDE.md's table) doesn't
> prevent scope errors — its *boundaries* do. This doc records, per module, what it
> **owns**, what it **explicitly does not own and why**, and which reference data it is
> responsible for sourcing. The negative space is the load-bearing part.
>
> **The filter this enforces.** A data-availability gap is **not** a feature backlog.
> "Module X *could* read reference data Y" is only a gap if Y serves X's *Owns*. A
> "what can modules consume now" pass that ranks by data-reachability will manufacture
> non-gaps (it did: it proposed Gandalf adjudicate quest eligibility — a direct charter
> violation). Before proposing work for a module, check its charter: does it serve what
> the module owns? If not, it belongs to a different module or to no module.

## How to read confidence markers

Each **Does NOT own** entry is tagged:

- **✅ confirmed** — backed by the module owner, an existing design doc, or an
  established design decision. Treat as binding.
- **⚠️ inferred** — drafted from code behaviour by Claude, *not* owner-confirmed.
  A wrong charter is worse than none (it gets cited as authority), so these are
  **proposals pending sign-off**, not yet binding. Owner: confirm, correct, or delete.

`Owns` lines are grounded in current code and are lower-risk; `Reference data` lists
what the module legitimately consumes within its charter (not everything it *could*).

A **Does NOT own** entry is one of two kinds, and the distinction matters:

- **Hard (authority):** another module or the game engine is the source of truth;
  doing it here would duplicate/contradict authority. Example: Gandalf vs. quest
  eligibility. *Must not.*
- **Soft (empty):** on-charter-adjacent and not prohibited, but a deliberate
  non-feature — the underlying mechanic is trivial or the data doesn't exist, so there
  is nothing worth building. Example: Samwise vs. crop-selection advice. *Won't,
  because empty.* These are still binding decisions, just for a different reason.

---

## Cross-cutting ownership (confirmed)

Applies to *every* module; owner-confirmed 2026-05-16:

- **Recipe / crafting → Elrond or Celebrimbor.** Anything recipe- or crafting-related
  — items as crafting ingredients, crafting *use* of an item, craft planning, leveling
  *via* recipes — is owned by **Elrond** (leveling advice) or **Celebrimbor** (planning
  / execution), never by the module that happens to produce or hold the item. Cited by
  Samwise (crops), Pippin (food), and Bilbo (craftables) below.

---

## Samwise — garden tracker

- **Owns: ✅ confirmed (owner, 2026-05-16)** — Samwise is *first and foremost a garden
  tracker*. It interprets `Player.log` to track what the player has planted, tracks
  crop state transitions, and raises alarms **so plants don't die**. The authority on
  *what is planted* and *where each plot is in its lifecycle*, derived from the log.
  Alarms are loss-prevention, not merely a ripeness/harvest-ready notification.
  (See `samwise-parser` memory: identification trade-offs, alias-learning convergence.)
- **Does NOT own:**
  - **✅ confirmed (owner, 2026-05-16) — soft/empty** — *Crop-selection / yield
    optimization.* Not prohibited (Samwise *could* suggest what to plant) but a
    deliberate non-feature: PG gardening is intentionally trivial (plant → water when
    thirsty → fertilize when hungry → collect when ready), so there is nothing to
    optimize.
  - **✅ confirmed (owner, 2026-05-16)** — *Anything recipe/crafting* (crops as
    ingredients, cooking with crops, **and Gardening crafting recipes** — see data
    note) → Elrond/Celebrimbor, per the cross-cutting rule.
- **Reference data:** `Items` (seed→crop identity via `ItemsByInternalName`).
- **Data note (verified v470, 2026-05-16; refined by owner):** Gardening's **primary**
  XP source is the planting loop (plant → water → fertilize → harvest) — owner-confirmed
  it grants XP. A **secondary** source is a handful of Gardening *crafting* recipes
  (`BasicFertilizer1/2/3`, …), which are in `recipes.json` with full
  `RewardSkillXp`/first-time/drop-off and so fall to Elrond via the cross-cutting rule
  like any craft skill. Structural consequence: Elrond is recipe-anchored, so it sees
  *only the secondary slice* — Gardening's primary progression sits outside both
  Samwise's charter (tracker, not advisor) and Elrond's (recipe-anchored). **Verification
  owed:** the planting-loop per-action XP values do not appear to be in reference data
  (owner's observation; not exhaustively checked). If confirmed absent, no data-feasible
  XP-driven gardening advisor is possible for the primary source — which is the
  data-level reason crop-selection optimization stays a soft non-feature, not a
  temporary gap.

## Pippin — Gourmand support (food-variety + provenance)

- **Owns: ✅ confirmed (owner, 2026-05-16)** — supplementing PG's **Gourmand** skill,
  which levels from eating foods *not previously eaten*. Pippin tracks the per-character
  set of foods already eaten against the food catalog and surfaces the not-yet-eaten
  foods so the player can progress Gourmand. **Food provenance is in scope** — *where*
  a not-yet-eaten food can be obtained is part of Pippin's responsibility. (This is
  food-specific provenance in service of Gourmand; it does not collide with
  Silmarillion's *generic* reference browsing — different surface, different purpose.)
  About food *novelty/variety*, not buffs.
- **Does NOT own:**
  - **✅ confirmed (owner, 2026-05-16)** — *Food buffs.* Pippin tracks nothing about
    buff effects or buff uptime. Gourmand novelty only.
  - **✅ confirmed** — *Crafting food itself* → Celebrimbor, per the cross-cutting
    recipe/crafting rule. (Pippin may *point at* where a food comes from, including
    "it's crafted"; it does not own the crafting.)
- **Primary data source: ✅ owner-stated (2026-05-16)** — the **in-game reporting
  tool**'s character report (the eaten-food record). That report is *dumped into*
  `Player.log` — where Pippin reads it today — and is *also* written as a standalone
  raw text file that Pippin does **not** currently consume (a potentially cleaner
  input). Not the CDN. (An earlier draft's flat "*not* `Player.log`" was wrong —
  corrected: the report content lands in `Player.log`.)
- **Reference data:** CDN `Items` (`FoodDesc`) for the food catalog/identity — used to
  compute not-yet-eaten and to resolve where un-eaten foods come from.
- **Endorsed opportunity → Tracked: #348** — combine the two: "what haven't I eaten
  *and where do I find it*." Owner-endorsed (2026-05-16) as good value; filed as #348.
  (Charter carries the stable pointer; the issue holds the spec.)

## Legolas — surveying & route optimization

- **Owns: ✅ confirmed (owner, 2026-05-16)** — the survey FSM, position-anchor/projection,
  survey-run route optimization, and the map overlay. (See `legolas-overview` doc +
  `legolas_position_anchor_constraint` memory: surveying produces inventory items;
  movement invalidates the projector.)
- **Does NOT own:**
  - **✅ confirmed (owner, 2026-05-16)** — *General geographic reference* (where
    places/landmarks are). Areas/landmarks browsing is Silmarillion.
  - **✅ confirmed (owner, 2026-05-16)** — *Item acquisition guidance.*
- **Reference data:** `Items` (survey items).

## Arwen — NPC favor & gift tracking

- **Owns: ✅ confirmed (owner, 2026-05-16)** — per-NPC favor state, gift-rate
  calibration, and gift-outcome tracking.
- **Does NOT own:**
  - **✅ confirmed (owner, 2026-05-16)** — *NPC location/services browsing.* The NPC
    reference card is Silmarillion.
  - **✅ confirmed (owner, 2026-05-16)** — *Quest-giving / favor-quest adjudication.*
- **Reference data:** `Npcs` (preferences), `Items` (gift identity).

## Elrond — skill leveling advisor

- **Owns:** per-recipe leveling math (effective XP, first-time bonuses, drop-off),
  the optimal grind path *within a skill*, and the cookbook view.
- **Does NOT own:**
  - **✅ confirmed** — *Non-recipe skills.* Recipe-anchored by design: skills without
    recipes (combat/gathering/etc.) intentionally never appear; Elrond cannot advise
    on them. (Design doc + `elrond_recipe_anchored_design` memory.)
  - **✅ confirmed (by entailment, 2026-05-16)** — *Multi-recipe shopping / inventory.*
    Celebrimbor's Owns is owner-confirmed as exactly this; Elrond not owning it is the
    direct complement.
  - ⚠️ *The skill-XP math itself, post-#225.* Slated to lift into shared
    `Mithril.Leveling`; after #225 Elrond consumes that engine rather than owning it.
- **Reference data:** `Recipes`, `Skills`, `XpTables`.

## Gandalf — timers & repeatable-quest cooldowns

- **Owns: time.** When you completed a repeatable, when it is re-attemptable, and
  user-defined timers/alarms. The authority on *temporal bookkeeping* only.
- **Does NOT own:**
  - **✅ confirmed (owner, 2026-05-16)** — *Quest eligibility / "can you do this now".*
    The game engine is the source of truth for requirements; Gandalf never adjudicates
    them from `Quest.Requirements`. Recomputing eligibility would duplicate authority it
    deliberately disclaims and risk disagreeing with the engine. This is the canonical
    example of why charters exist.
- **Reference data:** `Quests` (reuse-time fields for cooldown anchoring only),
  `Strings`/`Areas` (display resolution).

## Bilbo — storage/inventory management

- **Owns:** parsing storage/inventory exports, inventory query, and location chips.
- **Does NOT own:**
  - **✅ confirmed** — *Craft planning* (multi-recipe shopping lists) → Celebrimbor,
    per the cross-cutting recipe/crafting rule.
  - **✅ confirmed** — *Leveling advice* → Elrond, per the cross-cutting rule
    (leveling via recipes is Elrond's).
- **Reference data:** `Items`, `Recipes`, keyword index (craftable-from-on-hand).
  *(The inventory-query surface is a candidate for shared extraction — Celebrimbor §1
  `IInventoryQueryService`; Bilbo would consume the shared service.)*

## Silmarillion — reference-data browser

- **Owns: ✅ confirmed** — browsing and cross-linking reference data (master-detail,
  navigator, provenance popups). Field-level scope is governed by
  [silmarillion-field-coverage.md](silmarillion-field-coverage.md); tab scope by
  [silmarillion-roadmap.md](silmarillion-roadmap.md).
- **Does NOT own:**
  - **✅ confirmed** — *Computation/simulation.* It is a browser, not a calculator.
    Calculators are Elrond/Celebrimbor; per the roadmap, TSys/power calc is explicitly
    Celebrimbor's territory, "not a Silmarillion tab."
    **Carve-out (owner-clarified 2026-05-16):** *displaying* parsed treasure-effect
    previews via the shared `ResultEffectsParser` is browsing, not calculation — it is
    within Silmarillion's charter and is exactly the intent of #214. "Does not
    calculate" ≠ "cannot render parser output." Silmarillion not showing effects today
    is a gap, not a prohibition.
- **Reference data:** effectively all sources (it is the browser), per the bucketing
  rule in the roadmap.

## Celebrimbor — crafting planner

- **Owns: ✅ confirmed (owner, 2026-05-16)** — build a shopping list and craft a given
  set of targets, accounting for what is on hand. Scope is *exactly* "what is needed to
  make N units of X": multi-recipe selection, shopping-list aggregation, on-hand
  cross-reference, the craft-step ladder. **It accounts for no logic beyond the
  quantity math** — no optimization, no strategy, no decision about *what* or *how
  much* to make. (See [celebrimbor-roadmap.md](celebrimbor-roadmap.md).)
- **Does NOT own:**
  - **✅ confirmed (owner, 2026-05-16)** — *Any logic beyond "make N of X".* Leveling
    optimization, what-to-craft strategy, ROI — all out. Celebrimbor only ever
    *consumes* a target list; whatever decides that list (Elrond / the #227 cross-skill
    planner) is upstream. #228's "plan-aware craft list" means Celebrimbor *receives* a
    computed plan as targets — it does not compute it.
  - **✅ confirmed (owner, 2026-05-16)** — *Generic reference browsing / cross-link
    navigation* → Silmarillion. **Recipe *display* deliberately overlaps** — both
    modules show a recipe — differentiated by *purpose*: Silmarillion browses it;
    Celebrimbor shows it in service of a craft plan and additionally previews its
    treasure (ResultEffects) outcomes ("what you might get"). The effect-preview
    capability is **shared infra** (`Mithril.Shared.ResultEffectsParser`), owned by
    neither; Silmarillion just does not consume it yet — tracked as #214, a *gap, not
    a boundary*.
- **Reference data:** `Recipes`, `Items`, `ResultEffectsParser` previews, `Areas`
  (source resolution), inventory.

---

## Cross-module shared infra (charter-adjacent)

Some responsibilities are deliberately being lifted *out* of modules into shared
libraries; the charter follows the code:

- **`ResultEffectsParser` (Mithril.Shared) — shipped, shared.** Parses recipe
  treasure-effect strings into typed previews. Owned by *neither* recipe-displaying
  module: consumed by Celebrimbor today, by Silmarillion per #214. The canonical
  "shared infra, not module turf" case — effect *display* is appropriate in any
  recipe surface; the parser is the single source of truth both lean on.
- **`Mithril.Leveling` (#225)** — skill-XP math, lifted from Elrond. Future owner of
  the math both Elrond and the #227 planner consume.
- **Shared demand-driven recipe expander (#226, supersedes #121)** — generalises
  Celebrimbor's aggregator; future shared consumer set = Bilbo + Elrond + #227 planner.
- **`PrereqRecipe` resolver (latent, unfiled)** — three surfaces want the same
  prereq-chain modelling: #341 (Silmarillion *displays*), Celebrimbor §2 (*visualises*),
  #227 (*gates on*). If the planner work lands first, downstream surfaces should consume
  its resolver rather than re-derive.

## History

- **2026-05-16** — Doc created. `Owns` grounded in current code; ✅ entries are
  owner/design-doc-confirmed (Gandalf eligibility confirmed by owner this date; Elrond
  recipe-anchoring and Silmarillion browser-not-calculator from existing design docs).
  All ⚠️ entries are unconfirmed Claude drafts pending owner sign-off.
- **2026-05-16** — Samwise charter confirmed by owner: first and foremost a garden
  tracker — interprets `Player.log` to track plantings + state transitions and alarms
  so plants don't die (loss-prevention, not just ripeness). Owns promoted to ✅
  confirmed.
- **2026-05-16** — Celebrimbor↔Silmarillion ⚠️ resolved by owner (the last genuine
  open charter ruling): recipe *display* deliberately overlaps — both show recipes,
  differentiated by purpose — and Celebrimbor additionally previews treasure effects
  while Silmarillion currently does not (#214: gap, not boundary). Added a carve-out
  to Silmarillion's "no computation" line so #214 can't be misread as a charter
  violation; listed `ResultEffectsParser` as shipped shared infra owned by neither.
- **2026-05-16** — Pippin data source corrected: the in-game report is *dumped into*
  `Player.log` (Pippin reads it there today) and *also* exists as an un-consumed
  standalone raw text file — the prior flat "not `Player.log`" was wrong. Endorsed
  food-provenance opportunity filed as **#348**; charter now carries the pointer
  instead of "untracked".
- **2026-05-16** — Pippin corrected again by owner: **food provenance IS in scope**
  — the prior ⚠️ "→ Silmarillion" was a wrong inference, removed. Primary data source
  clarified as the in-game reporting tool's character report (not `Player.log`/CDN; the
  earlier "from the log" Owns wording was also wrong). Owner endorsed an integrated
  "what haven't I eaten + where to find it" feature — recorded as an untracked
  opportunity (charter doesn't list issues; filing offered separately).
- **2026-05-16** — Celebrimbor charter confirmed & tightened by owner: scope is exactly
  "what is needed to make N units of X" + on-hand, no logic beyond the quantity math.
  Owns + the no-extra-logic boundary → ✅; Elrond's reciprocal "multi-recipe shopping →
  Celebrimbor" → ✅ by entailment. Celebrimbor↔Silmarillion left ⚠️ (strongly implied,
  not explicitly ruled).
- **2026-05-16** — Samwise gardening note refined by owner: planting loop is the
  *primary* Gardening XP source (grants XP — confirmed); recipes are secondary and in
  ref data. Recorded that Elrond (recipe-anchored) sees only the secondary slice;
  primary-loop XP presence in ref data remains Verification owed.
- **2026-05-16** — Legolas & Arwen sections confirmed accurate by owner; their ⚠️
  entries promoted to ✅.
- **2026-05-16** — Samwise gardening-XP claim corrected after a v470 data check: the
  asserted "gardening XP not in reference data" was **false** — `Gardening` is a skill
  and `BasicFertilizer1/2/3` recipes carry full Gardening XP, so recipe-based Gardening
  leveling is Elrond's via the cross-cutting rule. Crop-*lifecycle* XP availability
  recorded as explicit Verification owed. Both the prior draft and the owner's
  assumption were off; checking the data resolved it.
- **2026-05-16** — Samwise boundaries refined by owner: crop-selection/yield advice is
  a *soft* non-feature (PG gardening trivial; gardening XP absent from ref data), not a
  prohibition — promoted to ✅. Recipe/crafting confirmed as Elrond/Celebrimbor
  territory and lifted into a new "Cross-cutting ownership (confirmed)" section
  (recurs for Pippin/Bilbo). Added the hard-vs-soft does-not-own distinction to the
  reading guide.
- **2026-05-16** — Pippin charter corrected by owner: it supplements the **Gourmand**
  skill (food-novelty tracking — foods *not yet eaten*), not buff/consumption tracking.
  Owns + the no-buffs boundary promoted to ✅ confirmed. The earlier draft's
  "recommending what to eat" non-ownership was deleted — it contradicted the real
  charter (surfacing un-eaten foods *is* Pippin's job).
