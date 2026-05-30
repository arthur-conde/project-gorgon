# Map Calibration Gate Study Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Build a throwaway `tools/MapCalibrationStudy` console tool that measures whether PG's per-area map renderer is regular enough (rotation∈{0,π}, isotropy, consistent inset, cold-correspondence) to justify a near-zero-ref auto-calibration engine, and records a go/no-go verdict.

**Architecture:** A small isolated console tool with two subcommands — `measure` (Half A: geometric-consistency table) and `bootstrap` (Half B: zero-prior correspondence proof). All decision math lives in **pure, unit-tested** classes (`OrientationClass`, `InsetMetrics`, `AffineFit`, `OutlierGuard`, `ColdBootstrap`); `Program.cs` is thin IO glue (readers, NCC detection, CSV/markdown emit) and is manually verified, not unit-tested. Reuses the shipped solver + detectors verbatim.

**Tech Stack:** C# / .NET 10 (`net10.0-windows`), xUnit + FluentAssertions, `Mithril.MapCalibration` (solver + `AreaCalibration`), `Mithril.MapCalibration.Tools.Common` (`NccTemplateMatch`, `IconTemplateExtractor`, `ImageIo`, `LandmarksReader`, `NpcsReader`, `MapRectLocator`, `MapTextureExtractor`).

