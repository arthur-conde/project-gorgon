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

---

## Samwise — garden tracker

- **Owns: ✅ confirmed (owner, 2026-05-16)** — Samwise is *first and foremost a garden
  tracker*. It interprets `Player.log` to track what the player has planted, tracks
  crop state transitions, and raises alarms **so plants don't die**. The authority on
  *what is planted* and *where each plot is in its lifecycle*, derived from the log.
  Alarms are loss-prevention, not merely a ripeness/harvest-ready notification.
  (See `samwise-parser` memory: identification trade-offs, alias-learning convergence.)
- **Does NOT own:**
  - ⚠️ *What to plant / crop economics / yield optimization.* It tracks the garden; it
    does not advise on it. (The "first and foremost a *tracker*" framing leans this
    way, but the exact boundary is not owner-confirmed.)
  - ⚠️ *Cooking/crafting use of crops.* Crop-as-ingredient is Celebrimbor/Pippin.
- **Reference data:** `Items` (seed→crop identity via `ItemsByInternalName`).

## Pippin — Gourmand support (food-variety tracking)

- **Owns: ✅ confirmed (owner, 2026-05-16)** — supplementing PG's **Gourmand** skill,
  which levels from eating foods *not previously eaten*. Pippin tracks the per-character
  set of foods already eaten (from the log) against the food catalog and surfaces the
  not-yet-eaten foods so the player can progress Gourmand. It is about food
  *novelty/variety*, not buffs.
- **Does NOT own:**
  - **✅ confirmed (owner, 2026-05-16)** — *Food buffs.* Pippin tracks nothing about
    buff effects or buff uptime. Gourmand novelty only.
  - ⚠️ *Crafting food.* That's Celebrimbor.
  - ⚠️ *Food provenance browsing* (where a food comes from). That's Silmarillion.
- **Reference data:** `Items` (food catalog / `FoodDesc` to enumerate the universe of
  foods and identify which items are food).

## Legolas — surveying & route optimization

- **Owns:** the survey FSM, position-anchor/projection, survey-run route optimization,
  and the map overlay. (See `legolas-overview` doc + `legolas_position_anchor_constraint`
  memory: surveying produces inventory items; movement invalidates the projector.)
- **Does NOT own:**
  - ⚠️ *General geographic reference* (where places/landmarks are). Areas/landmarks
    browsing is Silmarillion.
  - ⚠️ *Item acquisition guidance.*
- **Reference data:** `Items` (survey items).

## Arwen — NPC favor & gift tracking

- **Owns:** per-NPC favor state, gift-rate calibration, and gift-outcome tracking.
- **Does NOT own:**
  - ⚠️ *NPC location/services browsing.* The NPC reference card is Silmarillion.
  - ⚠️ *Quest-giving / favor-quest adjudication.*
- **Reference data:** `Npcs` (preferences), `Items` (gift identity).

## Elrond — skill leveling advisor

- **Owns:** per-recipe leveling math (effective XP, first-time bonuses, drop-off),
  the optimal grind path *within a skill*, and the cookbook view.
- **Does NOT own:**
  - **✅ confirmed** — *Non-recipe skills.* Recipe-anchored by design: skills without
    recipes (combat/gathering/etc.) intentionally never appear; Elrond cannot advise
    on them. (Design doc + `elrond_recipe_anchored_design` memory.)
  - ⚠️ *Multi-recipe shopping / inventory.* That's Celebrimbor.
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
  - ⚠️ *Craft planning* (multi-recipe shopping lists). That's Celebrimbor.
  - ⚠️ *Leveling advice.* That's Elrond.
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
- **Reference data:** effectively all sources (it is the browser), per the bucketing
  rule in the roadmap.

## Celebrimbor — crafting planner

- **Owns:** multi-recipe selection, shopping-list aggregation, on-hand cross-reference,
  and the craft-step ladder. (See [celebrimbor-roadmap.md](celebrimbor-roadmap.md).)
- **Does NOT own:**
  - ⚠️ *Leveling optimization.* The cross-skill planner (#227) is the Elrond/Celebrimbor
    convergence; Celebrimbor *executes* a plan (#228), it does not compute the leveling
    strategy.
  - ⚠️ *Reference browsing.* That's Silmarillion.
- **Reference data:** `Recipes`, `Items`, `ResultEffectsParser` previews, `Areas`
  (source resolution), inventory.

---

## Cross-module shared infra (charter-adjacent)

Some responsibilities are deliberately being lifted *out* of modules into shared
libraries; the charter follows the code:

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
  confirmed; ⚠️ tracker-not-advisor boundary left inferred (framing leans that way,
  not explicitly ruled on).
- **2026-05-16** — Pippin charter corrected by owner: it supplements the **Gourmand**
  skill (food-novelty tracking — foods *not yet eaten*), not buff/consumption tracking.
  Owns + the no-buffs boundary promoted to ✅ confirmed. The earlier draft's
  "recommending what to eat" non-ownership was deleted — it contradicted the real
  charter (surfacing un-eaten foods *is* Pippin's job).
