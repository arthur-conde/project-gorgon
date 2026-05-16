# Favor-tier convergence — closing #370

**Tracked in:** #370 (favor-tier divergence umbrella). Follow-up: #385 (Smaug `MinFavorTier` retype).

## Problem

The PG favor ladder was modelled four times. Three module-local copies have been
fixed and one canonical type now exists:

- `Mithril.Reference.Models.Npcs.FavorTier` — canonical enum + `FavorTierExtensions`
  (`Parse`/`ToToken`/`DisplayName`), signed Neutral-centred rank, `Unknown = int.MinValue`.
  Shipped by #373/#368. **Enum + token/display only — no favor-point math.**
- `Arwen.Domain.FavorTier` + `FavorTiers` — duplicate enum **plus** a real
  favor-point calculator: a `(Floor, Cap)` table and `TierForFavor`/`ProgressInTier`/
  `TierBreakdown`/`FavorToReachTier`/`CeilingOf`/`DisplayName`/`TryParse`.
- `Smaug.Domain.FavorTierName` — duplicate string consts + `Ordered` ladder +
  `RankOf`/`IsAtLeast` + #373's `RankOf(FavorTier)` bridge.
- Silmarillion's `FavorOrder` — already converged onto the canonical type (#374/#382).

#370's end-state: one canonical type, consumed everywhere; duplicates deleted.

## Wiki verification (load-bearing — done before canonicalizing)

