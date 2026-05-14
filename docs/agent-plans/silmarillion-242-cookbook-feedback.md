# Silmarillion · Tab Cookbook — Feedback from the Quests Tab Shipment (#242)

Notes from shipping the second Bucket B tab against the cookbook + #241 feedback. Same scaffold as [silmarillion-tab-cookbook-feedback.md](../silmarillion-tab-cookbook-feedback.md) — items ordered by how much friction they caused, not by where they belong in the cookbook.

## 1. Handoff cross-reference claims need a data-shape sanity check

**Friction:** The handoff suggested folding `directedgoals.json` into the Quests tab as an `IsGuidedObjective` filter chip ("guided objectives" / "stuff-to-do pane"). The premise was that directedgoal entries would reference quest InternalNames so quest rows could be tagged with guided-objective membership. The actual `directedgoals.json` shape carries `Id` / `Label` / `LargeHint` / `SmallHint` / `Zone` / `CategoryGateId` — no quest InternalName references anywhere. The "Grow a potato" / "Meet Nightshade" entries relate to quests **thematically**, not structurally; there's no key to join on.

The filter facet was dropped from scope mid-execution after grepping the actual JSON, but the handoff text presented the join as if it had structural backing.

**Cookbook addition:** when a handoff proposes a filter facet or cross-link sourced from data file A indexed by data file B's entity keys, **the handoff drafter should verify A actually contains those keys before recommending the facet**. Even a one-line `grep` against the bundled JSON would have caught this. A future "Areas tab" handoff that proposes "quests filtered by Area" needs to confirm `Quest.DisplayedLocation` is an area-key match, not a freeform string. Same with any item-keyword filter sourced from another file.

Action item for handoffs: each proposed facet should cite the **exact field on the join file** that contains the foreign key, by name.

## 2. POCO-migration sizing should look at field reads, not consumer file count

**Friction:** The handoff recommended path 2 (full migration of `QuestEntry` to `Mithril.Reference.Models.Quests.Quest`) and estimated the blast radius as "likely only `ResolveSourceContext` in `ReferenceDataService` and a handful of tests". The actual `QuestEntry` consumer surface was ~12 files: Gandalf's `QuestSource` + `QuestCatalogPayload`, GameState's `QuestService` + 2 parsers, the just-shipped `NpcsTabViewModel`, plus ~10 test fakes across modules.

My first read of that consumer list pushed me toward path 1 (the lower-blast-radius path the cookbook defaults to). The user correctly identified that the blast-radius **file count** wasn't load-bearing — each consumer reads a tight subset of fields (`InternalName`, `Name`, `Reuse*`, `DisplayedLocation`, `FavorNpc`) that are *all available on the POCO*. The migration was mechanical: type swap, one field rename (`ReuseDays` → `ReuseTime_Days`), null guards for nullable POCO fields.

**Cookbook addition:** in the *"What your handoff still owns"* section's slim-projection-vs-POCO subsection, add: *"When sizing path 2, don't stop at the consumer file count. Read what each consumer actually reads from the slim type. If they all read narrow subsets that also exist on the POCO, the migration is mostly a type-swap and one field rename per divergent name. The slim type's projected-only fields (e.g. `QuestEntry.SkillRewards`, which flattens polymorphic `Rewards.SkillXp`) are where the real migration cost lives — and absence of consumers for those fields is the signal that path 2 is cheap."*

## 3. Stale subclass counts in the handoff caused incomplete grouping

**Friction:** The handoff said "25 typed `QuestRequirement` subclasses + 9 typed `QuestReward` subclasses". The actual count was **42 QuestRequirement subclasses** (+ `UnknownQuestRequirement` sentinel). Six newer subclasses — `InCombatRequirement`, `InCombatWithEliteRequirement`, `OtherHasTypeTagRequirement`, `AbilityKnownRequirement`, `PetCountRequirement`, `EntityPhysicalStateRequirement` — weren't placed in any group in the handoff's grouping recommendation. The reward count of 9 was correct.

I caught the count delta by reading the POCO file directly during my pre-implementation review, but a future agent might have copy-pasted the handoff's grouping and silently shipped six subclasses unhandled (they'd fall through to the projector's default branch, which renders the raw `T` value as `"Unhandled: T_value"`).

