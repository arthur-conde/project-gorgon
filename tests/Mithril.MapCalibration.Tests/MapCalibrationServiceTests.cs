using System.IO;
using FluentAssertions;
using Mithril.MapCalibration.DependencyInjection;
using Mithril.MapCalibration.Internal;
using Xunit;

namespace Mithril.MapCalibration.Tests;

/// <summary>
/// Stacked-source precedence: user-refinement &gt; community-sync (future) &gt;
/// bundled-baseline, with the residual threshold downgrading a bad user
/// refinement.
/// </summary>
public sealed class MapCalibrationServiceTests : IDisposable
{
    private readonly string _tempDir;

    public MapCalibrationServiceTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "mithril-mapcal-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); }
        catch (IOException) { /* leave it; CI temp dir gets reaped */ }
    }

    [Fact]
    public void Good_user_refinement_wins_over_baseline()
    {
        var baseline = new Dictionary<string, AreaCalibration>
        {
            ["AreaEltibule"] = MakeCal(residual: 4.0, scale: 1.0) with { Source = CalibrationSource.BundledBaseline },
        };
        var store = new UserRefinementStore(_tempDir);
        store.Save("AreaEltibule", MakeCal(residual: 8.0, scale: 2.0));

        var svc = new MapCalibrationService(baseline, store, goodResidualThresholdPx: 12.0, logger: null);

        var active = svc.GetCalibration("AreaEltibule");
        active.Should().NotBeNull();
        active!.Source.Should().Be(CalibrationSource.UserRefinement);
        active.Scale.Should().Be(2.0);
    }

    [Fact]
    public void Bad_user_refinement_falls_through_to_baseline()
    {
        var baseline = new Dictionary<string, AreaCalibration>
        {
            ["AreaEltibule"] = MakeCal(residual: 5.0, scale: 1.0) with { Source = CalibrationSource.BundledBaseline },
        };
        var store = new UserRefinementStore(_tempDir);
        // Above the threshold of 12 — the resolver should prefer the baseline.
        store.Save("AreaEltibule", MakeCal(residual: 25.0, scale: 2.0));

        var svc = new MapCalibrationService(baseline, store, goodResidualThresholdPx: 12.0, logger: null);

        var active = svc.GetCalibration("AreaEltibule");
        active.Should().NotBeNull();
        active!.Source.Should().Be(CalibrationSource.BundledBaseline);
        active.Scale.Should().Be(1.0);
    }

    [Fact]
    public void Bad_user_refinement_with_no_baseline_still_returns_user()
    {
        var baseline = new Dictionary<string, AreaCalibration>(); // none
        var store = new UserRefinementStore(_tempDir);
        store.Save("AreaEltibule", MakeCal(residual: 25.0, scale: 2.0));

        var svc = new MapCalibrationService(baseline, store, goodResidualThresholdPx: 12.0, logger: null);

        var active = svc.GetCalibration("AreaEltibule");
        active.Should().NotBeNull();
        active!.Source.Should().Be(CalibrationSource.UserRefinement);
    }

    [Fact]
    public void IsCalibrated_returns_false_when_no_source_exists()
    {
        var svc = new MapCalibrationService(
            new Dictionary<string, AreaCalibration>(),
            new UserRefinementStore(_tempDir),
            goodResidualThresholdPx: 12.0);

        svc.IsCalibrated("AreaEltibule").Should().BeFalse();
        svc.GetCalibration("AreaEltibule").Should().BeNull();
        svc.WorldToWindow("AreaEltibule", new WorldCoord(1, 0, 1), 1.0).Should().BeNull();
        svc.WindowToWorld("AreaEltibule", new PixelPoint(1, 1), 1.0).Should().BeNull();
    }

    [Fact]
    public void GetAllSources_returns_user_and_baseline_separately()
    {
        var baseline = new Dictionary<string, AreaCalibration>
        {
            ["AreaEltibule"] = MakeCal(residual: 4.0, scale: 1.0) with { Source = CalibrationSource.BundledBaseline },
        };
        var store = new UserRefinementStore(_tempDir);
        store.Save("AreaEltibule", MakeCal(residual: 8.0, scale: 2.0));

        var svc = new MapCalibrationService(baseline, store, goodResidualThresholdPx: 12.0);

        var sources = svc.GetAllSources("AreaEltibule");
        sources.Should().HaveCount(2);
        sources.Should().Contain(c => c.Source == CalibrationSource.UserRefinement && c.ResidualPixels == 8.0);
        sources.Should().Contain(c => c.Source == CalibrationSource.BundledBaseline && c.ResidualPixels == 4.0);
    }

    [Fact]
    public void SaveUserRefinement_persists_across_service_instances()
    {
        var svc1 = new MapCalibrationService(
            new Dictionary<string, AreaCalibration>(),
            new UserRefinementStore(_tempDir),
            goodResidualThresholdPx: 12.0);

        svc1.SaveUserRefinement("AreaEltibule", MakeCal(residual: 3.0, scale: 1.7));

        // New service instance reading from the same directory should see it.
        var svc2 = new MapCalibrationService(
            new Dictionary<string, AreaCalibration>(),
            new UserRefinementStore(_tempDir),
            goodResidualThresholdPx: 12.0);

        var loaded = svc2.GetCalibration("AreaEltibule");
        loaded.Should().NotBeNull();
        loaded!.Scale.Should().Be(1.7);
        loaded.Source.Should().Be(CalibrationSource.UserRefinement);
    }

    [Fact]
    public void ClearUserRefinement_returns_to_baseline()
    {
        var baseline = new Dictionary<string, AreaCalibration>
        {
            ["AreaEltibule"] = MakeCal(residual: 4.0, scale: 1.0) with { Source = CalibrationSource.BundledBaseline },
        };
        var store = new UserRefinementStore(_tempDir);
        var svc = new MapCalibrationService(baseline, store, goodResidualThresholdPx: 12.0);

        svc.SaveUserRefinement("AreaEltibule", MakeCal(residual: 6.0, scale: 2.0));
        svc.GetCalibration("AreaEltibule")!.Source.Should().Be(CalibrationSource.UserRefinement);

        svc.ClearUserRefinement("AreaEltibule");
        svc.GetCalibration("AreaEltibule")!.Source.Should().Be(CalibrationSource.BundledBaseline);
    }

    [Fact]
    public void Changed_fires_for_save_and_clear()
    {
        var store = new UserRefinementStore(_tempDir);
        var svc = new MapCalibrationService(
            new Dictionary<string, AreaCalibration>(),
            store,
            goodResidualThresholdPx: 12.0);

        var notifications = new List<string>();
        svc.Changed += (_, key) => notifications.Add(key);

        svc.SaveUserRefinement("AreaEltibule", MakeCal(residual: 5.0, scale: 1.0));
        svc.ClearUserRefinement("AreaEltibule");

        notifications.Should().Equal("AreaEltibule", "AreaEltibule");
    }

    [Fact]
    public void Import_if_absent_does_not_overwrite_existing()
    {
        var store = new UserRefinementStore(_tempDir);
        var keep = MakeCal(residual: 5.0, scale: 1.5);
        store.Save("AreaEltibule", keep);

        // Migration "imports" a different scale; should be ignored because key already present.
        var migrationPayload = new Dictionary<string, AreaCalibration>
        {
            ["AreaEltibule"] = MakeCal(residual: 5.0, scale: 999.0),
            ["AreaSerbule"] = MakeCal(residual: 5.0, scale: 2.0),
        };
        var imported = store.ImportIfAbsent(migrationPayload);
        imported.Should().Be(1); // only Serbule was new
        store.TryGet("AreaEltibule", out var elt).Should().BeTrue();
        elt.Scale.Should().Be(1.5);
        store.TryGet("AreaSerbule", out var ser).Should().BeTrue();
        ser.Scale.Should().Be(2.0);
    }

    private static AreaCalibration MakeCal(double residual, double scale) =>
        new(
            Scale: scale,
            RotationRadians: 0,
            OriginX: 100,
            OriginY: 100,
            ReferenceCount: 3,
            ResidualPixels: residual);
}