Arwen's `(Floor, Cap)` table was checked against the Project Gorgon wiki's
"Total Favor" table. **Every real tier matches the wiki exactly.** One value is
fabricated: `Despised` had `Floor = -99999, Cap = 1800`. Despised is the
open-ended *bottom* tier (mirror of Soul Mates' open *top*): unbounded below,
upper boundary −600 (where Hated begins). The `1800` span has no wiki basis and
must **not** be canonicalized. This is the same "converge, don't copy the bug"
lesson as #374.

## Design

### 1. Canonical favor surface — `Mithril.Reference.Models.Npcs.FavorScale` (new)

A new static alongside the existing `FavorTier` enum. The enum and
`FavorTierExtensions` are unchanged.

Each tier is modelled as a **half-open numeric interval `[Floor, Ceiling)`** —
both bounds wiki-readable, no derived/fabricated span. Stored as a tiny
`readonly record struct FavorTierRange(double? Floor, double? Ceiling)` keyed by
`FavorTier`:

| Tier | Floor | Ceiling |
|---|---|---|
| Despised | `null` (unbounded below) | −600 |
| Hated | −600 | −300 |
| Disliked | −300 | −100 |
| Tolerated | −100 | 0 |
| Neutral | 0 | 100 |
| Comfortable | 100 | 300 |
| Friends | 300 | 600 |
| CloseFriends | 600 | 1200 |
| BestFriends | 1200 | 2000 |
| LikeFamily | 2000 | 3000 |
| SoulMates | 3000 | `null` (unbounded above) |

API:
- `RangeOf(FavorTier) : FavorTierRange`
- `FloorOf(FavorTier) : double?`
- `CeilingOf(FavorTier) : double?` (null for both open tiers)
- `SpanOf(FavorTier) : double?` (derived `Ceiling − Floor`; null if either bound null)
- `TierForFavor(double) : FavorTier` — never returns `Unknown` (a favor *number*
  always has a real tier; clamps to `Despised` at the bottom)
- `ProgressInTier(double, FavorTier) : double` — `NaN` for both open tiers
- `FavorToReachTier(double, FavorTier) : double`
- `TierBreakdown(double) : IReadOnlyList<(FavorTier Tier, int Remaining)>` —
  **open-tier rule:** the bottom-open tier (Despised) is *skipped but does not
  terminate* the climb; only the top-open tier (SoulMates) terminates. This
  corrects the current `break`-on-any-null-cap bug exposed by the wiki check.

Naming: `Cap → Span`. In Smaug "cap" means *vendor gold cap*; reusing it for
tier width in shared code is a footgun.

`Unknown` semantics unchanged: it is exclusively the bad-token sentinel from
`Parse`, never produced by `TierForFavor`.

### 2. Deletions & consumer migration

**Deleted:** `Arwen.Domain.FavorTier`/`FavorTiers` (whole file);
`Smaug.Domain.FavorTierName` (whole file); `tests/Smaug.Tests/FavorTierNameParityTests.cs`
(its only purpose was the now-removed string↔typed bridge).

**Arwen** (8 consumer files, ~39 usages — mechanical):
`using Arwen.Domain;` → `using Mithril.Reference.Models.Npcs;` (member names
already identical post-#372). `FavorTiers.*` math → `FavorScale.*`.
`FavorTiers.DisplayName` → `FavorTierExtensions.DisplayName` (dedupes identical
logic). `FavorTiers.TryParse(s, out t)` → `FavorTierExtensions.Parse(s)`; the
behavioural seam is `FavorStateService` Priority-2, which must treat
`FavorTier.Unknown` as "not known" and fall through to Priority-3 (preserves
today's `TryParse==false` behaviour). `FavorTiers.TargetTierOptions`
(Comfortable→SoulMates calculator picker) **stays in Arwen**, retyped — it is a
view concern, not reference data.

**Smaug** (`VendorCapResolver`, `SellPlannerService`,
`StorageSellbackService`, `VendorCatalogService`): tier consts →
`FavorTier` members; `RankOf`/`IsAtLeast`/`Ordered` → direct enum comparison
(the underlying values *are* signed rank); `ResolveMaxGold`'s
`string? playerFavorTier` → `FavorTier`.

**Persistence boundary (respected, not crossed):** `PriceObservation.FavorTier`
and calibration keys (`AbsoluteKey`/`RatioKey`) stay **token-string at rest** —
parsed to `FavorTier` only for in-memory gating, serialized back via
`ToToken()`. No calibration-data migration.

### 3. `IFavorLookupService` retype

`Mithril.Shared` already references `Mithril.Reference`, so no new project
dependency.

`string? GetFavorTier(string)` → `FavorTier? GetFavorTier(string)`.
- `null` keeps its exact meaning: *no favor data / never interacted*.
  Deliberately distinct from `FavorTier.Unknown` (unparseable token).
- Producer: Arwen `FavorStateService` — internal map becomes
  `Dictionary<string, FavorTier>`; the post-#372 `ToToken`/`ToString` shim is
  deleted.
- Consumers: the three Smaug services — `?? FavorTierName.Neutral` →
  `?? FavorTier.Neutral`; "assume Neutral when no data" default preserved.
- Calibration seam: at the single `EstimateSellPrice` call site, convert
  `FavorTier → ToToken()` (persistence stays tokenized).

**`NpcService.MinFavorTier` stays `string?`** for this change, parsed to
`FavorTier` at the Smaug boundary via `FavorTierExtensions.Parse`. Parsing an
IO-sourced string to the canonical type at point of use is correct single-source
usage. The field retype + reference-projection changes are tracked separately in
**#385**.

### 4. Testing

TDD; the wiki correction is behaviour-changing → failing-first.

- `Mithril.Reference.Tests/FavorScaleTests.cs` (new — math's new home):
  - **Adjacency invariant:** for every interior pair `tier[n].Ceiling ==
    tier[n+1].Floor` (gapless, overlap-free). This is the permanent guard that
    would have caught the fabricated `1800`.
  - `TierForFavor` wiki boundary cases: `-600.1→Despised`, `-600→Hated`,
    `-300→Disliked`, `-100→Tolerated`, `-0.1→Tolerated`, `0→Neutral`,
    `99.9→Neutral`, `100→Comfortable`, `3000→SoulMates`.
  - `ProgressInTier` → `NaN` for **both** open tiers; `0.5` mid-tier;
    `CeilingOf` → `null` for both.
  - **`TierBreakdown` from a Despised-favor value** — red-first regression test
    for the wiki fix: climbs through all tiers, not empty.
- Arwen.Tests: pure-math assertions move to `FavorScaleTests`. Arwen.Tests keeps
  only Arwen-specific behaviour (`FavorStateService` Unknown→Priority-3
  fall-through; `TargetTierOptions`). `Parse` test: `"InvalidTier"` → `Unknown`
  (was `false`/`Neutral`).
- Smaug.Tests: delete `FavorTierNameParityTests`; retype `VendorCapResolverTests`
  to the typed signature (red→green guarantee preserved); full suite green as
  no-collateral-regression (calibration parity corpus cannot cover sub-Neutral
  boundaries — known limitation, synthetic test is the proof).

### 5. Packaging & sequencing

Branch `fix/370-favor-tier-convergence` off current `origin/main` (`c4c784d`),
**single PR `closes #370`**. Logical commits:

1. Add `Mithril.Reference.FavorScale` + `FavorScaleTests` (wiki-correct,
   adjacency invariant, TDD red→green for the Despised/`TierBreakdown` fix).
   *Independently green.*
2. Retype `IFavorLookupService → FavorTier?`; converge Arwen; delete
   `Arwen.Domain.FavorTier`/`FavorTiers`; move math tests.
3. Converge Smaug; delete `FavorTierName` + `FavorTierNameParityTests`;
   `MinFavorTier` parsed at boundary.
4. Docs/charter note if warranted; PR body summarizes the #370 resolution and
   links #385.

**Honest sequencing caveat:** commit 1 is independently green; **commits 2–3 are
a compile-coupled unit** — the `IFavorLookupService` retype forces Arwen + Smaug
to move in lockstep, so `main` is only guaranteed green after commit 3.
Verification is per-module suites + full build at end of sequence. Post-merge
re-verify discipline applies on the eventual rebase (this is favor-touching, as
#373 was).

## Out of scope

- `NpcService.MinFavorTier` field retype + reference-projection changes → #385.
- Calibration-data schema change → #385 (default: keep token-at-rest, no migration).
- Any non-favor refactoring of touched files.

## Risks

- Cross-module atomic change; a regression in any module blocks all (accepted —
  inherent to a true single source of truth).
- Rebase exposure if other favor-touching work merges first (mitigated by
  post-merge-reverify; observed with #373).
- `TierBreakdown` open-tier behaviour delta — covered by the red-first test.

---

## Implementation status

Implemented on branch `fix/370-favor-tier-convergence` (2026-05-16): FavorScale + Arwen converge + Smaug converge. Follow-up #385 (Smaug `MinFavorTier` field retype) deferred as designed. Full solution green at convergence.