**Cookbook addition:** when a handoff cites subclass counts for polymorphic kinds, the counts should reference the POCO file as the source of truth (with a path) so the executing agent can verify in a single read. Better: an automated assertion — perhaps a test fixture that asserts the projector's switch arms match the count of concrete subclasses for each polymorphic kind, so adding a new subclass to the model surfaces as a test failure.

For #242 the projector ended up with 42 explicit cases (one per concrete subclass) plus a fallback that renders `"Unhandled: T_value"` for forward compatibility. That fallback is the right safety net; the test fixture would tell future agents *they need to wire up a case for the new subclass*.

## 4. Polymorphic-rendering "bucket by intent, not by class" pays off

**Friction (positive):** The cookbook's *Polymorphic-rendering warning* (carried forward from #241) was load-bearing for the Quests tab. 42 QuestRequirement subclasses collapsed to 8 player-facing groups (Story prerequisites / Skill gates / Favor / Identity / Inventory / Time & moon / Location & area / Combat state / Pets / Composite / Internal flags / Unrecognised drift). Each group renders as a labelled bordered card; rows inside a group share semantics. This shape reads cleanly even for quests with 6+ requirements across 4 buckets.

**Cookbook addition:** the rule is right; just add a concrete example with 4+ buckets to anchor what "by intent" looks like for a future agent (`QuestDetailProjector.ClassifyRequirement` switch in [`src/Silmarillion.Module/ViewModels/QuestDetailProjector.cs`](../../src/Silmarillion.Module/ViewModels/QuestDetailProjector.cs)). Pointing at a worked example is more useful than the abstract rule.

## 5. Visual smoke caught five issues synthetic tests missed (cookbook rung 4 is load-bearing)

The synthetic projector tests passed every assertion before the user's visual smoke pass. Real-data smoke caught:

1. **NPC slug-form references didn't resolve.** Quest fields reference NPCs as `AreaSerbule2/NPC_DurstinTallow`; `npcs.json` keys them bare (`NPC_DurstinTallow`). Resolver lookup missed; chip rendered the raw slug. Fix: `EntityRef.Npc` factory now strips the area prefix at construction so every downstream consumer (resolver, kind target, navigator history) sees the canonical bare form.

2. **Inline `<i>...</i>` / `<b>...</b>` markup rendered literally.** Quest descriptions use this as speaker prefixes (`<i>Zhia Lian:</i> ...`); 880 italic + 124 bold pairs across the catalogue. Fix: new `FormattedText` attached property in `Mithril.Shared.Wpf` parses the two-tag subset into `Inline` runs.

3. **Right-aligned arrow buttons for navigable rows were a poor affordance.** My first pass put a 14px `→` button at the row's far right via `DockPanel.Dock="Right"`. The user surfaced this as "did you introduce this instead of chips?" — correctly diagnosed as bad UX. Fix: `QuestRequirementDisplay` gained `Prefix` + `ChipName` optional fields; navigable rows now render as `Prefix [EntityChip]`.

4. **Repeatability buried in the footer.** Was right-aligned italic small text below the chips, easy to miss. Daily-vs-one-shot is gameplay-critical and wants prominent display. Fix: promoted to a gold-tinted header chip alongside Level/Location.

5. **Description buried at the bottom.** Player-facing summary lived after every gameplay section. Fix: moved between header and Giver/Turn-in chips; NPC dialogue (preface/midway/success) tucked into a collapsed Expander labelled "Quest dialogue".

**Cookbook addition:** rung 4 already mandates the real-data sanity walk. The five issues above are five distinct *categories* the walk catches that synthetic tests don't. Worth surfacing the categories:

- Cross-file foreign-key form mismatches (slug ↔ bare envelope key)
- In-data markup the rendering layer doesn't parse
- Affordance discoverability (size, position, visual weight)
- Information hierarchy (what should be in the header vs the body vs the footer)
- Section ordering (does the layout match the player's read order?)

Synthetic tests answer "does the projection produce the right data?". Smoke answers "is the rendering legible?". Both are required.

## 6. Inline-markup parser should be the canonical helper, not per-tab

**Friction:** PG's `<i>...</i>` / `<b>...</b>` markup appears in:

- Quest `Description`, `PrefaceText`, `MidwayText`, `SuccessText` (this PR's focus)
- `Item.Description` and `Item.FoodDesc` (likely)
- `Recipe.Description` (likely)
- Lorebook bodies (likely)
- Ability descriptions (likely)

I built `Mithril.Shared.Wpf.FormattedText` as an attached property: `<TextBlock c:FormattedText.Text="{Binding Description}"/>`. It's a tiny parser (~100 lines) and handles nesting + unbalanced tags safely. Currently only used on quest detail.

**Cookbook addition:** the cookbook should mention this helper exists and recommend its use anywhere quest/item/recipe/lorebook long-form text is displayed. Items v1 and Recipes v1 shipped before this helper existed and may currently show literal `<i>` tags too — a follow-up audit grep for plain `Text="{Binding Description}"` bindings would find them.

## 7. EntityRef factories should normalise tail-component refs

**Friction:** Quest fields (`Quest.QuestNpc`, `Quest.FavorNpc`, `Quest.MainNpcName`) reference NPCs as `AreaX/NPC_Y` slugs while `npcs.json` keys them bare. Same NPC, two reference forms. Without normalisation:

- Resolver lookup fails (no `NpcsByInternalName["AreaX/NPC_Y"]`)
- Kind target's `TrySelectByInternalName` fails (master list is keyed on bare envelope keys)
- Navigator history doesn't dedup slug vs bare references

I fixed this in `EntityRef.Npc(string internalName)` — strip everything before the last `/`. Confirmed no npcs.json envelope key contains a slash so the strip is unambiguous.

**Cookbook addition:** in the *Cross-link chips* section, add a gotcha: *"Before constructing an `EntityRef.<Kind>(input)`, check whether `input` comes from a data file whose reference form matches that kind's primary envelope-key form. If they diverge (e.g. quest-data references NPCs as `AreaX/NpcKey` slugs while npcs.json keys them bare), the factory should normalise the input. Add the strip inside the factory, not at every call site — there will always be a new call site you forget."*

Possible future surfaces: Effect references may use a similar slug form; Area references in quests already use a prefixed key. Worth grepping the existing factories when shipping a new tab.

## 8. Display-name resolution with area annotation

**Friction:** Once the slug-strip fix went in, "Friends with Joeh" was unambiguous in test data but unsatisfying in real data — multiple NPCs share first names (there are several NPCs named "Marna", several named "Joe" / "Joeh"). User's directive: *"If we have the area data, let's include it. Fallback to just NPC name."*

I added `QuestDetailProjector.ResolveNpcDisplayWithArea` — looks up the resolved NPC POCO and appends `AreaFriendlyName` in parentheses when present. Used in favor-requirement lines, giver/turn-in/favor chips, favor-reward line.

**Cookbook addition:** when surfacing an NPC name in disambiguation contexts (anywhere multiple NPCs could share a name), annotate with `AreaFriendlyName`. The pattern generalises: items don't have this collision concern (InternalName is unique), but **any kind with non-unique display names benefits from a disambiguator field on the POCO** (NPCs have area; quests have `DisplayedLocation`; recipes have skill+level). The cookbook could surface this as a *"Disambiguator when display names collide"* guideline.

## 9. The right-aligned-arrow anti-pattern — a "common pitfalls" entry

**Friction:** My first instinct for "this row references a navigable entity" was a `Button` with `→` glyph at `DockPanel.Dock="Right"`. It compiled, passed visibility tests (button shows when `IsNavigable=true`), and synthetic tests didn't object. The visual smoke immediately caught it as a poor affordance — too small, too far from the entity, the wrong visual weight for "this is clickable".

The cookbook's *Cross-link chips* section names `EntityChip` and `ItemSourceChip` as the canonical patterns, but doesn't explicitly call out the anti-pattern of "row of text + sidecar button" as a *non-affordance*. A future agent could repeat my mistake.

**Cookbook addition:** add a *"Pitfalls"* sub-section under *Cross-link chips* with at least these entries:

- ❌ Plain `TextBlock` with a small `Button` for navigation. Sidecar buttons aren't read as navigation affordances by players; chips are.
- ❌ Hyperlink-style underlined text inside a `Run`. Same problem — affordance is too subtle, conflicts with WPF's default `Hyperlink` rendering on some themes.
- ✅ `EntityChip` for self-contained entity references. The chip *is* the affordance.
- ✅ `EntityChip` + label prefix when the row needs context (`"Completed:" + [chip]`). Carries the prefix as a `TextBlock` next to the chip, not wrapping the chip in a button.

## 10. Repeatability prominence — header chip, not footer line

**Friction:** I initially put repeatability ("Repeatable every 20 hours" / "One-time quest") in the footer above the InternalName, right-aligned italic. It's small, easy to miss, and competes for attention with the mono-styled internal name. Daily-vs-one-shot distinction is *the* first piece of info a player wants when scanning a quest.

Fixed: promoted to a gold-tinted badge chip in the header row alongside Level and Location. Short-form: "Daily" (20–24h), "Weekly" (7d exact), "Every Xd Yh Zm" for arbitrary cadences. One-time quests get **no** chip — most quests are one-shot, so the chip's presence carries the "this is a daily" signal economically.

**Cookbook addition:** the cookbook's *Detail-pane reading order* convention puts metadata trailing (description, internal name footer). Cadence-style metadata is an exception — when the metadata changes the *gameplay loop* (daily vs one-shot, cooldown duration), it belongs in the header. The "default off" convention (don't render the chip for the common case) is a nice noise-reduction pattern; could be generalised in the cookbook as *"Don't show chips for the universal default"* (mirror of #241 feedback item 6 on `Favor: Despised`).

## 11. SilmarillionViewModel constructor changes still ripple to navigator tests

**Friction:** Adding `QuestsTabViewModel quests` to the `SilmarillionViewModel` constructor broke `SilmarillionViewModelTests` (2 sites) and `SilmarillionReferenceNavigatorTests` (5 sites — all named-argument calls of the form `new SilmarillionViewModel(items: null!, recipes: null!, npcs: null!, nav, targets)`).

This is verbatim the same friction #241 feedback item 8 called out. The cookbook addition suggested by that feedback hasn't materialised (I assume because the cookbook hasn't been updated since #241). **Re-confirming the pattern is real and worth surfacing.**

Mitigation that would have saved time: have `SilmarillionViewModel` accept `IEnumerable<ITabViewModel>` (with each tab VM implementing a marker interface) so adding a tab doesn't change the constructor signature. The Tabs array would be built from the enumerable. This is a bigger change than a cookbook addition can capture but is a real architectural improvement.

## 12. XamlResourceLint caught a stale-StaticResource bug for me

**Friction (positive):** I registered `RequirementChipVmConverter` in `src/Silmarillion.Module/Views/Resources.xaml` and used `{StaticResource RequirementChipVmConverter}` from `QuestDetailView.xaml`. The lint flagged it: *"`{StaticResource RequirementChipVmConverter}` resolves only from src/Silmarillion.Module/Views/Resources.xaml, but this view does not merge that dictionary at UserControl scope."* — exactly correct and pointed at the canonical fix pattern (`src/Samwise.Module/Views/GardenView.xaml`).

This is the kind of issue that would have manifested at runtime (XAML parse error when opening the detail pane) and the lint caught it pre-merge.

**Cookbook addition:** the cookbook doesn't mention the project ships an XamlResourceLint tool. Worth a one-liner in the verification ladder: *"Build-time XamlResourceLint catches dangling StaticResource references at parse scope — when adding a new converter or template, either declare it locally in the consuming view's `UserControl.Resources` or explicitly merge the module's `Resources.xaml`."*

## 13a. Chip-stub pattern must be applied symmetrically across requirement and reward paths

**Friction:** The chip-stub pattern (carry `Prefix + ChipName + Reference + IsNavigable` on the display VM, render via converter) got applied to the requirement-side favor lines (`MinFavorLevelRequirement`, `MinFavorRequirement`) and to entity-shaped reward effects (`BestowTitle`, `LearnAbility`, `BestowRecipe`, `EnsureLoreBookKnown`). The reward-side equivalent — `DeltaNpcFavor`, the most common reward effect in the catalogue (239 occurrences) — was left as prose-only. The Text-shape assertion in the synthetic test passed because the *prose* was correct; only the chip fields were absent.

User caught it in visual smoke against a real quest screen: "+10 favor with Orran (Red Wing Casino)" rendered as plain text where every other entity-shape effect was a clickable chip.

**Cookbook addition:** when a tab has parallel projection paths for symmetric concepts (requirements vs rewards), enumerate the entity-shape transforms as a **grid** (kind × path) during chip-stub design, not a flat per-method list. A grid surfaces gaps:

| Entity kind   | Requirement side          | Reward side          |
|---------------|---------------------------|----------------------|
| NPC (favor)   | `MinFavorLevel` ✓ chip    | `DeltaNpcFavor` ✗→✓  |
| Quest         | `QuestCompleted` ✓ chip   | (n/a)                |
| Item          | `InventoryItem` ✓ chip    | (via reward chips)   |
| Ability       | `AbilityKnown` text only  | `LearnAbility` ✓ chip |

The mismatched row (NPC×reward) jumps out visually. A flat list of *"these methods build chips: ..."* hides it.

Related: synthetic tests that assert `Text` should also assert `ChipName`/`Reference`/`Prefix` when a chip is intended. Asserting only Text accepts a regression where the chip half drops to null.

## 13b. Fallback identifier splitter must handle underscores as word boundaries

**Friction:** Chip-stub fallback rendering routes through a `SplitCamelCase` helper that inserts a space before any uppercase letter preceded by a non-uppercase. Catalog identifiers like `LiveEvent_Crafting` (effect keyword), `LiveNpc_Orran` (event NPC), and `LiveEvent_Kalrod_Done` (internal flag) use underscores as semantic separators — the splitter treated `_` as "non-uppercase" and inserted a space *after* it, producing `"Live Event_ Crafting"` (literal underscore + space).

Visible in the visual smoke on the Effects tab's chip-stub fallback (effects tab not registered yet, so the chip renders as fallback text). Fixed by extending the splitter to treat `_` as a word boundary that emits a single space (deduped against the camel-case rule's space insertion).

**Cookbook addition:** when the fallback display path is "CamelCase-split the identifier", verify it gracefully handles **all separator chars present in the source data**, not just camel-case boundaries. Catalog identifiers commonly carry `_` as a semantic separator; some data files also use `.` (e.g. `Skill.Bard`). Add a regression test with a real-data identifier (e.g. `LiveEvent_Crafting`) the first time the helper is wired up — the fallback path is exactly the case synthetic tests with clean identifier inputs don't cover.

## 13. Real-data integration test as a sanity walk artefact

**Friction:** Cookbook rung 4 mandates a manual real-data sanity walk before shipping. I added two automated tests that load the real bundled `quests.json` and walk specific quests (`Wolf_HuntDeer2` for MoonPhase + favor + skill + story; `quest_1` `KillSkeletons` for objectives + rewards). They no-op if bundled data isn't co-located (graceful CI behaviour) but otherwise assert text shape: no `(unknown)` sentinels, every text non-empty, expected buckets present.

**Cookbook addition:** the cookbook's verification ladder rung 4 should encourage this *as* an automated test, not just a manual walk. A test that loads real bundled data + walks 2–3 known-real entries gives the human smoke a foundation: they're testing for *rendering quality* rather than *correctness*, because the test already locked in correctness.

Naming convention: `RealBundled<EntityKind>_<SpecificEntry>_ProjectsSensibly()`. Skips when bundled data isn't present so it works in trimmed CI shapes.

## Summary — proposed cookbook delta

In rough priority order:

1. **Add a "Common pitfalls" section** to *Cross-link chips* including the right-aligned-arrow anti-pattern (item 9).
2. **Add data-shape verification step** to the handoff drafting guidance — every proposed cross-file facet should cite the specific field that contains the foreign key (item 1).
3. **Document the FormattedText attached property** and recommend its use on any long-form text binding (item 6).
4. **Add tail-component-strip pattern** for EntityRef factories (item 7).
5. **Subclass-count automation** — a test fixture asserting every polymorphic discriminator has a switch arm (item 3).
6. **Disambiguator-when-display-names-collide pattern** for entity kinds (item 8).
7. **Cadence/loop-affecting metadata belongs in the header** (item 10).
8. **Chip-stub coverage grid** — enumerate kind × projection-path as a matrix during chip-stub design to surface gaps; assert `ChipName` + `Reference` in tests, not just `Text` (item 13a).
9. **Fallback identifier splitter — test against real-data identifiers** that contain underscores or other separators, not just clean CamelCase (item 13b).
10. **Promote the real-data sanity walk to an automated test** alongside the manual walk (item 13).
11. **XamlResourceLint exists** — mention it (item 12).
12. **SilmarillionViewModel constructor friction is recurrent** — refactor to enumerable injection (item 11).

Items 1, 2, and 5 are the highest-friction items — the first two would have prevented two of the visual-smoke iterations entirely, and the third would have prevented six silently-unhandled subclasses. Items 13a and 13b are both *symmetric-application* failures: the pattern was applied to most-but-not-all cases, and only visual smoke caught the gap. The chip-stub coverage grid (item 13a) is the cheap fix; the underscore-aware splitter (item 13b) is a one-line hardening of a helper used catalogue-wide.
