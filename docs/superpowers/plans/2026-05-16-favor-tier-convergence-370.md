# Favor-Tier Convergence (#370) Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Collapse the four favor-tier models into one — a wiki-verified `Mithril.Reference.FavorScale` over the existing canonical `FavorTier` enum — delete the Arwen/Smaug duplicates, and retype `IFavorLookupService` to `FavorTier?`. Closes #370.

**Architecture:** New `FavorScale` static in `Mithril.Reference` carries the favor-point math as half-open `[Floor, Ceiling)` intervals (Despised corrected to unbounded-below — the fabricated `1800` is NOT canonicalized). `Arwen.Domain.FavorTier`/`FavorTiers` and `Smaug.Domain.FavorTierName` are deleted; a slim `Arwen.Domain.FavorTiers` adapter is retained holding ONLY the Arwen-UI `TargetTierOptions` + a `RepresentativeFavor` heuristic (keeps `FavorCalculatorTab.xaml`'s `x:Static` binding working with zero XAML churn). Logic is typed; the Smaug display/persistence layer stays string at rest.

**Tech Stack:** .NET 10, C# (nullable enabled, warnings-as-errors), xunit + FluentAssertions. Spec: `docs/superpowers/specs/2026-05-16-favor-tier-convergence-370-design.md`.

**Branch:** `fix/370-favor-tier-convergence` (already created off `origin/main` `c4c784d`; spec already committed as `ce26a88`).

**Reference — canonical enum (already on main, do not edit):** `Mithril.Reference.Models.Npcs.FavorTier` = `Unknown = int.MinValue, Despised = -4, Hated = -3, Disliked = -2, Tolerated = -1, Neutral = 0, Comfortable = 1, Friends = 2, CloseFriends = 3, BestFriends = 4, LikeFamily = 5, SoulMates = 6`. `FavorTierExtensions.Parse(string?) → FavorTier` (Unknown for blank/unrecognised/numeric), `.ToToken() → string` (member name), `.DisplayName() → string` (curated; "Close Friends", "Best Friends", "Like Family", "Soul Mates", "Unknown", else member name).

---

## Task 1: Create `FavorScale` in Mithril.Reference (independently green)

**Files:**
- Create: `src/Mithril.Reference/Models/Npcs/FavorScale.cs`
- Test: `tests/Mithril.Reference.Tests/FavorScaleTests.cs`

- [ ] **Step 1: Write the failing tests**

Create `tests/Mithril.Reference.Tests/FavorScaleTests.cs`:

```csharp
using FluentAssertions;
using Mithril.Reference.Models.Npcs;
using Xunit;

namespace Mithril.Reference.Tests;

public sealed class FavorScaleTests
{
    // The guard that would have caught Arwen's fabricated "1800": the ladder must
    // be gapless and overlap-free — every interior tier's Ceiling is the next
    // tier's Floor. Only Despised.Floor and SoulMates.Ceiling may be null (open).
    [Fact]
    public void Table_IsGaplessAndOverlapFree()
    {
        FavorTier[] ladder =
        [
            FavorTier.Despised, FavorTier.Hated, FavorTier.Disliked, FavorTier.Tolerated,
            FavorTier.Neutral, FavorTier.Comfortable, FavorTier.Friends, FavorTier.CloseFriends,
            FavorTier.BestFriends, FavorTier.LikeFamily, FavorTier.SoulMates,
        ];

        FavorScale.FloorOf(FavorTier.Despised).Should().BeNull("Despised is unbounded below");
        FavorScale.CeilingOf(FavorTier.SoulMates).Should().BeNull("SoulMates is unbounded above");

        for (var i = 0; i < ladder.Length - 1; i++)
            FavorScale.CeilingOf(ladder[i]).Should()
                .Be(FavorScale.FloorOf(ladder[i + 1]),
                    $"{ladder[i]}.Ceiling must equal {ladder[i + 1]}.Floor");
    }

    [Theory]
    [InlineData(-600.1, FavorTier.Despised)]
    [InlineData(-600, FavorTier.Hated)]
    [InlineData(-300.1, FavorTier.Hated)]
    [InlineData(-300, FavorTier.Disliked)]
    [InlineData(-100, FavorTier.Tolerated)]
    [InlineData(-0.1, FavorTier.Tolerated)]
    [InlineData(0, FavorTier.Neutral)]
    [InlineData(99.9, FavorTier.Neutral)]
    [InlineData(100, FavorTier.Comfortable)]
    [InlineData(299.9, FavorTier.Comfortable)]
    [InlineData(300, FavorTier.Friends)]
    [InlineData(600, FavorTier.CloseFriends)]
    [InlineData(1200, FavorTier.BestFriends)]
    [InlineData(2000, FavorTier.LikeFamily)]
    [InlineData(3000, FavorTier.SoulMates)]
    [InlineData(99999, FavorTier.SoulMates)]
    [InlineData(-99999, FavorTier.Despised)]
    public void TierForFavor_MatchesWikiBoundaries(double favor, FavorTier expected) =>
        FavorScale.TierForFavor(favor).Should().Be(expected);

    [Fact]
    public void TierForFavor_NeverReturnsUnknown() =>
        FavorScale.TierForFavor(-1e9).Should().Be(FavorTier.Despised);

    [Theory]
    [InlineData(0, FavorTier.Neutral, 0.0)]
    [InlineData(50, FavorTier.Neutral, 0.5)]
    [InlineData(100, FavorTier.Neutral, 1.0)]
    [InlineData(300, FavorTier.Friends, 0.0)]
    [InlineData(450, FavorTier.Friends, 0.5)]
    public void ProgressInTier_ClosedTier(double favor, FavorTier tier, double expected) =>
        FavorScale.ProgressInTier(favor, tier).Should().BeApproximately(expected, 1e-9);

    [Theory]
    [InlineData(FavorTier.SoulMates)]
    [InlineData(FavorTier.Despised)]
    public void ProgressInTier_OpenTiers_AreNaN(FavorTier tier) =>
        double.IsNaN(FavorScale.ProgressInTier(5000, tier)).Should().BeTrue();

    [Fact]
    public void CeilingOf_BothOpenTiers()
    {
        FavorScale.CeilingOf(FavorTier.Despised).Should().Be(-600);
        FavorScale.CeilingOf(FavorTier.SoulMates).Should().BeNull();
        FavorScale.FloorOf(FavorTier.SoulMates).Should().Be(3000);
    }

    [Fact]
    public void FavorToReachTier_NeededAndAlreadyThere()
    {
        FavorScale.FavorToReachTier(50, FavorTier.Friends).Should().Be(250);
        FavorScale.FavorToReachTier(500, FavorTier.Friends).Should().Be(0);
    }

    // Regression guard for the wiki correction: from a Despised-favor value the
    // breakdown must still climb every closed tier — not return empty because
    // the bottom tier is open. (Old code break-ed on any null cap.)
    [Fact]
    public void TierBreakdown_FromDespised_ClimbsAllTiers_NotEmpty()
    {
        var b = FavorScale.TierBreakdown(-1000);
        b.Should().NotBeEmpty();
        b[0].Tier.Should().Be(FavorTier.Hated, "Despised is open-bottom — first climb step is Hated");
        b.Should().Contain(x => x.Tier == FavorTier.LikeFamily);
        b.Should().NotContain(x => x.Tier == FavorTier.SoulMates, "top tier is open — terminates the climb");
    }

    [Fact]
    public void TierBreakdown_FromNeutral()
    {
        var b = FavorScale.TierBreakdown(50);
        b[0].Tier.Should().Be(FavorTier.Neutral);
        b[0].Remaining.Should().Be(50);
    }
}
```

- [ ] **Step 2: Run tests, verify they fail**

Run: `dotnet test tests/Mithril.Reference.Tests --filter "FullyQualifiedName~FavorScaleTests"`
Expected: FAIL — `FavorScale` does not exist (compile error).

- [ ] **Step 3: Implement `FavorScale`**

Create `src/Mithril.Reference/Models/Npcs/FavorScale.cs`:

```csharp
namespace Mithril.Reference.Models.Npcs;

/// <summary>
/// A favor tier's half-open favor-point interval <c>[Floor, Ceiling)</c>.
/// <see cref="Floor"/> is null for the open-bottom tier (Despised);
/// <see cref="Ceiling"/> is null for the open-top tier (SoulMates).
/// </summary>
public readonly record struct FavorTierRange(double? Floor, double? Ceiling);

/// <summary>
/// Canonical favor-point model for <see cref="FavorTier"/>. Values are the
/// Project Gorgon wiki "Total Favor" thresholds (verified). Despised is
/// unbounded below and SoulMates unbounded above — symmetric open tiers; the
/// ladder is otherwise gapless (every Ceiling is the next Floor), enforced by
/// <c>FavorScaleTests.Table_IsGaplessAndOverlapFree</c>.
/// </summary>
public static class FavorScale
{
    // Ordered low → high. Half-open [Floor, Ceiling).
    private static readonly (FavorTier Tier, FavorTierRange Range)[] Table =
    [
        (FavorTier.Despised,     new(null,  -600)),
        (FavorTier.Hated,        new(-600,  -300)),
        (FavorTier.Disliked,     new(-300,  -100)),
        (FavorTier.Tolerated,    new(-100,     0)),
        (FavorTier.Neutral,      new(   0,   100)),
        (FavorTier.Comfortable,  new( 100,   300)),
        (FavorTier.Friends,      new( 300,   600)),
        (FavorTier.CloseFriends, new( 600,  1200)),
        (FavorTier.BestFriends,  new(1200,  2000)),
        (FavorTier.LikeFamily,   new(2000,  3000)),
        (FavorTier.SoulMates,    new(3000,  null)),
    ];

    private static int IndexOf(FavorTier tier)
    {
        for (var i = 0; i < Table.Length; i++)
            if (Table[i].Tier == tier) return i;
        throw new ArgumentOutOfRangeException(nameof(tier), tier,
            "FavorScale has no range for this tier (FavorTier.Unknown is not a real tier).");
    }

    public static FavorTierRange RangeOf(FavorTier tier) => Table[IndexOf(tier)].Range;

    public static double? FloorOf(FavorTier tier) => RangeOf(tier).Floor;

    public static double? CeilingOf(FavorTier tier) => RangeOf(tier).Ceiling;

    /// <summary>Tier width, or null when either bound is open (Despised/SoulMates).</summary>
    public static double? SpanOf(FavorTier tier)
    {
        var r = RangeOf(tier);
        return r is { Floor: { } f, Ceiling: { } c } ? c - f : null;
    }

    /// <summary>
    /// The tier a raw favor value falls in. A favor *number* always has a real
    /// tier — never returns <see cref="FavorTier.Unknown"/>; clamps to Despised
    /// at the bottom.
    /// </summary>
    public static FavorTier TierForFavor(double favor)
    {
        for (var i = Table.Length - 1; i >= 0; i--)
        {
            var floor = Table[i].Range.Floor;
            if (floor is null || favor >= floor.Value) return Table[i].Tier;
        }
        return FavorTier.Despised;
    }

    /// <summary>0.0–1.0 progress within the tier; NaN for the open tiers.</summary>
    public static double ProgressInTier(double favor, FavorTier tier)
    {
        var r = RangeOf(tier);
        if (r.Floor is not { } floor || r.Ceiling is not { } ceiling) return double.NaN;
        return Math.Clamp((favor - floor) / (ceiling - floor), 0.0, 1.0);
    }

    /// <summary>Favor points still needed to reach <paramref name="target"/>'s floor.</summary>
    public static double FavorToReachTier(double currentFavor, FavorTier target)
    {
        var floor = RangeOf(target).Floor ?? double.NegativeInfinity;
        return Math.Max(0, floor - currentFavor);
    }

    /// <summary>
    /// Remaining favor to clear each closed tier from the current position. The
    /// open-bottom tier (Despised) is skipped but does not terminate the climb;
    /// the open-top tier (SoulMates) terminates it.
    /// </summary>
    public static IReadOnlyList<(FavorTier Tier, int Remaining)> TierBreakdown(double currentFavor)
    {
        var start = IndexOf(TierForFavor(currentFavor));
        var result = new List<(FavorTier, int)>();

        for (var i = start; i < Table.Length; i++)
        {
            var (tier, range) = Table[i];
            if (range.Ceiling is not { } ceiling)
            {
                if (range.Floor is null) continue; // open-bottom (Despised): skip, keep climbing
                break;                              // open-top (SoulMates): terminate
            }
            var remaining = (int)Math.Max(0, ceiling - currentFavor);
            if (remaining > 0) result.Add((tier, remaining));
            currentFavor = Math.Max(currentFavor, ceiling);
        }

        return result;
    }
}
```

- [ ] **Step 4: Run tests, verify they pass**

Run: `dotnet test tests/Mithril.Reference.Tests --filter "FullyQualifiedName~FavorScaleTests"`
Expected: PASS (all green, no warnings).

- [ ] **Step 5: Commit**

```bash
git add src/Mithril.Reference/Models/Npcs/FavorScale.cs tests/Mithril.Reference.Tests/FavorScaleTests.cs
git commit -m "feat(reference): FavorScale — wiki-verified favor-point model for FavorTier (#370)

Half-open [Floor, Ceiling) intervals; Despised unbounded-below (corrects
Arwen's fabricated 1800 span — not canonicalized); gapless-ladder invariant
test; TierBreakdown distinguishes open-bottom (skip) vs open-top (terminate).

Co-Authored-By: Claude Opus 4.7 (1M context) <noreply@anthropic.com>"
```

---

## Task 2: Retype `IFavorLookupService` + converge Arwen

**Files:**
- Modify: `src/Mithril.Shared/Reference/IFavorLookupService.cs`
- Delete: `src/Arwen.Module/Domain/FavorTier.cs` (replace contents — see Step 3)
- Modify: `src/Arwen.Module/State/FavorStateService.cs:51-58,135-158`
- Modify: `src/Arwen.Module/Views/Converters.cs:3,26`
- Modify: `src/Arwen.Module/Domain/StorageGiftCards.cs:1 (usings),34-35`
- Modify: `src/Arwen.Module/ViewModels/FavorCalculatorViewModel.cs`
- Modify: `src/Arwen.Module/ViewModels/GiftScannerViewModel.cs`
- Modify: `src/Arwen.Module/ViewModels/StorageGiftsViewModel.cs`
- Modify: `src/Arwen.Module/ViewModels/ItemLookupViewModel.cs:118`
- Modify: `tests/Arwen.Tests/FavorTierTests.cs` (replace — see Step 9)

> **Note:** `GiftIndex.cs:95 pref.RequiredFavorTier` is an unrelated gift-preference string field, NOT `Arwen.Domain.FavorTier`. Do not touch it.

> **Compile-coupled with Task 3:** the interface retype breaks Smaug until Task 3. Build/test green is verified at the END of Task 3. Commit Task 2 as a logical unit even though `dotnet build Smaug.Module` will fail between Task 2 and Task 3.

- [ ] **Step 1: Retype the shared interface**

In `src/Mithril.Shared/Reference/IFavorLookupService.cs`, add `using Mithril.Reference.Models.Npcs;` at the top (above `namespace`), and replace the method + its doc:

```csharp
    /// <summary>
    /// The known favor tier for the active character with this NPC, as the
    /// canonical <see cref="FavorTier"/>. Returns null when the player has not
    /// interacted with the NPC or no favor data is available (distinct from
    /// <see cref="FavorTier.Unknown"/>, which means an unparseable token).
    /// </summary>
    FavorTier? GetFavorTier(string npcKey);
```

- [ ] **Step 2: Replace `Arwen.Domain.FavorTier.cs` with the slim retained adapter**

Replace the ENTIRE contents of `src/Arwen.Module/Domain/FavorTier.cs` with (deletes the duplicate enum + all math; keeps only the Arwen-UI adapter so `FavorCalculatorTab.xaml`'s `x:Static domain:FavorTiers.TargetTierOptions` keeps working):

```csharp
using Mithril.Reference.Models.Npcs;

namespace Arwen.Domain;

/// <summary>
/// Arwen-local favor-UI adapter. The favor model itself is canonical
/// (<see cref="FavorTier"/> + <see cref="FavorScale"/> in Mithril.Reference);
/// this holds only Arwen view concerns: the calculator's target picker list and
/// the "estimate a favor value from a known tier" heuristic used when the exact
/// favor is unknown.
/// </summary>
public static class FavorTiers
{
    /// <summary>Calculator target options (Comfortable → Soul Mates). Bound by
    /// FavorCalculatorTab.xaml via <c>x:Static</c>.</summary>
    public static IReadOnlyList<FavorTier> TargetTierOptions { get; } =
        [FavorTier.Comfortable, FavorTier.Friends, FavorTier.CloseFriends,
         FavorTier.BestFriends, FavorTier.LikeFamily, FavorTier.SoulMates];

    /// <summary>
    /// A finite favor value standing in for a tier when only the tier is known
    /// (not the exact favor): the tier floor, or for open-bottom Despised its
    /// ceiling (−600 — wiki-grounded, vs the old fabricated −99999).
    /// </summary>
    public static double RepresentativeFavor(FavorTier tier) =>
        FavorScale.FloorOf(tier) ?? FavorScale.CeilingOf(tier) ?? 0;
}
```

- [ ] **Step 3: Migrate `FavorStateService`**

In `src/Arwen.Module/State/FavorStateService.cs`:

Add `using Mithril.Reference.Models.Npcs;` at the top if not present (it uses `FavorTier`/`FavorScale`).

Replace `GetFavorTier` (lines ~51-58):

```csharp
    public FavorTier? GetFavorTier(string npcKey)
    {
        if (string.IsNullOrEmpty(npcKey)) return null;
        return _tierByNpcKey.TryGetValue(npcKey, out var tier) ? tier : null;
    }
```

In `ApplyFavorData`, replace the Priority-1 body lines:

```csharp
            entry.CurrentTier = FavorScale.TierForFavor(snapshot.ExactFavor);
            entry.TierProgress = FavorScale.ProgressInTier(snapshot.ExactFavor, entry.CurrentTier);
```

Replace the Priority-2 block (the `if (activeChar?.NpcFavor.TryGetValue... && FavorTiers.TryParse...)`):

```csharp
        // Priority 2: Character export tier (no exact value). Unknown token →
        // treat as not-known and fall through to Priority 3 (preserves the old
        // FavorTiers.TryParse==false behaviour).
        if (activeChar?.NpcFavor.TryGetValue(entry.NpcKey, out var tierName) == true)
        {
            var parsed = FavorTierExtensions.Parse(tierName);
            if (parsed != FavorTier.Unknown)
            {
                entry.CurrentTier = parsed;
                entry.ExactFavor = null;
                entry.TierProgress = double.NaN;
                entry.IsKnown = true;
                return;
            }
        }
```

Priority-3 line `entry.CurrentTier = FavorTier.Neutral;` is unchanged (now the canonical enum via the new using).

- [ ] **Step 4: Migrate `Converters.cs`**

In `src/Arwen.Module/Views/Converters.cs`: change line 3 `using Arwen.Domain;` → `using Mithril.Reference.Models.Npcs;`, and line 26:

```csharp
        value is FavorTier tier ? tier.DisplayName() : value?.ToString() ?? "";
```

- [ ] **Step 5: Migrate `StorageGiftCards.cs`**

In `src/Arwen.Module/Domain/StorageGiftCards.cs`: add `using Mithril.Reference.Models.Npcs;` at the top. The record fields `FavorTier CurrentTier` / `FavorTier ProjectedTier` (lines ~34-35) now resolve to the canonical type — no further change.

- [ ] **Step 6: Migrate `FavorCalculatorViewModel.cs`**

In `src/Arwen.Module/ViewModels/FavorCalculatorViewModel.cs`: ensure `using Mithril.Reference.Models.Npcs;` is present; `using Arwen.Domain;` stays (still uses `FavorTiers.RepresentativeFavor`). Apply these exact line replacements:

- L179 `currentFavor = FavorTiers.FloorOf(SelectedNpc.CurrentTier);` → `currentFavor = FavorTiers.RepresentativeFavor(SelectedNpc.CurrentTier);`
- L182 `var targetFloor = FavorTiers.FloorOf(TargetTier);` → `var targetFloor = FavorScale.FloorOf(TargetTier) ?? 0;`
- L187 `{FavorTiers.DisplayName(TargetTier)}` → `{TargetTier.DisplayName()}`
- L200 `{FavorTiers.DisplayName(TargetTier)}` → `{TargetTier.DisplayName()}`
- L205 `var breakdown = FavorTiers.TierBreakdown(currentFavor);` → `var breakdown = FavorScale.TierBreakdown(currentFavor);`
- L207 `.Where(b => FavorTiers.FloorOf(b.Tier) < targetFloor || b.Tier == TargetTier)` → `.Where(b => (FavorScale.FloorOf(b.Tier) ?? double.NegativeInfinity) < targetFloor || b.Tier == TargetTier)`
- L208 `.Select(b => $"{FavorTiers.DisplayName(b.Tier)}: {b.Remaining:N0}");` → `.Select(b => $"{b.Tier.DisplayName()}: {b.Remaining:N0}");`

(`_targetTier = FavorTier.SoulMates`, `OnTargetTierChanged(FavorTier value)` need only the using — names unchanged.)

- [ ] **Step 7: Migrate `GiftScannerViewModel.cs` and `StorageGiftsViewModel.cs`**

Add `using Mithril.Reference.Models.Npcs;` to both. Record fields `FavorTier CurrentTier/ProjectedTier` resolve canonically. Replace these exact expressions:

`GiftScannerViewModel.cs`:
- L144 `?? (double?)FavorTiers.FloorOf(SelectedNpc.CurrentTier);` → `?? FavorTiers.RepresentativeFavor(SelectedNpc.CurrentTier);`
- L168 `var tierCeiling = (double)(FavorTiers.CeilingOf(currentTier) ?? FavorTiers.FloorOf(currentTier));` → `var tierCeiling = FavorScale.CeilingOf(currentTier) ?? FavorScale.FloorOf(currentTier) ?? 0;`
- L172 `currentTier = FavorTiers.TierForFavor(currentFavor.Value);` → `currentTier = FavorScale.TierForFavor(currentFavor.Value);`
- L173 `tierCeiling = (double)(FavorTiers.CeilingOf(currentTier) ?? currentFavor.Value);` → `tierCeiling = FavorScale.CeilingOf(currentTier) ?? currentFavor.Value;`
- L174 `currentFrac = FavorTiers.ProgressInTier(currentFavor.Value, currentTier);` → `currentFrac = FavorScale.ProgressInTier(currentFavor.Value, currentTier);`
- L180 `projectedTier = FavorTiers.TierForFavor(proj);` → `projectedTier = FavorScale.TierForFavor(proj);`
- L182 `? FavorTiers.ProgressInTier(proj, currentTier)` → `? FavorScale.ProgressInTier(proj, currentTier)`

`StorageGiftsViewModel.cs`:
- L210 `?? (favorEntry?.IsKnown == true ? (double?)FavorTiers.FloorOf(favorEntry.CurrentTier) : null);` → `?? (favorEntry?.IsKnown == true ? FavorTiers.RepresentativeFavor(favorEntry.CurrentTier) : (double?)null);`
- L217 `var currentTier = favorEntry?.CurrentTier ?? FavorTier.Neutral;` → unchanged (using only)
- L221 `var tierCeiling = (double)(FavorTiers.CeilingOf(currentTier) ?? FavorTiers.FloorOf(currentTier));` → `var tierCeiling = FavorScale.CeilingOf(currentTier) ?? FavorScale.FloorOf(currentTier) ?? 0;`
- L225 `currentTier = FavorTiers.TierForFavor(currentFavor.Value);` → `currentTier = FavorScale.TierForFavor(currentFavor.Value);`
- L226 `tierCeiling = (double)(FavorTiers.CeilingOf(currentTier) ?? currentFavor.Value);` → `tierCeiling = FavorScale.CeilingOf(currentTier) ?? currentFavor.Value;`
- L227 `var cFrac = FavorTiers.ProgressInTier(currentFavor.Value, currentTier);` → `var cFrac = FavorScale.ProgressInTier(currentFavor.Value, currentTier);`
- L234 `projectedTier = FavorTiers.TierForFavor(proj);` → `projectedTier = FavorScale.TierForFavor(proj);`
- L236 `? FavorTiers.ProgressInTier(proj, currentTier)` → `? FavorScale.ProgressInTier(proj, currentTier)`

- [ ] **Step 8: Migrate `ItemLookupViewModel.cs`**

Add `using Mithril.Reference.Models.Npcs;`. L118:

```csharp
                CurrentTier = favorEntry is not null ? favorEntry.CurrentTier.DisplayName() : "",
```

- [ ] **Step 9: Replace `tests/Arwen.Tests/FavorTierTests.cs`**

The pure-math tests moved to `FavorScaleTests` (Task 1). Replace the ENTIRE file with the Arwen-specific behaviour that remains — the `FavorStateService` Unknown→Priority-3 fall-through and `TargetTierOptions`/`RepresentativeFavor`:

```csharp
using Arwen.Domain;
using FluentAssertions;
using Mithril.Reference.Models.Npcs;
using Xunit;

namespace Arwen.Tests;

public sealed class FavorTierTests
{
    [Fact]
    public void TargetTierOptions_AreComfortableThroughSoulMates() =>
        FavorTiers.TargetTierOptions.Should().Equal(
            FavorTier.Comfortable, FavorTier.Friends, FavorTier.CloseFriends,
            FavorTier.BestFriends, FavorTier.LikeFamily, FavorTier.SoulMates);

    [Fact]
    public void RepresentativeFavor_UsesFloor_AndDespisedFallsBackToCeiling()
    {
        FavorTiers.RepresentativeFavor(FavorTier.Friends).Should().Be(300);
        FavorTiers.RepresentativeFavor(FavorTier.Despised).Should().Be(-600);
        FavorTiers.RepresentativeFavor(FavorTier.SoulMates).Should().Be(3000);
    }

    [Fact]
    public void Parse_UnknownToken_IsUnknown_NotNeutral() =>
        FavorTierExtensions.Parse("InvalidTier").Should().Be(FavorTier.Unknown);

    [Theory]
    [InlineData("Friends", FavorTier.Friends)]
    [InlineData("Hated", FavorTier.Hated)]
    [InlineData("SoulMates", FavorTier.SoulMates)]
    public void Parse_KnownTokens(string token, FavorTier expected) =>
        FavorTierExtensions.Parse(token).Should().Be(expected);
}
```

- [ ] **Step 10: Commit (build NOT yet green — Smaug breaks until Task 3)**

```bash
git add src/Mithril.Shared/Reference/IFavorLookupService.cs src/Arwen.Module tests/Arwen.Tests/FavorTierTests.cs
git commit -m "refactor(arwen): converge onto canonical FavorTier/FavorScale; retype IFavorLookupService (#370)

Delete Arwen.Domain.FavorTier enum + FavorTiers math (now Mithril.Reference
FavorScale). Retain a slim Arwen.Domain.FavorTiers adapter (TargetTierOptions
+ RepresentativeFavor) so FavorCalculatorTab.xaml needs no change. Retype
IFavorLookupService.GetFavorTier -> FavorTier?. Smaug converges in the next
commit (interface change is compile-coupled).

Co-Authored-By: Claude Opus 4.7 (1M context) <noreply@anthropic.com>"
```

---

## Task 3: Converge Smaug + delete `FavorTierName` (restores green)

**Files:**
- Delete: `src/Smaug.Module/Domain/FavorTierName.cs`
- Delete: `tests/Smaug.Tests/FavorTierNameParityTests.cs`
- Modify: `src/Smaug.Module/Domain/VendorCapResolver.cs:32-49`
- Modify: `src/Smaug.Module/State/SellPlannerService.cs:99-105`
- Modify: `src/Smaug.Module/State/StorageSellbackService.cs:112,121-124,146`
- Modify: `src/Smaug.Module/State/VendorCatalogService.cs:81-90`
- Modify: `tests/Smaug.Tests/VendorCapResolverTests.cs`

- [ ] **Step 1: Delete the duplicate + its bridge test**

```bash
git rm src/Smaug.Module/Domain/FavorTierName.cs tests/Smaug.Tests/FavorTierNameParityTests.cs
```

- [ ] **Step 2: Migrate `VendorCapResolver.cs`**

`using Mithril.Reference.Models.Npcs;` already present (it uses `FavorTier` via #373). Change the signature and body:

- Signature L32-36: `string? playerFavorTier` → `FavorTier playerFavorTier`.
- L41 `var currentTier = playerFavorTier ?? FavorTierName.Neutral;` → `var currentTier = playerFavorTier;`
- L42 → `if (store.MinFavorTier is { } minTier && currentTier < FavorTierExtensions.Parse(minTier)) return null;`
- L45 `var currentRank = FavorTierName.RankOf(currentTier);` → delete this line.
- L49 `if (FavorTierName.RankOf(cap.Tier) > currentRank) continue;` → `if (cap.Tier > currentTier) continue;`

- [ ] **Step 3: Migrate `SellPlannerService.cs` (L99-105)**

```csharp
            var playerTier = _favorLookup?.GetFavorTier(npcKey);
            var isAccessible = store.MinFavorTier is null ||
                               (playerTier ?? FavorTier.Neutral) >= FavorTierExtensions.Parse(store.MinFavorTier);

            // Use the player's known tier for the estimate when we have it; otherwise fall back
            // to the vendor's requirement so users see some number for tier-gated vendors.
            var estimateTier = playerTier
                ?? (store.MinFavorTier is { } m ? FavorTierExtensions.Parse(m) : FavorTier.Neutral);
            var estimate = _calibration.EstimateSellPrice(
                npcKey,
                item.InternalName,
                estimateTier.ToToken(),
                _sellContext.CivicPrideLevel);
```

Add `using Mithril.Reference.Models.Npcs;` if not already present.

- [ ] **Step 4: Migrate `StorageSellbackService.cs`**

Add `using Mithril.Reference.Models.Npcs;` if missing. L112 `var playerTier = _favorLookup?.GetFavorTier(npcKey);` (type now `FavorTier?` — unchanged source line). L121-124 `if (playerTier is not null) { ... ResolveMaxGold(store, playerTier, ...) ... }` → pass `playerTier.Value`:

```csharp
                if (playerTier is not null)
                {
                    maxGold = VendorCapResolver.ResolveMaxGold(
                        store, playerTier.Value, ctx.Keywords, _sellContext.CivicPrideLevel);
```

L146 `PlayerFavorTier: playerTier,` — the display record field is `string?` (unchanged): replace with `PlayerFavorTier: playerTier?.DisplayName(),`.

- [ ] **Step 5: Migrate `VendorCatalogService.cs` (L81-90)**

```csharp
                FavorTier? playerTier = null;
                int? maxGold = null;
                bool? acceptable = null;
                if (storeService is not null)
                {
                    playerTier = _favorLookup?.GetFavorTier(src.Npc);
                    if (playerTier is not null)
                    {
                        maxGold = VendorCapResolver.ResolveMaxGold(
                            storeService, playerTier.Value, itemKeywords, _sellContext.CivicPrideLevel);
                        acceptable = maxGold is not null && item.Value <= maxGold.Value;
                    }
                }
```

Add `using Mithril.Reference.Models.Npcs;` if missing. Where `playerTier` is later put into the catalog display record (field is `string?`), pass `playerTier?.DisplayName()` (locate the `new VendorCatalogEntry(...)` and the `PlayerFavorTier:` argument; convert there exactly as in Step 4).

- [ ] **Step 6: Update `VendorCapResolverTests.cs`**

The cap-tier args already use the typed `FavorTier` (from #381). Change the player-tier args from the string alias to the enum: replace `playerFavorTier: FavorTierName.Neutral` with `playerFavorTier: FavorTier.Neutral` and remove the now-unused `using FavorTier = Mithril.Reference.Models.Npcs.FavorTier;` alias only if it now conflicts (it does not — keep it; `FavorTierName` alias/using is removed). Verify the file has no remaining `FavorTierName` reference.

- [ ] **Step 7: Build the whole solution, verify green**

Run: `dotnet build Mithril.slnx`
Expected: SUCCESS, no warnings (warnings-as-errors). If `FavorTierName` symbol errors remain, grep `grep -rn "FavorTierName" src tests` and fix each per the patterns above.

- [ ] **Step 8: Run the full affected suites**

Run: `dotnet test tests/Mithril.Reference.Tests tests/Arwen.Tests tests/Smaug.Tests tests/Silmarillion.Tests`
Expected: ALL PASS. (Silmarillion already consumes the canonical type — guards against cross-module regression.)

- [ ] **Step 9: Commit**

```bash
git add src/Smaug.Module tests/Smaug.Tests
git commit -m "refactor(smaug): converge onto canonical FavorTier; delete FavorTierName (#370)

RankOf/IsAtLeast/Ordered → direct enum comparison; ResolveMaxGold takes
FavorTier; IFavorLookupService consumers typed. MinFavorTier parsed at the
boundary via FavorTierExtensions.Parse (field retype deferred to #385).
Display records keep string (DisplayName at the boundary); calibration stays
token-keyed. Deletes FavorTierName + its obsolete parity bridge test.

Co-Authored-By: Claude Opus 4.7 (1M context) <noreply@anthropic.com>"
```

---

## Task 4: Docs + close-out

**Files:**
- Modify: `docs/superpowers/specs/2026-05-16-favor-tier-convergence-370-design.md` (add an "Implemented" note line) — optional, only if it adds clarity.

- [ ] **Step 1: Full solution build + test sweep**

Run: `dotnet build Mithril.slnx && dotnet test Mithril.slnx`
Expected: SUCCESS; all tests pass. This is the post-merge-reverify gate — the whole tree green, not just touched modules.

- [ ] **Step 2: Confirm no favor duplication remains**

Run: `grep -rn "FavorTierName\|Arwen.Domain.*enum FavorTier\|FavorOrder" src tests`
Expected: no `FavorTierName` anywhere; no second `FavorTier` enum; no `FavorOrder`. (Hits inside the spec/plan docs are fine.)

- [ ] **Step 3: Push branch and open PR**

```bash
git push -u origin fix/370-favor-tier-convergence
```
Then `gh pr create --base main --head fix/370-favor-tier-convergence` with title `refactor(favor): converge all favor-tier models onto canonical FavorTier/FavorScale (closes #370)` and a body that: summarises the #370 umbrella resolution (4 models → 1), states the wiki verification + the Despised `1800` correction, lists the deleted types, calls out the compile-coupled commit 2–3 caveat, links the #385 follow-up, and ends with the standard Claude Code trailer. Use `--body-file` (a temp file) per the multiline-body gotcha.

- [ ] **Step 4: Post the #370 umbrella resolution comment**

Comment on #370 noting all four models converged (Silmarillion #382 already; Arwen/Smaug here), duplicates deleted, wiki-corrected, follow-up #385 filed. Append the `— drafted by Claude (Opus 4.7), posted by @arthur-conde` trailer.

---

## Self-Review

**Spec coverage:** §1 canonical surface → Task 1. §2 deletions/Arwen+Smaug migration → Tasks 2–3. §3 interface retype + MinFavorTier-at-boundary + calibration token seam → Task 2 Step 1, Task 3 Steps 2–5. §4 testing (adjacency invariant, boundaries, NaN open tiers, Despised breakdown red-first, Arwen-specific tests, delete parity test, retype VendorCapResolverTests) → Task 1 Steps 1–4, Task 2 Step 9, Task 3 Steps 1/6/8. §5 packaging (4 commits, lockstep caveat) → Task structure + Task 2 note + Task 3 Step 7-9. Out-of-scope (#385) → not implemented, referenced. All sections covered.

**Placeholder scan:** No TBD/TODO. Every code step shows complete code. Task 4 Step 3 PR body is described, not templated, because exact wording is composed at push time from the (then-final) commit set — content requirements are enumerated explicitly.

**Type consistency:** `FavorScale.FloorOf/CeilingOf` return `double?` consistently; consumers coalesce with `?? 0` / `?? double.NegativeInfinity` / `?? currentFavor.Value` as shown per-line. `FavorTier?` from `GetFavorTier` is `.Value`-unwrapped at every Smaug call site (Tasks 3.4, 3.5) and `?? FavorTier.Neutral`-defaulted where a non-null tier is needed (3.3). `RepresentativeFavor` returns `double` (non-null) and is used wherever the old `FavorTiers.FloorOf` (int) seeded `currentFavor`. `.DisplayName()`/`.ToToken()` are the canonical `FavorTierExtensions` members. `FavorTierName` is fully removed (Task 4 Step 2 guards it).