**Spec:** [`docs/superpowers/specs/2026-05-30-map-calibration-gate-study-design.md`](../specs/2026-05-30-map-calibration-gate-study-design.md) · **Issue:** [mithril#897](https://github.com/moumantai-gg/mithril/issues/897)

> **Build/test note:** the repo's `check-mithril-running.ps1` PreToolUse hook blocks `dotnet build/test` while the Mithril shell is open. Close Mithril before running any build/test step below.
>
> **Throwaway:** this tool is deleted once the verdict is recorded (separate delete PR per the squash-merge-orphans rule). Do not wire it into the shell or any module.

---

## File structure

| File | Responsibility |
|---|---|
| `tools/MapCalibrationStudy/MapCalibrationStudy.csproj` | Isolated Exe; opts out of central PM + Directory.Build.targets (sibling-tool convention). |
| `tools/MapCalibrationStudy/Program.cs` | Arg parse, `measure`/`bootstrap` subcommands, readers, NCC detection wiring, CSV/markdown emit. IO glue; not unit-tested. |
| `tools/MapCalibrationStudy/OrientationClass.cs` | **(H1)** Classify a rotation (radians) to the discrete set {0, π}; report deviation. Pure. |
| `tools/MapCalibrationStudy/InsetMetrics.cs` | **(H3)** From an `AreaCalibration` + world points + texture (W,H): predictedScale (X/Z), scaleRatio, per-edge insetFrac. Pure. |
| `tools/MapCalibrationStudy/AffineFit.cs` | **(H2)** 6-param affine LSQ over (world↔pixel) pairs → RMS; plus similarity RMS for comparison. Pure. |
| `tools/MapCalibrationStudy/OutlierGuard.cs` | **(H4)** One-axis-large-residual rejection over solver references. Pure. |
| `tools/MapCalibrationStudy/ColdBootstrap.cs` | **(H4)** Enumerate 4 axis-aligned orientations, correspond predicted→nearest-detected, outlier-guarded solve; return refined cal + pairing. Pure. |
| `tools/MapCalibrationStudy/StudyRecord.cs` | Per-area record + CSV + markdown-table writers. Pure. |
| `tests/MapCalibrationStudy.Tests/MapCalibrationStudy.Tests.csproj` | xUnit test project; ProjectReferences the tool. Added to `Mithril.slnx`. |
| `tests/MapCalibrationStudy.Tests/*Tests.cs` | Unit tests for the five pure classes + StudyRecord. |

**Type reference (already shipped — do not redefine):**
- `Mithril.MapCalibration.WorldCoord` — `record struct WorldCoord(double X, double Y, double Z)`, `WorldCoord.TryParse(string)`.
- `Mithril.MapCalibration.PixelPoint` — `record struct PixelPoint(double X, double Y)`.
- `Mithril.MapCalibration.AreaCalibration` — `Scale`, `RotationRadians`, `OriginX`, `OriginY`, `MirrorNorth`, `ResidualPixels`, `WorldToWindow(WorldCoord)`.
- `Mithril.MapCalibration.LandmarkCalibrationSolver.Reference(double WorldX, double WorldZ, PixelPoint Pixel)` and `LandmarkCalibrationSolver.Solve(IReadOnlyList<Reference>) → AreaCalibration?`.
- `Mithril.Tools.MapCalibration.Common.LandmarkRef(string Type, string Name, WorldCoord World)`; `LandmarksReader.LoadForArea(path, area)`, `NpcsReader.LoadForArea(path, area)`.
- `Mithril.Tools.MapCalibration.Common.IconTemplateExtractor.Load(iconsDir) → IconIndex{ List<IconMeta> Icons }`; `IconMeta(Name, File, Width, Height, PivotX, PivotY, LandmarkType)`.
- `Mithril.Tools.MapCalibration.Common.NccTemplateMatch.FindAll(GrayImage image, GrayImage template, GrayImage? mask, double minScore, int? maxResults)`; `Detection` has `.Centre(int tw, int th) → (double Cx, double Cy)`.
- `Mithril.Tools.MapCalibration.Common.MapRect(int OriginX, int OriginY, int Width, int Height, int TextureWidth, int TextureHeight, …)` with `.ScreenshotToTexture(sx, sy) → (Tx, Ty)`; `MapRectLocator.AutoDetect(screenshot, texture, minScore)`.
- `Mithril.Tools.MapCalibration.Common.ImageIo` — `LoadGray`, `LoadGrayAndAlpha`, `LoadBgra`.

---

## Task 1: Scaffold the tool + test project (build green)

**Files:**
- Create: `tools/MapCalibrationStudy/MapCalibrationStudy.csproj`
- Create: `tools/MapCalibrationStudy/Program.cs`
- Create: `tests/MapCalibrationStudy.Tests/MapCalibrationStudy.Tests.csproj`
- Create: `tests/MapCalibrationStudy.Tests/ScaffoldTests.cs`

- [ ] **Step 1: Create the tool csproj** (mirrors `tools/MapCalibrationFromScreenshot`)

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <OutputType>Exe</OutputType>
    <TargetFramework>net10.0-windows</TargetFramework>
    <RootNamespace>Mithril.Tools.MapCalibrationStudy</RootNamespace>
    <IsPackable>false</IsPackable>
    <Nullable>enable</Nullable>
    <ImplicitUsings>enable</ImplicitUsings>
    <!-- Throwaway gate-study tool (mithril#897), deleted once the verdict lands.
         Same isolation pattern as tools/MapCalibrationFromScreenshot: out of
         central PM + Directory.Build.targets to avoid the versionless analyzer /
         GitVersioning clash. -->
    <ManagePackageVersionsCentrally>false</ManagePackageVersionsCentrally>
    <ImportDirectoryBuildTargets>false</ImportDirectoryBuildTargets>
    <!-- System.Drawing image IO + registry reads — Windows-only. -->
    <NoWarn>$(NoWarn);CA1416</NoWarn>
  </PropertyGroup>

  <ItemGroup>
    <PackageReference Include="AssetsTools.NET" Version="3.0.4" />
    <PackageReference Include="AssetsTools.NET.Texture" Version="3.0.2" />
    <PackageReference Include="System.Drawing.Common" Version="9.0.0" />
  </ItemGroup>

  <ItemGroup>
    <ProjectReference Include="..\..\src\Mithril.MapCalibration\Mithril.MapCalibration.csproj" />
    <ProjectReference Include="..\Mithril.MapCalibration.Tools.Common\Mithril.MapCalibration.Tools.Common.csproj" />
  </ItemGroup>
</Project>
```

- [ ] **Step 2: Create a minimal `Program.cs`** so the Exe builds

```csharp
namespace Mithril.Tools.MapCalibrationStudy;

internal static class Program
{
    private static int Main(string[] args)
    {
        if (args.Length == 0)
        {
            Console.Error.WriteLine("usage: MapCalibrationStudy <measure|bootstrap> [options]");
            return 1;
        }
        // Subcommand dispatch is implemented in Task 7. Scaffolding only here.
        Console.WriteLine($"MapCalibrationStudy: '{args[0]}' (not yet implemented)");
        return 0;
    }
}
```

- [ ] **Step 3: Create the test csproj** (mirrors `tests/Mithril.MapCalibration.Harness.Tests`)

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net10.0-windows</TargetFramework>
    <IsPackable>false</IsPackable>
    <RootNamespace>Mithril.Tools.MapCalibrationStudy.Tests</RootNamespace>
  </PropertyGroup>
  <ItemGroup>
    <PackageReference Include="Microsoft.NET.Test.Sdk" />
    <PackageReference Include="xunit" />
    <PackageReference Include="xunit.runner.visualstudio" />
    <PackageReference Include="FluentAssertions" />
  </ItemGroup>
  <ItemGroup>
    <!-- Cross-boundary ProjectReference into the isolated tool Exe is fine; the
         tool's PM/targets opt-out is build-local (same as the harness tests). -->
    <ProjectReference Include="..\..\tools\MapCalibrationStudy\MapCalibrationStudy.csproj" />
  </ItemGroup>
</Project>
```

- [ ] **Step 4: Write a trivial scaffold test**

`Program` is `internal`, so the test can't reference a tool type yet (no public type exists until Task 2). Assert a trivial truth now; Task 2 Step 4 rewrites this to reference the first public type (`OrientationClass`) and prove the cross-project reference resolves.

```csharp
using FluentAssertions;
using Xunit;

namespace Mithril.Tools.MapCalibrationStudy.Tests;

public class ScaffoldTests
{
    [Fact]
    public void Test_project_builds_and_runs()
    {
        true.Should().BeTrue();
    }
}
```

- [ ] **Step 5: Add the test project to the solution**

Run: `dotnet sln Mithril.slnx add tests/MapCalibrationStudy.Tests/MapCalibrationStudy.Tests.csproj`
Expected: `Project ... added to the solution.`

- [ ] **Step 6: Build + run the scaffold test**

Run: `dotnet test tests/MapCalibrationStudy.Tests/MapCalibrationStudy.Tests.csproj -v q`
Expected: PASS (1 test). If the slnx build later objects to the opted-out Exe transitive reference, the test project still builds and runs standalone via this path — keep using the project-path form for test steps.

- [ ] **Step 7: Commit**

```bash
git add tools/MapCalibrationStudy tests/MapCalibrationStudy.Tests Mithril.slnx
git commit -m "feat(map-calibration-study): scaffold throwaway gate-study tool + tests (#897)"
```

---

## Task 2: OrientationClass — H1 discrete-orientation classifier

**Files:**
- Create: `tools/MapCalibrationStudy/OrientationClass.cs`
- Create: `tests/MapCalibrationStudy.Tests/OrientationClassTests.cs`

- [ ] **Step 1: Write the failing tests**

```csharp
using FluentAssertions;
using Mithril.Tools.MapCalibrationStudy;
using Xunit;

namespace Mithril.Tools.MapCalibrationStudy.Tests;

public class OrientationClassTests
{
    [Theory]
    [InlineData(0.00033359980485819676, 0)]     // AreaSerbule
    [InlineData(5.863329159379195e-06, 0)]       // AreaCave1
    [InlineData(-3.141529626160087, 180)]        // AreaEltibule ≈ -π
    [InlineData(3.1415789203621567, 180)]        // AreaKurMountains ≈ +π
    public void Classifies_to_nearest_axis_member(double radians, int expectedDeg)
    {
        OrientationClass.Classify(radians).NearestDeg.Should().Be(expectedDeg);
    }

    [Fact]
    public void Reports_small_deviation_for_on_axis_rotation()
    {
        var c = OrientationClass.Classify(0.00033359980485819676);
        c.DeviationDeg.Should().BeLessThan(0.05);
        c.InSet.Should().BeTrue();   // within tolerance of a member
    }

    [Fact]
    public void Flags_an_in_between_angle_as_out_of_set()
    {
        var c = OrientationClass.Classify(Math.PI / 4); // 45° — neither 0 nor 180
        c.NearestDeg.Should().Be(0);                    // 45 is closer to 0 than 180
        c.DeviationDeg.Should().BeApproximately(45, 0.01);
        c.InSet.Should().BeFalse();
    }
}
```

- [ ] **Step 2: Run to verify failure**

Run: `dotnet test tests/MapCalibrationStudy.Tests/MapCalibrationStudy.Tests.csproj --filter "FullyQualifiedName~OrientationClassTests" -v q`
Expected: FAIL (does not compile — `OrientationClass` not defined).

- [ ] **Step 3: Implement**

```csharp
namespace Mithril.Tools.MapCalibrationStudy;

/// <summary>
/// (H1) Classifies a solved rotation against the discrete axis-aligned set the
/// renderer is hypothesised to use: {0°, 180°}. A π rotation is a real
/// orientation flip, not drift; an in-between angle (large deviation) would
/// falsify the hypothesis.
/// </summary>
public static class OrientationClass
{
    /// <summary>Default tolerance for membership, in degrees (per the §6 H1 gate).</summary>
    public const double DefaultToleranceDeg = 0.1;

    public readonly record struct Result(int NearestDeg, double DeviationDeg, bool InSet);

    public static Result Classify(double radians, double toleranceDeg = DefaultToleranceDeg)
    {
        var deg = radians * 180.0 / Math.PI;
        // Normalise to (-180, 180].
        deg %= 360.0;
        if (deg > 180.0) deg -= 360.0;
        if (deg <= -180.0) deg += 360.0;

        // Distance to 0 vs ±180 (same magnitude either sign).
        var dTo0 = Math.Abs(deg);
        var dTo180 = Math.Abs(Math.Abs(deg) - 180.0);
        var (nearest, dev) = dTo0 <= dTo180 ? (0, dTo0) : (180, dTo180);
        return new Result(nearest, dev, dev <= toleranceDeg);
    }
}
```

- [ ] **Step 4: Run to verify pass**

Run: `dotnet test tests/MapCalibrationStudy.Tests/MapCalibrationStudy.Tests.csproj --filter "FullyQualifiedName~OrientationClassTests" -v q`
Expected: PASS (6 cases). Also fix the Task-1 `ScaffoldTests` assertion to `typeof(OrientationClass).Assembly.Should().NotBeNull();` now that a public type exists.

- [ ] **Step 5: Commit**

```bash
git add tools/MapCalibrationStudy/OrientationClass.cs tests/MapCalibrationStudy.Tests/OrientationClassTests.cs tests/MapCalibrationStudy.Tests/ScaffoldTests.cs
git commit -m "feat(map-calibration-study): H1 discrete-orientation classifier (#897)"
```

---

## Task 3: InsetMetrics — H3 scale/inset measurement

**Files:**
- Create: `tools/MapCalibrationStudy/InsetMetrics.cs`
- Create: `tests/MapCalibrationStudy.Tests/InsetMetricsTests.cs`

- [ ] **Step 1: Write the failing test**

The test builds a known similarity (scale `s`, no rotation, no mirror, origin `(ox,oy)`) so the world→pixel mapping is exact, then asserts the metrics recover it. With **no inset**, a world bbox of span `W/s` maps to exactly the full texture width, so `predictedScaleX == s` and `insetFrac == 0`.

```csharp
using FluentAssertions;
using Mithril.MapCalibration;
using Mithril.Tools.MapCalibrationStudy;
using Xunit;

namespace Mithril.Tools.MapCalibrationStudy.Tests;

public class InsetMetricsTests
{
    [Fact]
    public void No_inset_when_world_bbox_fills_texture()
    {
        const double s = 2.0;
        const int texW = 1000, texH = 800;
        // World bbox chosen so s*span == texture dim: spanX = 500, spanZ = 400.
        // Place 4 corner landmarks; origin maps world(0,0) → pixel(0,0).
        var cal = new AreaCalibration(s, 0.0, 0.0, 0.0, 4, 0.0);
        var world = new[]
        {
            new WorldCoord(0,   0, 0),
            new WorldCoord(500, 0, 0),
            new WorldCoord(0,   0, 400),
            new WorldCoord(500, 0, 400),
        };

        var m = InsetMetrics.Compute(cal, world, texW, texH);

        m.PredictedScaleX.Should().BeApproximately(s, 1e-9);
        m.PredictedScaleZ.Should().BeApproximately(s, 1e-9);
        m.ScaleRatioX.Should().BeApproximately(1.0, 1e-9);
        m.InsetFracMax.Should().BeApproximately(0.0, 1e-9);
    }

    [Fact]
    public void Detects_a_uniform_border_inset()
    {
        // Same world span, but the texture is 10% larger on each side than the
        // projected bbox → the solved scale is smaller than texture/span, and
        // the inset fraction is ~0.1 per edge in the larger dimension.
        const double s = 2.0;
        const int texW = 1200, texH = 960;     // 1000x800 content + 10% margin each side
        var cal = new AreaCalibration(s, 0.0, 100.0, 80.0, 4, 0.0); // origin offset = the inset
        var world = new[]
        {
            new WorldCoord(0,   0, 0),
            new WorldCoord(500, 0, 0),
            new WorldCoord(0,   0, 400),
            new WorldCoord(500, 0, 400),
        };

        var m = InsetMetrics.Compute(cal, world, texW, texH);

        // predictedScale assumes no inset: texW/spanX = 1200/500 = 2.4 > s.
        m.PredictedScaleX.Should().BeApproximately(2.4, 1e-9);
        m.ScaleRatioX.Should().BeApproximately(s / 2.4, 1e-9);
        m.InsetFracMax.Should().BeApproximately(100.0 / 1200.0, 1e-6); // left margin / texW
    }
}
```

- [ ] **Step 2: Run to verify failure**

Run: `dotnet test tests/MapCalibrationStudy.Tests/MapCalibrationStudy.Tests.csproj --filter "FullyQualifiedName~InsetMetricsTests" -v q`
Expected: FAIL (compile — `InsetMetrics` not defined).

- [ ] **Step 3: Implement**

```csharp
using Mithril.MapCalibration;

namespace Mithril.Tools.MapCalibrationStudy;

/// <summary>
/// (H3) Measures how the landmark world bounding-box projects into the texture:
/// the scale you would predict assuming the bbox fills the texture
/// (texture_dim / world_span), the ratio of that to the solved scale, and the
/// fractional margin (inset) between the projected bbox and the texture edges.
/// A constant inset fraction across areas is what makes scale computable cold.
/// </summary>
public static class InsetMetrics
{
    public readonly record struct Result(
        double PredictedScaleX,
        double PredictedScaleZ,
        double ScaleRatioX,
        double ScaleRatioZ,
        double InsetFracLeft,
        double InsetFracRight,
        double InsetFracTop,
        double InsetFracBottom)
    {
        public double InsetFracMax =>
            Math.Max(Math.Max(InsetFracLeft, InsetFracRight),
                     Math.Max(InsetFracTop, InsetFracBottom));
    }

    public static Result Compute(
        AreaCalibration cal, IReadOnlyList<WorldCoord> world, int textureW, int textureH)
    {
        if (world.Count < 2)
            throw new ArgumentException("need >= 2 world points for a bbox", nameof(world));

        double minX = double.MaxValue, maxX = double.MinValue;
        double minZ = double.MaxValue, maxZ = double.MinValue;
        double pMinX = double.MaxValue, pMaxX = double.MinValue;
        double pMinY = double.MaxValue, pMaxY = double.MinValue;
        foreach (var w in world)
        {
            minX = Math.Min(minX, w.X); maxX = Math.Max(maxX, w.X);
            minZ = Math.Min(minZ, w.Z); maxZ = Math.Max(maxZ, w.Z);
            var p = cal.WorldToWindow(w);
            pMinX = Math.Min(pMinX, p.X); pMaxX = Math.Max(pMaxX, p.X);
            pMinY = Math.Min(pMinY, p.Y); pMaxY = Math.Max(pMaxY, p.Y);
        }

        var spanX = maxX - minX;
        var spanZ = maxZ - minZ;
        var predX = spanX > 1e-9 ? textureW / spanX : 0.0;
        var predZ = spanZ > 1e-9 ? textureH / spanZ : 0.0;

        return new Result(
            PredictedScaleX: predX,
            PredictedScaleZ: predZ,
            ScaleRatioX: predX > 1e-9 ? cal.Scale / predX : 0.0,
            ScaleRatioZ: predZ > 1e-9 ? cal.Scale / predZ : 0.0,
            InsetFracLeft: pMinX / textureW,
            InsetFracRight: (textureW - pMaxX) / textureW,
            InsetFracTop: pMinY / textureH,
            InsetFracBottom: (textureH - pMaxY) / textureH);
    }
}
```

- [ ] **Step 4: Run to verify pass**

Run: `dotnet test tests/MapCalibrationStudy.Tests/MapCalibrationStudy.Tests.csproj --filter "FullyQualifiedName~InsetMetricsTests" -v q`
Expected: PASS (2 tests).

- [ ] **Step 5: Commit**

```bash
git add tools/MapCalibrationStudy/InsetMetrics.cs tests/MapCalibrationStudy.Tests/InsetMetricsTests.cs
git commit -m "feat(map-calibration-study): H3 scale/inset metrics (#897)"
```

---

## Task 4: AffineFit — H2 isotropy check

**Files:**
- Create: `tools/MapCalibrationStudy/AffineFit.cs`
- Create: `tests/MapCalibrationStudy.Tests/AffineFitTests.cs`

- [ ] **Step 1: Write the failing test**

A 6-param affine has strictly more freedom than a 4-param similarity, so on points generated from a pure similarity the affine RMS must be ≈0 (and ≤ similarity RMS). The H2 hypothesis is that on *real* data the affine win stays negligible.

```csharp
using FluentAssertions;
using Mithril.Tools.MapCalibrationStudy;
using Xunit;

namespace Mithril.Tools.MapCalibrationStudy.Tests;

public class AffineFitTests
{
    private static (double wx, double wz, double px, double py) P(
        double wx, double wz, double s, double rot, double ox, double oy)
    {
        var cos = Math.Cos(rot); var sin = Math.Sin(rot);
        var rotE = wx * cos + wz * sin;
        var rotN = -wx * sin + wz * cos;
        return (wx, wz, ox + s * rotE, oy - s * rotN);
    }

    [Fact]
    public void Affine_fits_a_pure_similarity_with_near_zero_residual()
    {
        const double s = 1.7, rot = 0.0, ox = 12, oy = -5;
        var pts = new[]
        {
            P(0, 0, s, rot, ox, oy),
            P(100, 0, s, rot, ox, oy),
            P(0, 80, s, rot, ox, oy),
            P(100, 80, s, rot, ox, oy),
            P(40, 30, s, rot, ox, oy),
        };

        AffineFit.Rms(pts).Should().BeLessThan(1e-6);
    }

    [Fact]
    public void Affine_rms_never_exceeds_similarity_rms()
    {
        // Perturb one point so neither model is exact; affine (more DOF) must
        // still fit at least as tightly as the similarity.
        const double s = 1.7, rot = 0.0, ox = 12, oy = -5;
        var pts = new[]
        {
            P(0, 0, s, rot, ox, oy),
            P(100, 0, s, rot, ox, oy),
            P(0, 80, s, rot, ox, oy),
            (wx: 100.0, wz: 80.0, px: 999.0, py: -999.0), // outlier
            P(40, 30, s, rot, ox, oy),
        };

        AffineFit.Rms(pts).Should().BeLessThanOrEqualTo(AffineFit.SimilarityRms(pts) + 1e-9);
    }
}
```

- [ ] **Step 2: Run to verify failure**

Run: `dotnet test tests/MapCalibrationStudy.Tests/MapCalibrationStudy.Tests.csproj --filter "FullyQualifiedName~AffineFitTests" -v q`
Expected: FAIL (compile — `AffineFit` not defined).

- [ ] **Step 3: Implement**

```csharp
using Mithril.MapCalibration;

namespace Mithril.Tools.MapCalibrationStudy;

/// <summary>
/// (H2) Isotropy check. Fits a full 6-parameter affine
/// (px = a·wx + b·wz + c; py = d·wx + e·wz + f) by least squares and reports
/// its RMS pixel residual, alongside the constrained 4-param similarity RMS
/// (via the shipped <see cref="LandmarkCalibrationSolver"/>). If the affine
/// barely beats the similarity on real data, the renderer is isotropic.
/// </summary>
public static class AffineFit
{
    public static double Rms(IReadOnlyList<(double wx, double wz, double px, double py)> pts)
    {
        if (pts.Count < 3) throw new ArgumentException("affine needs >= 3 points", nameof(pts));
        // Two independent 3-param normal-equation solves (X then Y) on basis
        // [wx, wz, 1]. Symmetric 3x3 system; solved by Cramer's rule.
        var (ax, bx, cx) = Solve3(pts, forX: true);
        var (ay, by, cy) = Solve3(pts, forX: false);
        double sumSq = 0;
        foreach (var (wx, wz, px, py) in pts)
        {
            var ex = ax * wx + bx * wz + cx - px;
            var ey = ay * wx + by * wz + cy - py;
            sumSq += ex * ex + ey * ey;
        }
        return Math.Sqrt(sumSq / pts.Count);
    }

    public static double SimilarityRms(IReadOnlyList<(double wx, double wz, double px, double py)> pts)
    {
        var refs = pts
            .Select(p => new LandmarkCalibrationSolver.Reference(p.wx, p.wz, new PixelPoint(p.px, p.py)))
            .ToList();
        var cal = LandmarkCalibrationSolver.Solve(refs);
        return cal?.ResidualPixels ?? double.PositiveInfinity;
    }

    private static (double a, double b, double c) Solve3(
        IReadOnlyList<(double wx, double wz, double px, double py)> pts, bool forX)
    {
        // Normal equations for minimising Σ (a·wx + b·wz + c − t)².
        double Sxx = 0, Sxz = 0, Sx1 = 0, Szz = 0, Sz1 = 0, S11 = 0;
        double Sxt = 0, Szt = 0, S1t = 0;
        foreach (var (wx, wz, px, py) in pts)
        {
            var t = forX ? px : py;
            Sxx += wx * wx; Sxz += wx * wz; Sx1 += wx;
            Szz += wz * wz; Sz1 += wz; S11 += 1;
            Sxt += wx * t; Szt += wz * t; S1t += t;
        }
        // 3x3 symmetric matrix M = [[Sxx,Sxz,Sx1],[Sxz,Szz,Sz1],[Sx1,Sz1,S11]],
        // rhs = [Sxt,Szt,S1t]. Cramer's rule.
        double Det(double a, double b, double c, double d, double e, double f, double g, double h, double i)
            => a * (e * i - f * h) - b * (d * i - f * g) + c * (d * h - e * g);

        var det = Det(Sxx, Sxz, Sx1, Sxz, Szz, Sz1, Sx1, Sz1, S11);
        if (Math.Abs(det) < 1e-12) return (0, 0, S1t / Math.Max(1, S11)); // degenerate fallback
        var a = Det(Sxt, Sxz, Sx1, Szt, Szz, Sz1, S1t, Sz1, S11) / det;
        var b = Det(Sxx, Sxt, Sx1, Sxz, Szt, Sz1, Sx1, S1t, S11) / det;
        var c = Det(Sxx, Sxz, Sxt, Sxz, Szz, Szt, Sx1, Sz1, S1t) / det;
        return (a, b, c);
    }
}
```

- [ ] **Step 4: Run to verify pass**

Run: `dotnet test tests/MapCalibrationStudy.Tests/MapCalibrationStudy.Tests.csproj --filter "FullyQualifiedName~AffineFitTests" -v q`
Expected: PASS (2 tests).

- [ ] **Step 5: Commit**

```bash
git add tools/MapCalibrationStudy/AffineFit.cs tests/MapCalibrationStudy.Tests/AffineFitTests.cs
git commit -m "feat(map-calibration-study): H2 affine-vs-similarity isotropy check (#897)"
```

---

## Task 5: OutlierGuard — one-axis-residual rejection

**Files:**
- Create: `tools/MapCalibrationStudy/OutlierGuard.cs`
- Create: `tests/MapCalibrationStudy.Tests/OutlierGuardTests.cs`

- [ ] **Step 1: Write the failing test**

A bad ref whose residual is large in **one axis only** is a detection/transcription error, not a real offset. The guard solves, finds the worst per-axis-asymmetric residual above a threshold, drops it, and re-solves.

```csharp
using FluentAssertions;
using Mithril.MapCalibration;
using Mithril.Tools.MapCalibrationStudy;
using Xunit;

namespace Mithril.Tools.MapCalibrationStudy.Tests;

public class OutlierGuardTests
{
    [Fact]
    public void Drops_a_single_axis_outlier_and_improves_residual()
    {
        const double s = 2.0, ox = 10, oy = 20;
        PixelPoint Px(double wx, double wz, double dx = 0, double dy = 0)
            => new(ox + s * wx + dx, oy - s * wz + dy);

        var refs = new List<LandmarkCalibrationSolver.Reference>
        {
            new(0, 0, Px(0, 0)),
            new(50, 0, Px(50, 0)),
            new(0, 40, Px(0, 40)),
            new(50, 40, Px(50, 40)),
            new(25, 20, Px(25, 20, dx: 30, dy: 0)), // +30px in X only — the outlier
        };

        var kept = OutlierGuard.Reject(refs, axisThresholdPx: 8.0);

        kept.Should().HaveCount(4);
        var cal = LandmarkCalibrationSolver.Solve(kept);
        cal!.ResidualPixels.Should().BeLessThan(1e-6);
    }

    [Fact]
    public void Keeps_all_when_no_axis_asymmetric_outlier()
    {
        const double s = 2.0, ox = 10, oy = 20;
        PixelPoint Px(double wx, double wz) => new(ox + s * wx, oy - s * wz);
        var refs = new List<LandmarkCalibrationSolver.Reference>
        {
            new(0, 0, Px(0, 0)), new(50, 0, Px(50, 0)),
            new(0, 40, Px(0, 40)), new(50, 40, Px(50, 40)),
        };
        OutlierGuard.Reject(refs, axisThresholdPx: 8.0).Should().HaveCount(4);
    }
}
```

- [ ] **Step 2: Run to verify failure**

Run: `dotnet test tests/MapCalibrationStudy.Tests/MapCalibrationStudy.Tests.csproj --filter "FullyQualifiedName~OutlierGuardTests" -v q`
Expected: FAIL (compile — `OutlierGuard` not defined).

- [ ] **Step 3: Implement**

```csharp
using Mithril.MapCalibration;

namespace Mithril.Tools.MapCalibrationStudy;

/// <summary>
/// (H4 support) Rejects references whose fit residual is large in a single axis
/// only — the signature of a detection/transcription error rather than a real
/// map offset (a genuine offset shows up roughly isotropically). Iteratively
/// solves, finds the worst single-axis-asymmetric residual above the threshold,
/// drops it, and re-solves until none remain or too few refs survive.
/// </summary>
public static class OutlierGuard
{
    public static List<LandmarkCalibrationSolver.Reference> Reject(
        IReadOnlyList<LandmarkCalibrationSolver.Reference> refs, double axisThresholdPx)
    {
        var live = refs.ToList();
        while (live.Count > 3)
        {
            var cal = LandmarkCalibrationSolver.Solve(live);
            if (cal is null) break;

            int worst = -1;
            double worstAsymmetry = axisThresholdPx;
            for (var i = 0; i < live.Count; i++)
            {
                var p = cal.WorldToWindow(new WorldCoord(live[i].WorldX, 0, live[i].WorldZ));
                var dx = Math.Abs(p.X - live[i].Pixel.X);
                var dy = Math.Abs(p.Y - live[i].Pixel.Y);
                var maxAxis = Math.Max(dx, dy);
                var minAxis = Math.Min(dx, dy);
                // "One axis only" = the larger axis error clears the threshold
                // while the other stays small. Asymmetry = maxAxis - minAxis.
                var asymmetry = maxAxis - minAxis;
                if (maxAxis >= axisThresholdPx && asymmetry > worstAsymmetry)
                {
                    worstAsymmetry = asymmetry;
                    worst = i;
                }
            }

            if (worst < 0) break;
            live.RemoveAt(worst);
        }
        return live;
    }
}
```

- [ ] **Step 4: Run to verify pass**

Run: `dotnet test tests/MapCalibrationStudy.Tests/MapCalibrationStudy.Tests.csproj --filter "FullyQualifiedName~OutlierGuardTests" -v q`
Expected: PASS (2 tests).

- [ ] **Step 5: Commit**

```bash
git add tools/MapCalibrationStudy/OutlierGuard.cs tests/MapCalibrationStudy.Tests/OutlierGuardTests.cs
git commit -m "feat(map-calibration-study): one-axis-residual outlier guard (#897)"
```

---

## Task 6: ColdBootstrap — H4 zero-prior correspondence + solve

**Files:**
- Create: `tools/MapCalibrationStudy/ColdBootstrap.cs`
- Create: `tests/MapCalibrationStudy.Tests/ColdBootstrapTests.cs`

- [ ] **Step 1: Write the failing test**

Given world landmarks and a set of **detected icon texture pixels** generated from a known transform (one of the 4 axis-aligned orientations), the bootstrap must, with zero priors, recover that orientation, correspond predicted→detected correctly, and solve to near-zero residual — even with a one-axis outlier detection injected.

```csharp
using FluentAssertions;
using Mithril.MapCalibration;
using Mithril.Tools.MapCalibrationStudy;
using Xunit;

namespace Mithril.Tools.MapCalibrationStudy.Tests;

public class ColdBootstrapTests
{
    // Known ground-truth transform for the synthetic area: scale, origin, and a
    // reflection (mirrorX/mirrorZ) — exercises orientation recovery.
    private static PixelPoint Project(WorldCoord w, double s, double ox, double oy, bool mirrorX, bool mirrorZ)
    {
        var x = mirrorX ? -w.X : w.X;
        var z = mirrorZ ? -w.Z : w.Z;
        return new PixelPoint(ox + s * x, oy - s * z);
    }

    [Fact]
    public void Recovers_orientation_correspondence_and_transform_cold()
    {
        const double s = 1.5, ox = 600, oy = 700;
        const bool mirrorX = true, mirrorZ = false; // a non-trivial orientation
        var world = new List<WorldCoord>
        {
            new(0, 0, 0), new(200, 0, 0), new(0, 0, 150),
            new(200, 0, 150), new(90, 0, 60), new(40, 0, 120),
        };
        // Detected icons = ground-truth projections, shuffled (detection has no
        // landmark identity — pairing must be inferred by geometry).
        var detected = world.Select(w => Project(w, s, ox, oy, mirrorX, mirrorZ)).ToList();
        var shuffled = new List<PixelPoint> { detected[3], detected[0], detected[5], detected[1], detected[4], detected[2] };

        var result = ColdBootstrap.Run(world, shuffled, textureW: 1280, textureH: 1024, axisThresholdPx: 8.0);

        result.Should().NotBeNull();
        result!.RefinedResidualPx.Should().BeLessThan(1.0);
        result.CorrespondedCount.Should().Be(6);
    }

    [Fact]
    public void Survives_a_one_axis_outlier_detection()
    {
        const double s = 1.5, ox = 600, oy = 700;
        var world = new List<WorldCoord>
        {
            new(0, 0, 0), new(200, 0, 0), new(0, 0, 150),
            new(200, 0, 150), new(90, 0, 60), new(40, 0, 120),
        };
        var detected = world.Select(w => Project(w, s, ox, oy, mirrorX: false, mirrorZ: false)).ToList();
        detected[4] = new PixelPoint(detected[4].X + 40, detected[4].Y); // +40px X only

        var result = ColdBootstrap.Run(world, detected, textureW: 1280, textureH: 1024, axisThresholdPx: 8.0);

        result.Should().NotBeNull();
        result!.RefinedResidualPx.Should().BeLessThan(1.0); // outlier dropped
    }
}
```

- [ ] **Step 2: Run to verify failure**

Run: `dotnet test tests/MapCalibrationStudy.Tests/MapCalibrationStudy.Tests.csproj --filter "FullyQualifiedName~ColdBootstrapTests" -v q`
Expected: FAIL (compile — `ColdBootstrap` not defined).

- [ ] **Step 3: Implement**

```csharp
using Mithril.MapCalibration;

namespace Mithril.Tools.MapCalibrationStudy;

/// <summary>
/// (H4) The decisive proof: from ONLY world landmark coordinates + texture
/// dimensions + detected icon pixels (no stored calibration, no manual clicks),
/// recover the renderer transform. Enumerates the 4 axis-aligned orientation
/// states (±X × ±Z), estimates a rough scale-from-bbox per state, corresponds
/// each predicted landmark to its nearest detected icon, solves via the shipped
/// similarity solver (which re-confirms handedness), and keeps the best-fitting
/// orientation after an outlier-guard pass. Informational prototype of the
/// engine's correspondence step — not the engine itself.
/// </summary>
public static class ColdBootstrap
{
    public sealed record Result(
        AreaCalibration Calibration,
        double RefinedResidualPx,
        int CorrespondedCount,
        bool MirrorX,
        bool MirrorZ);

    public static Result? Run(
        IReadOnlyList<WorldCoord> world,
        IReadOnlyList<PixelPoint> detected,
        int textureW,
        int textureH,
        double axisThresholdPx)
    {
        if (world.Count < 3 || detected.Count < 3) return null;

        double minX = world.Min(w => w.X), maxX = world.Max(w => w.X);
        double minZ = world.Min(w => w.Z), maxZ = world.Max(w => w.Z);
        var spanX = Math.Max(1e-9, maxX - minX);
        var spanZ = Math.Max(1e-9, maxZ - minZ);
        // Rough isotropic scale: the smaller predicted scale avoids over-shooting
        // when one axis is inset more than the other.
        var scale0 = Math.Min(textureW / spanX, textureH / spanZ);

        Result? best = null;
        foreach (var mirrorX in new[] { false, true })
        foreach (var mirrorZ in new[] { false, true })
        {
            // Predict each landmark's texture pixel under this orientation +
            // rough scale, centring the world bbox in the texture so nearest-
            // neighbour correspondence is meaningful before the real solve.
            var cxWorld = (minX + maxX) / 2.0;
            var czWorld = (minZ + maxZ) / 2.0;
            PixelPoint Predict(WorldCoord w)
            {
                var x = (mirrorX ? -1 : 1) * (w.X - cxWorld);
                var z = (mirrorZ ? -1 : 1) * (w.Z - czWorld);
                return new PixelPoint(textureW / 2.0 + scale0 * x, textureH / 2.0 - scale0 * z);
            }

            var refs = new List<LandmarkCalibrationSolver.Reference>();
            var used = new bool[detected.Count];
            var corresponded = 0;
            foreach (var w in world)
            {
                var pred = Predict(w);
                var bestIdx = -1; var bestD = double.MaxValue;
                for (var i = 0; i < detected.Count; i++)
                {
                    if (used[i]) continue;
                    var dx = detected[i].X - pred.X;
                    var dy = detected[i].Y - pred.Y;
                    var d = dx * dx + dy * dy;
                    if (d < bestD) { bestD = d; bestIdx = i; }
                }
                if (bestIdx < 0) continue;
                used[bestIdx] = true;
                corresponded++;
                refs.Add(new LandmarkCalibrationSolver.Reference(w.X, w.Z, detected[bestIdx]));
            }

            if (refs.Count < 3) continue;
            var kept = OutlierGuard.Reject(refs, axisThresholdPx);
            var cal = LandmarkCalibrationSolver.Solve(kept);
            if (cal is null) continue;

            if (best is null || cal.ResidualPixels < best.RefinedResidualPx)
                best = new Result(cal, cal.ResidualPixels, kept.Count, mirrorX, mirrorZ);
        }

        return best;
    }
}
```

- [ ] **Step 4: Run to verify pass**

Run: `dotnet test tests/MapCalibrationStudy.Tests/MapCalibrationStudy.Tests.csproj --filter "FullyQualifiedName~ColdBootstrapTests" -v q`
Expected: PASS (2 tests). If the orientation-recovery test is flaky on the nearest-neighbour pairing for tightly-clustered synthetic points, widen the world spread in the test (the real corpus has well-separated landmarks); do not loosen the residual assertion.

- [ ] **Step 5: Commit**

```bash
git add tools/MapCalibrationStudy/ColdBootstrap.cs tests/MapCalibrationStudy.Tests/ColdBootstrapTests.cs
git commit -m "feat(map-calibration-study): H4 cold-correspondence bootstrap (#897)"
```

---

## Task 7: StudyRecord — per-area record + table emit

**Files:**
- Create: `tools/MapCalibrationStudy/StudyRecord.cs`
- Create: `tests/MapCalibrationStudy.Tests/StudyRecordTests.cs`

- [ ] **Step 1: Write the failing test**

```csharp
using FluentAssertions;
using Mithril.Tools.MapCalibrationStudy;
using Xunit;

namespace Mithril.Tools.MapCalibrationStudy.Tests;

public class StudyRecordTests
{
    [Fact]
    public void Csv_has_header_and_one_row_per_record()
    {
        var rows = new[]
        {
            new StudyRecord("AreaSerbule", 0.019, 0, false, 0.385, 0.40, 0.96, 0.012, 0.30, 0.31, 6, true),
            new StudyRecord("AreaEltibule", -179.996, 180, false, 0.315, 0.33, 0.95, 0.011, 0.34, 0.35, 5, true),
        };
        var csv = StudyRecord.ToCsv(rows);
        var lines = csv.Trim().Split('\n');
        lines.Should().HaveCount(3); // header + 2
        lines[0].Should().Contain("area").And.Contain("rotationDeg").And.Contain("insetFracMax");
        lines[1].Should().StartWith("AreaSerbule,");
    }

    [Fact]
    public void Markdown_renders_a_table()
    {
        var rows = new[] { new StudyRecord("AreaCave1", 0.0003, 0, false, 0.42, 0.43, 0.98, 0.0, 0.05, 0.05, 4, true) };
        var md = StudyRecord.ToMarkdown(rows);
        md.Should().Contain("| area |").And.Contain("| AreaCave1 |");
    }
}
```

- [ ] **Step 2: Run to verify failure**

Run: `dotnet test tests/MapCalibrationStudy.Tests/MapCalibrationStudy.Tests.csproj --filter "FullyQualifiedName~StudyRecordTests" -v q`
Expected: FAIL (compile — `StudyRecord` not defined).

- [ ] **Step 3: Implement**

```csharp
using System.Globalization;
using System.Text;

namespace Mithril.Tools.MapCalibrationStudy;

/// <summary>
/// One area's row in the study table. Combines the Half-A geometric metrics
/// (rotation/handedness/scale/inset/affine) and the Half-B cold-bootstrap
/// outcome (corresponded count, refined residual). Emitted as CSV (machine)
/// and markdown (the wiki + notebook write-up).
/// </summary>
public sealed record StudyRecord(
    string Area,
    double RotationDeg,
    int OrientationDeg,
    bool MirrorNorth,
    double SolvedScale,
    double PredictedScaleX,
    double ScaleRatioX,
    double InsetFracMax,
    double SimilarityResidualPx,
    double AffineResidualPx,
    int BootstrapCorresponded,
    bool BootstrapPaired)
{
    private const string Header =
        "area,rotationDeg,orientationDeg,mirrorNorth,solvedScale,predictedScaleX,scaleRatioX,insetFracMax,similarityResidualPx,affineResidualPx,bootstrapCorresponded,bootstrapPaired";

    public static string ToCsv(IReadOnlyList<StudyRecord> rows)
    {
        var sb = new StringBuilder();
        sb.AppendLine(Header);
        foreach (var r in rows)
        {
            sb.AppendLine(string.Join(",", new[]
            {
                r.Area,
                F(r.RotationDeg), r.OrientationDeg.ToString(CultureInfo.InvariantCulture),
                r.MirrorNorth.ToString(), F(r.SolvedScale), F(r.PredictedScaleX), F(r.ScaleRatioX),
                F(r.InsetFracMax), F(r.SimilarityResidualPx), F(r.AffineResidualPx),
                r.BootstrapCorresponded.ToString(CultureInfo.InvariantCulture), r.BootstrapPaired.ToString(),
            }));
        }
        return sb.ToString();
    }

    public static string ToMarkdown(IReadOnlyList<StudyRecord> rows)
    {
        var sb = new StringBuilder();
        sb.AppendLine("| area | rotationDeg | orient | mirror | scale | predScaleX | ratioX | insetMax | simResid | affResid | corr | paired |");
        sb.AppendLine("|---|---|---|---|---|---|---|---|---|---|---|---|");
        foreach (var r in rows)
        {
            sb.AppendLine($"| {r.Area} | {F(r.RotationDeg)} | {r.OrientationDeg} | {r.MirrorNorth} | {F(r.SolvedScale)} | {F(r.PredictedScaleX)} | {F(r.ScaleRatioX)} | {F(r.InsetFracMax)} | {F(r.SimilarityResidualPx)} | {F(r.AffineResidualPx)} | {r.BootstrapCorresponded} | {r.BootstrapPaired} |");
        }
        return sb.ToString();
    }

    private static string F(double d) => d.ToString("0.#####", CultureInfo.InvariantCulture);
}
```

- [ ] **Step 4: Run to verify pass**

Run: `dotnet test tests/MapCalibrationStudy.Tests/MapCalibrationStudy.Tests.csproj --filter "FullyQualifiedName~StudyRecordTests" -v q`
Expected: PASS (2 tests).

- [ ] **Step 5: Commit**

```bash
git add tools/MapCalibrationStudy/StudyRecord.cs tests/MapCalibrationStudy.Tests/StudyRecordTests.cs
git commit -m "feat(map-calibration-study): per-area record + CSV/markdown emit (#897)"
```

---

## Task 8: Program — wire the `measure` + `bootstrap` subcommands (IO glue)

**Files:**
- Modify: `tools/MapCalibrationStudy/Program.cs`

This task is IO glue — readers, source-0 parsing, NCC detection, and table emit. It is **not** unit-tested (the pure pieces it orchestrates already are); it is verified by running against the real corpus in Task 9.

- [ ] **Step 1: Implement the full Program**

```csharp
using System.Text.Json;
using Mithril.MapCalibration;
using Mithril.Tools.MapCalibration.Common;

namespace Mithril.Tools.MapCalibrationStudy;

internal static class Program
{
    private static int Main(string[] args)
    {
        try
        {
            if (args.Length == 0) return Usage();
            return args[0] switch
            {
                "measure" => Measure(ParseOptions(args)),
                "bootstrap" => Bootstrap(ParseOptions(args)),
                _ => Usage(),
            };
        }
        catch (UserFacingException ex)
        {
            Console.Error.WriteLine($"error: {ex.Message}");
            return 2;
        }
    }

    private static int Usage()
    {
        Console.Error.WriteLine("""
            usage:
              MapCalibrationStudy measure   --refinements <refinements.json> --baseline <baseline.json>
                                            --landmarks <landmarks.json> --npcs <npcs.json>
                                            --textures <dir> --areas <A,B,C> --out <dir>
              MapCalibrationStudy bootstrap --screenshots <dir> --textures <dir> --icons <dir>
                                            --landmarks <landmarks.json> --npcs <npcs.json>
                                            --areas <A,B,C> --out <dir>
            """);
        return 1;
    }

    // ---- measure (Half A) --------------------------------------------------

    private static int Measure(Dictionary<string, string> o)
    {
        var areas = o["--areas"].Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var source0 = LoadRefinements(o["--refinements"]);   // overlay-frame: rotation + handedness only
        var rows = new List<StudyRecord>();

        foreach (var area in areas)
        {
            // Rotation/handedness come from whichever solve we have (source 0
            // is enough for those, frame-invariant).
            source0.TryGetValue(area, out var overlayCal);
            var rotationDeg = overlayCal is null ? double.NaN : overlayCal.RotationRadians * 180.0 / Math.PI;
            var orient = overlayCal is null ? -1 : OrientationClass.Classify(overlayCal.RotationRadians).NearestDeg;
            var mirror = overlayCal?.MirrorNorth ?? false;

            // Scale/inset/affine need a TEXTURE-frame solve (the committed
            // baseline) + the texture dims + the world points.
            var textureCal = BaselineFile.TryReadAnchor(o["--baseline"], area);
            double solvedScale = 0, predX = 0, ratioX = 0, insetMax = 0, simRms = 0, affRms = 0;
            if (textureCal is not null && TryTextureSize(o["--textures"], area, out var tw, out var th))
            {
                var world = LoadWorldPoints(o["--landmarks"], o["--npcs"], area).Select(l => l.World).ToList();
                var m = InsetMetrics.Compute(textureCal, world, tw, th);
                solvedScale = textureCal.Scale; predX = m.PredictedScaleX;
                ratioX = m.ScaleRatioX; insetMax = m.InsetFracMax;
                simRms = textureCal.ResidualPixels;
                // affine RMS needs the (world↔pixel) pairs the baseline was fit
                // on; we don't persist those, so re-project the world points
                // through the solved transform as the pixel side (affine then
                // measures self-consistency of the model, the H2 question).
                var pts = world.Select(w => { var p = textureCal.WorldToWindow(w); return (w.X, w.Z, p.X, p.Y); }).ToList();
                affRms = pts.Count >= 3 ? AffineFit.Rms(pts) : 0;
            }

            rows.Add(new StudyRecord(area, rotationDeg, orient, mirror,
                solvedScale, predX, ratioX, insetMax, simRms, affRms, 0, false));
        }

        Emit(o["--out"], "measure", rows);
        return 0;
    }

    // ---- bootstrap (Half B) ------------------------------------------------

    private static int Bootstrap(Dictionary<string, string> o)
    {
        var areas = o["--areas"].Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var icons = IconTemplateExtractor.Load(o["--icons"]);
        var rows = new List<StudyRecord>();

        foreach (var area in areas)
        {
            var shotPath = Path.Combine(o["--screenshots"], area + ".png");
            if (!File.Exists(shotPath)) { Console.WriteLine($"[skip] no screenshot for {area}"); continue; }
            if (!TryTextureSize(o["--textures"], area, out var tw, out var th)) { Console.WriteLine($"[skip] no texture for {area}"); continue; }

            var world = LoadWorldPoints(o["--landmarks"], o["--npcs"], area).Select(l => l.World).ToList();
            var detected = DetectIcons(shotPath, o["--textures"], area, o["--icons"], icons);
            if (detected.Count < 3) { Console.WriteLine($"[skip] <3 icons detected in {area}"); continue; }

            var result = ColdBootstrap.Run(world, detected, tw, th, axisThresholdPx: 8.0);
            if (result is null) { Console.WriteLine($"[skip] bootstrap returned null for {area}"); continue; }

            var orient = OrientationClass.Classify(result.Calibration.RotationRadians).NearestDeg;
            rows.Add(new StudyRecord(area,
                result.Calibration.RotationRadians * 180.0 / Math.PI, orient,
                result.Calibration.MirrorNorth, result.Calibration.Scale, 0, 0, 0,
                result.RefinedResidualPx, 0, result.CorrespondedCount, result.CorrespondedCount >= 3));
        }

        Emit(o["--out"], "bootstrap", rows);
        return 0;
    }

    // ---- detection (texture-frame icon pixels) -----------------------------

    private static List<PixelPoint> DetectIcons(
        string screenshotPath, string texturesDir, string area, string iconsDir, IconIndex icons)
    {
        var screen = ImageIo.LoadGray(screenshotPath);
        var texturePath = MapTextureExtractor.EnsureExtractedOrCached(texturesDir, area)
            ?? throw new UserFacingException($"no cached texture PNG for {area} in {texturesDir}");
        var texture = ImageIo.LoadGray(texturePath);
        var rect = MapRectLocator.AutoDetect(screen, texture, minScore: 0.30)
            ?? throw new UserFacingException($"could not locate the map rect in {Path.GetFileName(screenshotPath)} — is it zoomed fully out?");

        var pixels = new List<PixelPoint>();
        foreach (var meta in icons.Icons)
        {
            var (gray, alpha) = ImageIo.LoadGrayAndAlpha(Path.Combine(iconsDir, meta.File));
            var hits = NccTemplateMatch.FindAll(screen, gray, alpha, minScore: 0.5, maxResults: 64);
            foreach (var hit in hits)
            {
                var (cx, cy) = hit.Centre(meta.Width, meta.Height);
                // pivot-correct: anchor pixel = centre + (w*(pivot.x-0.5), h*(0.5-pivot.y))
                var ax = cx + meta.Width * (meta.PivotX - 0.5);
                var ay = cy + meta.Height * (0.5 - meta.PivotY);
                var (tx, ty) = rect.ScreenshotToTexture(ax, ay);
                pixels.Add(new PixelPoint(tx, ty));
            }
        }
        return pixels;
    }

    // ---- helpers -----------------------------------------------------------

    private static Dictionary<string, AreaCalibration> LoadRefinements(string path)
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(path));
        var map = new Dictionary<string, AreaCalibration>(StringComparer.Ordinal);
        if (!doc.RootElement.TryGetProperty("calibrations", out var cals)) return map;
        foreach (var p in cals.EnumerateObject())
        {
            var v = p.Value;
            double D(string k) => v.TryGetProperty(k, out var e) ? e.GetDouble() : 0;
            bool B(string k) => v.TryGetProperty(k, out var e) && e.GetBoolean();
            int I(string k) => v.TryGetProperty(k, out var e) ? e.GetInt32() : 0;
            map[p.Name] = new AreaCalibration(D("scale"), D("rotationRadians"), D("originX"), D("originY"),
                I("referenceCount"), D("residualPixels")) { MirrorNorth = B("mirrorNorth") };
        }
        return map;
    }

    private static List<LandmarkRef> LoadWorldPoints(string landmarksPath, string npcsPath, string area)
    {
        var list = new List<LandmarkRef>(LandmarksReader.LoadForArea(landmarksPath, area));
        list.AddRange(NpcsReader.LoadForArea(npcsPath, area));
        return list;
    }

    private static bool TryTextureSize(string texturesDir, string area, out int w, out int h)
    {
        w = h = 0;
        var path = MapTextureExtractor.EnsureExtractedOrCached(texturesDir, area);
        if (path is null || !File.Exists(path)) return false;
        var (_, iw, ih) = ImageIo.LoadBgra(path);
        w = iw; h = ih;
        return true;
    }

    private static void Emit(string outDir, string mode, IReadOnlyList<StudyRecord> rows)
    {
        Directory.CreateDirectory(outDir);
        File.WriteAllText(Path.Combine(outDir, $"{mode}.csv"), StudyRecord.ToCsv(rows));
        File.WriteAllText(Path.Combine(outDir, $"{mode}.md"), StudyRecord.ToMarkdown(rows));
        Console.WriteLine(StudyRecord.ToMarkdown(rows));
        Console.WriteLine($"[{mode}] wrote {rows.Count} rows to {outDir}");
    }

    private static Dictionary<string, string> ParseOptions(string[] args)
    {
        var o = new Dictionary<string, string>(StringComparer.Ordinal);
        for (var i = 1; i < args.Length - 1; i++)
            if (args[i].StartsWith("--", StringComparison.Ordinal)) o[args[i]] = args[i + 1];
        return o;
    }
}
```

- [ ] **Step 2: Add the texture-cache helper to `MapTextureExtractor`**

The Program calls `MapTextureExtractor.EnsureExtractedOrCached(texturesDir, area)` — a no-PG-install path that returns the cached PNG if present (the study runs against pre-extracted textures, not a live decode). Add to `tools/Mithril.MapCalibration.Tools.Common/MapTextureExtractor.cs`:

```csharp
/// <summary>
/// Returns the cached <c>Map_&lt;Area&gt;.v{N}.png</c> in <paramref name="mapDir"/>
/// if it exists, without requiring a PG install / bundle decode. Used by the
/// gate-study tool, which runs against pre-extracted textures. Returns null if
/// no cached PNG is present.
/// </summary>
public static string? EnsureExtractedOrCached(string mapDir, string area)
{
    var outPng = Path.Combine(mapDir, $"Map_{area}.v{CacheFormatVersion}.png");
    return File.Exists(outPng) ? outPng : null;
}
```

- [ ] **Step 3: Build (no new unit tests — IO glue)**

Run: `dotnet build tools/MapCalibrationStudy/MapCalibrationStudy.csproj -v q`
Expected: Build succeeded. (Close Mithril first — the running-shell hook blocks builds.)

- [ ] **Step 4: Re-run the full test project to confirm nothing regressed**

Run: `dotnet test tests/MapCalibrationStudy.Tests/MapCalibrationStudy.Tests.csproj -v q`
Expected: PASS (all tasks' tests).

- [ ] **Step 5: Commit**

```bash
git add tools/MapCalibrationStudy/Program.cs tools/Mithril.MapCalibration.Tools.Common/MapTextureExtractor.cs
git commit -m "feat(map-calibration-study): measure + bootstrap subcommands wiring (#897)"
```

---

## Task 9: Run the study against the real corpus + record the verdict

**Files:**
- Create: `study/` working dir (screenshots + extracted textures + outputs) — **not committed** (add to `.gitignore` if needed).
- Modify (out of band): wiki [Legolas-Calibration-Findings](https://github.com/moumantai-gg/mithril/wiki/Legolas-Calibration-Findings)
- Create: `docs/map-calibration-gate-verdict.md`

This task is manual (real game data); no unit tests. It produces the actual verdict.

- [ ] **Step 1: Capture the corpus.** For as many of the 6 reachable areas as practical (incl. ≥1 of Eltibule/KurMountains), open the map, **zoom fully out** (pan = 0), screenshot → `study/screenshots/<AreaKey>.png`.

- [ ] **Step 2: Stage textures + icons.** Ensure per-area `Map_<Area>.v{N}.png` exist in `study/textures/` (run the existing `tools/MapCalibrationFromScreenshot` / harness extraction once per area against your PG install) and the icon index in `study/icons/` (via `IconTemplateExtractor`).

- [ ] **Step 3: Produce texture-frame ground-truth solves.** For each captured area, calibrate in the existing `tools/MapCalibrationWpf` harness against the screenshot and Commit, so `src/Mithril.MapCalibration/BundledData/map-calibration-baseline.json` (or a study copy) holds a texture-frame `AreaCalibration` per area. (Half A ground truth.)

- [ ] **Step 4: Run `measure`.**

Run:
```
dotnet run --project tools/MapCalibrationStudy -- measure ^
  --refinements "%LOCALAPPDATA%\Mithril\MapCalibration\refinements.json" ^
  --baseline src/Mithril.MapCalibration/BundledData/map-calibration-baseline.json ^
  --landmarks <path-to>/landmarks.json --npcs <path-to>/npcs.json ^
  --textures study/textures --areas AreaSerbule,AreaEltibule,AreaKurMountains,AreaCave1,AreaCasino,AreaMyconianCave ^
  --out study/out
```
Expected: a 6-row markdown table printed + `study/out/measure.{csv,md}`. Eyeball: all `orientationDeg` ∈ {0,180}; `insetFracMax` clusters; `affResid` ≈ `simResid`.

- [ ] **Step 5: Run `bootstrap`** on the captured areas.

Run:
```
dotnet run --project tools/MapCalibrationStudy -- bootstrap ^
  --screenshots study/screenshots --textures study/textures --icons study/icons ^
  --landmarks <path-to>/landmarks.json --npcs <path-to>/npcs.json ^
  --areas <captured-areas> --out study/out
```
Expected: per-area refined residual + corresponded count. Confirm residual `< 2 px` and pairing matches the Task-3 ground-truth solve on areas with ≥3 clean icons.

- [ ] **Step 6: Evaluate against the §6 thresholds and write the verdict.** Create `docs/map-calibration-gate-verdict.md` with: the two tables, each sub-hypothesis (H1–H4) marked pass/fail with the numbers, and the decision — **proceed** (write the engine spec), **stop**, or **investigate** (naming the broken sub-hypothesis + offending area).

- [ ] **Step 7: Update the wiki.** Extend [Legolas-Calibration-Findings](https://github.com/moumantai-gg/mithril/wiki/Legolas-Calibration-Findings) from n=2 to the full sample with the table + the go/no-go conclusion. (Wiki uses the `master` branch; see the `workspace_repo_map` convention.)

- [ ] **Step 8: Commit the notebook + close the issue.**

```bash
git add docs/map-calibration-gate-verdict.md
git commit -m "docs(map-calibration): record gate-study verdict (#897)"
```
Then comment on [mithril#897](https://github.com/moumantai-gg/mithril/issues/897) with the verdict; on **pass**, that comment is the trigger to write the engine spec; on **fail**, it records the broken sub-hypothesis. (AI-drafted GitHub comments carry the `— drafted by Claude … posted by @arthur-conde` trailer.)

---

## Task 10: Schedule tool teardown (separate delete PR)

The tool is throwaway. Once the verdict is recorded and the engine decision is made, delete it in its **own** PR (separate from the add, per the squash-merge-orphans rule so neither commit is gc-eligible mid-history).

- [ ] **Step 1:** `dotnet sln Mithril.slnx remove tests/MapCalibrationStudy.Tests/MapCalibrationStudy.Tests.csproj`
- [ ] **Step 2:** `git rm -r tools/MapCalibrationStudy tests/MapCalibrationStudy.Tests` and revert the `EnsureExtractedOrCached` helper if no other consumer adopted it.
- [ ] **Step 3:** Commit `chore(map-calibration): remove throwaway gate-study tool after verdict (#897)` and open as a standalone PR.

> Do not run Task 10 until Task 9's verdict is committed. It is listed here so the teardown isn't forgotten.

---

## Self-review notes

- **Spec coverage:** §3 source-0 (rotation/handedness) → Task 8 `LoadRefinements` + `measure`; §4 Half-A metrics → Tasks 2/3/4 + `measure`; §5 Half-B bootstrap → Tasks 5/6 + `bootstrap`; §6 verdict thresholds → Task 9; §7 tooling/deliverables/teardown → Tasks 1/8/9/10. H1→Task 2, H2→Task 4, H3→Task 3, H4→Tasks 5+6.
- **Known weak seam:** Task 8's affine RMS re-projects world points through the solved similarity (the baseline doesn't persist the original click pixels), so `affResid` measures model self-consistency rather than an independent affine-vs-similarity contest. If a sharper H2 answer is wanted, capture the raw `(world↔click)` pairs during the Task-3 harness solves and feed those to `AffineFit.Rms`/`SimilarityRms` instead. Noted so the executor doesn't mistake the convenience path for the rigorous one.
- **Throwaway discipline:** no shell/module wiring; Task 10 removes it.
