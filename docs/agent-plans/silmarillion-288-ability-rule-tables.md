# Silmarillion: plumb ability conditional-rule sub-tables (#288)

**Tracked in:** #288 — `module:silmarillion` / `area:ui` / `type:feature`.

**Companion doc:** [silmarillion-roadmap.md](../silmarillion-roadmap.md) (Bucket-C reclassification: these three files now belong on the **Effects tab** side, not Abilities — landed via PR #289).

## What this issue is (and isn't)

Three JSON files were originally framed under Bucket C as ability sub-tables that would fold into the Abilities tab's detail pane. Data-shape verification during #243 caught that all three are **arrays of conditional rules** keyed by `Req*Keywords` predicates, not lookup tables keyed by ability `InternalName` — folding them under per-ability detail would be a category error (the answer to "what keywords does ability X have" is `Ability.Keywords` directly). #288 plumbs the rule data through `IReferenceDataService` so that the **Effects tab (#244)** — the natural consumer, since these rules fire when their `ReqEffectKeywords` predicate matches — can render them in detail.

This issue is **plumbing only**. No tab work, no rendering. The Effects tab (#244) is a separate ticket that will consume what #288 exposes.

## Files in scope (three rule files, all bundled)

| File | Shape | Size | Predicate fields |
|---|---|---|---|
| [`abilitykeywords.json`](../../src/Mithril.Shared/Reference/BundledData/abilitykeywords.json) | array of "if ability has keyword X, these attribute deltas apply" | ~30 entries | `MustHaveAbilityKeywords` |
| [`abilitydynamicdots.json`](../../src/Mithril.Shared/Reference/BundledData/abilitydynamicdots.json) | array of conditional DoT rules | ~4 entries | `ReqAbilityKeywords`, `ReqActiveSkill`, `ReqEffectKeywords` |
| [`abilitydynamicspecialvalues.json`](../../src/Mithril.Shared/Reference/BundledData/abilitydynamicspecialvalues.json) | array of conditional tooltip-value rules | ~5 entries | `ReqAbilityKeywords`, `ReqEffectKeywords` |

POCOs and parsers already exist:

- [`src/Mithril.Reference/Models/Misc/AbilityKeyword.cs`](../../src/Mithril.Reference/Models/Misc/AbilityKeyword.cs)
- [`src/Mithril.Reference/Models/Misc/AbilityDynamicDot.cs`](../../src/Mithril.Reference/Models/Misc/AbilityDynamicDot.cs)
- [`src/Mithril.Reference/Models/Misc/AbilityDynamicSpecialValue.cs`](../../src/Mithril.Reference/Models/Misc/AbilityDynamicSpecialValue.cs)
- `ReferenceDeserializer.ParseAbilityKeywords` / `ParseAbilityDynamicDots` / `ParseAbilityDynamicSpecialValues` at [ReferenceDeserializer.cs:279-301](../../src/Mithril.Reference/Serialization/ReferenceDeserializer.cs#L279-L301).

The gap is the `ReferenceDataService` wiring (Load → ParseAndSwap → public property → RefreshAsync switch → Keys list).

## ⚠️ Critical gotcha — incomplete POCO

The `AbilityKeyword` POCO models only **three** fields:

```csharp
AttributesThatDeltaCritChance
AttributesThatModCritDamage
MustHaveAbilityKeywords
```

The bundled JSON contains **eight** distinct `AttributesThat*` field names:

```
AttributesThatDeltaAccuracy
AttributesThatDeltaCritChance
AttributesThatDeltaDamage      ← not modelled
AttributesThatDeltaPowerCost   ← not modelled
AttributesThatDeltaRange       ← not modelled
AttributesThatDeltaResetTime   ← not modelled
AttributesThatModCritDamage
AttributesThatModDamage        ← not modelled
```

Newtonsoft.Json silently drops unknown fields, so the existing POCO **round-trips a lossy subset** today and any unit test that re-serialises and compares structure would mask the gap. **First task before the service wiring**: extend the POCO with the five missing `AttributesThat*` properties. Verify by grepping the bundled JSON (`grep -oh '"AttributesThat[A-Za-z]*"' src/Mithril.Shared/Reference/BundledData/abilitykeywords.json | sort -u` should produce 8 distinct keys, all modelled).

The two `AbilityDynamic*` POCOs already match their bundled JSON shape — no field extension needed.

## Scope

1. **Extend `AbilityKeyword` POCO** with the missing `AttributesThat*` fields (per the gotcha above). All five are `IReadOnlyList<string>?` like the existing fields. Order them alphabetically inside the class for readability.

2. **Plumb three new properties on `IReferenceDataService`** ([src/Mithril.Shared/Reference/IReferenceDataService.cs](../../src/Mithril.Shared/Reference/IReferenceDataService.cs)), all with empty-list interface defaults so consumer fakes don't ripple (the cookbook pattern — see *Test scaffolding → non-rippling default*):

   ```csharp
   IReadOnlyList<AbilityKeyword> AbilityKeywordRules => Array.Empty<AbilityKeyword>();
   IReadOnlyList<AbilityDynamicDot> AbilityDynamicDots => Array.Empty<AbilityDynamicDot>();
   IReadOnlyList<AbilityDynamicSpecialValue> AbilityDynamicSpecialValues => Array.Empty<AbilityDynamicSpecialValue>();
   ```

   Add the `Mithril.Reference.Models.Misc` `using` if it's not already present.

3. **Wire the implementation in [ReferenceDataService.cs](../../src/Mithril.Shared/Reference/ReferenceDataService.cs)** following the exact pattern used for `DirectedGoals` (also a flat list, also from a single-file array). Concretely:

   - Three private fields (`_abilityKeywordRules`, `_abilityDynamicDots`, `_abilityDynamicSpecialValues`) initialised to `Array.Empty<T>()`.
   - Three `ReferenceFileSnapshot` fields, constructed in the ctor with `ReferenceFileSource.Bundled`, `FallbackCdnVersion`.
   - Public properties returning the backing fields.
   - `LoadAbilityKeywords` / `LoadAbilityDynamicDots` / `LoadAbilityDynamicSpecialValues` private helpers (call from the ctor, anywhere after `LoadAbilities`).
   - `ParseAndSwapAbilityKeywords` / `ParseAndSwapAbilityDynamicDots` / `ParseAndSwapAbilityDynamicSpecialValues` swappers — atomic reference-assign the field, copy `meta` into the snapshot. No cross-file index build is required (these are flat lists, no derived index).
   - Three branches in `RefreshAsync`'s switch.
   - Three new `await RefreshAsync(...)` lines in `RefreshAllAsync`.
   - Three `GetSnapshot` switch arms.
   - Three new file keys in the `Keys` list (`"abilitykeywords"`, `"abilitydynamicdots"`, `"abilitydynamicspecialvalues"`).

   Mirror the `directedgoals` shape line-by-line; that file is the closest existing precedent (flat list, no index).

4. **Predicate-match helpers** — `IRulePredicate` extension method or static helper, optional but useful enough to do once for #244's benefit. The three rule shapes share the `Req*Keywords` predicate vocabulary; collapse the check into one helper rather than open-coding it three times in the consumer:

   ```csharp
   // src/Mithril.Shared/Reference/AbilityRulePredicate.cs (new)
   public static class AbilityRulePredicate
   {
       public static bool Matches(
           IReadOnlyList<string>? required,
           IReadOnlyList<string>? candidate) =>
           required is null || required.Count == 0 ||
           (candidate is not null && required.All(r => candidate.Contains(r, StringComparer.Ordinal)));
   }
   ```

   `MustHaveAbilityKeywords` (on `AbilityKeyword`) and `ReqAbilityKeywords` (on the two `Dynamic*` shapes) call the same helper with the candidate ability's keyword list. `ReqEffectKeywords` calls it with an effect's keyword list. `ReqActiveSkill` is a single string and gets a sibling 2-arg helper — different shape, separate method. Keep the helpers field-agnostic; the consumer (Effects tab) decides which rule field maps to which candidate set.

   If you'd prefer to defer this to #244 (the consumer that actually needs it), say so in the PR description and skip step 4 — the rule data is still queryable as-is. Either choice is fine.

5. **Tests** — new file [`tests/Mithril.Shared.Tests/Reference/ReferenceDataServiceAbilityRuleTests.cs`](../../tests/Mithril.Shared.Tests/Reference/) (closest precedent: [`ReferenceDataServiceProfilesTests.cs`](../../tests/Mithril.Shared.Tests/Reference/ReferenceDataServiceProfilesTests.cs) — flat-list bundled-load shape):

   - **Parse-and-count round-trip per file.** Load the bundled JSON via `ReferenceDataService` (`bundledDir` constructor arg pointed at a temp dir containing real bundled copies, same `[Collection("FileIO")]` pattern as `ReferenceDataServiceAbilityCrossLinkIndexTests`). Assert `AbilityKeywordRules.Count > 0` (real-bundled is ~30), `AbilityDynamicDots.Count > 0` (~4), `AbilityDynamicSpecialValues.Count > 0` (~5). The exact count is **brittle** against CDN refreshes — assert `> 0` rather than exact equality unless you're locking a structural invariant.
   - **POCO property coverage on `AbilityKeyword`.** Walk the bundled list and assert that each of the **five newly-modelled** properties (`AttributesThatDeltaAccuracy`, `AttributesThatDeltaDamage`, `AttributesThatDeltaPowerCost`, `AttributesThatDeltaRange`, `AttributesThatDeltaResetTime`, `AttributesThatModDamage`) is populated on at least one entry. This is a *correctness regression net for this PR* — it catches a future refactor that renames a property and silently breaks the JSON binding (Newtonsoft drops unknown fields silently). It is **not** a drift detector for unknown-on-disk fields; that concern belongs to the daily `cdn-drift-check.yml` workflow and is out of scope here (see Related → #285).
   - **AbilityRulePredicate unit tests** (if you implemented step 4): null/empty required → match; required is subset of candidate → match; required has a value missing from candidate → no match. Three cases minimum.

   Service-level tests for `ReferenceDataService` live under `tests/Mithril.Shared.Tests/Reference/`; the convention-driven parse-validation harness ([`tests/Mithril.Reference.Tests/Validation/BundledDataValidationTests.cs`](../../tests/Mithril.Reference.Tests/Validation/BundledDataValidationTests.cs)) picks up the existing `AbilityKeywordParserSpec` automatically and will continue to pass — though note it only enforces parse/min-count/`IUnknownDiscriminator`, not field-name coverage.

## Out of scope

- **Effects-tab rendering.** Surfacing "behaviours this effect triggers" is #244's responsibility. #288 just exposes the data.
- **Abilities-tab rendering.** This is the category error the issue exists to avoid. Don't add chips or detail-pane sections referencing these tables on `AbilityDetailView`.
- **`IEntityNameResolver` integration.** These rule rows have no `InternalName`; they aren't entities. The resolver remains unchanged.
- **Cross-link chip surfaces.** Same reason — no entity, nothing to link to. The rule-row contents (attribute tokens, effect keywords, ability keywords) are strings that *reference* entities, but resolving those is a #244 problem.
- **CDN refresh ordering with `abilities.json`.** These three files are independent of `abilities.json` — they describe predicates over `Ability.Keywords` values that already round-trip via `LoadAbilities`. No ordering constraint; place the `LoadAbility*Rule` calls anywhere in the ctor after `LoadAbilities`.

## Verification ladder

1. `dotnet build Mithril.slnx` — warnings-as-errors clean.
2. `dotnet test tests/Mithril.Shared.Tests --filter "FullyQualifiedName~AbilityRule"` — new tests pass.
3. `dotnet test Mithril.slnx` — no regressions. `BundledDataValidationTests` should still pass; it now walks the extended `AbilityKeyword` POCO too.
4. **Sanity walk.** `dotnet run --project src/Mithril.Shell` and watch the boot log — confirm three new `Loaded abilitykeywords from bundled (...)` etc. lines appear. No need to surface in UI for this PR.

## Workflow

1. Branch from `origin/main`: `feat/288-ability-rule-tables`. Branch policy forbids direct commits to main — always PR.
2. POCO extension first, then a single commit per logical step (POCO → interface → service wiring → predicate helper → tests) so review can walk through cleanly. ~150-250 LoC total.
3. PR title: `feat(silmarillion): plumb ability conditional-rule tables — #288`.
4. PR body should cite #288, link to the roadmap PR #289 for the reclassification context, and note whether step 4 (predicate helper) is included or deferred to #244.

## Related

- **#289** — roadmap reclassification of the three files into the Effects-tab bucket (already filed; PR open).
- **#243** — Abilities tab handoff that flagged the framing error and deferred to this issue.
- **#244** — Effects tab; the natural rendering consumer. Can ship before, alongside, or after #288 — no ordering constraint, but the rule data is unused until #244 lands.
- **#285** — *Drift detect: new Rewards_Effects prefixes*. The umbrella for extending the existing CDN drift channel (`IUnknownDiscriminator` / `ReportUnknowns` reported via `IDiagnosticsSink`, exercised daily by [`.github/workflows/cdn-drift-check.yml`](../../.github/workflows/cdn-drift-check.yml)) to cover new shapes. If we later want unknown-JSON-field detection on `AbilityKeyword` (so an Elder-Game-shipped 9th `AttributesThat*` surfaces as a warning rather than silently round-tripping lossy), the work belongs in a sibling chore under that umbrella — `[JsonExtensionData]` on the POCO + parser-spec wiring to feed `ReportUnknowns`. Out of scope for #288.
- **Cookbook pattern** ([docs/silmarillion-tab-cookbook.md](../silmarillion-tab-cookbook.md)) — the *Test scaffolding → non-rippling default* section codifies the empty-default interface property pattern this issue uses for the three new properties.

---

*Drafted by Claude (Opus 4.7), filed by @arthur-conde via Claude Code on 2026-05-14.*
