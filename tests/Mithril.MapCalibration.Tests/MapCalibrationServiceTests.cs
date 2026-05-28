using System.IO;
using FluentAssertions;
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
    public void ImportUserRefinements_first_run_writes_every_entry()
    {
        var svc = new MapCalibrationService(
            new Dictionary<string, AreaCalibration>(),
            new UserRefinementStore(_tempDir),
            goodResidualThresholdPx: 12.0);

        var payload = new Dictionary<string, AreaCalibration>
        {
            ["AreaEltibule"] = MakeCal(residual: 5.0, scale: 1.5),
            ["AreaSerbule"] = MakeCal(residual: 5.0, scale: 2.0),
        };

        svc.ImportUserRefinements(payload).Should().Be(2);
        svc.GetCalibration("AreaEltibule")!.Scale.Should().Be(1.5);
        svc.GetCalibration("AreaSerbule")!.Scale.Should().Be(2.0);
    }

    [Fact]
    public void ImportUserRefinements_is_idempotent_when_store_already_matches()
    {
        var svc = new MapCalibrationService(
            new Dictionary<string, AreaCalibration>(),
            new UserRefinementStore(_tempDir),
            goodResidualThresholdPx: 12.0);

        var cal = MakeCal(residual: 5.0, scale: 1.5);
        svc.ImportUserRefinements(new Dictionary<string, AreaCalibration> { ["AreaEltibule"] = cal });

        // Re-running the import with the same content is a no-op; covers the
        // every-startup re-import perf regression cited in the PR review (#3).
        svc.ImportUserRefinements(new Dictionary<string, AreaCalibration> { ["AreaEltibule"] = cal })
           .Should().Be(0);
    }

    [Fact]
    public void ImportUserRefinements_overwrites_when_legacy_value_differs()
    {
        // Downgrade-edit scenario from PR review #5: user calibrates via new
        // path (both stores in sync), downgrades to legacy-only build,
        // recalibrates (only LegolasSettings.AreaCalibrations updates), then
        // re-upgrades. At re-upgrade the legacy entry is newer than the
        // stored refinement — prefer it.
        var svc = new MapCalibrationService(
            new Dictionary<string, AreaCalibration>(),
            new UserRefinementStore(_tempDir),
            goodResidualThresholdPx: 12.0);

        svc.ImportUserRefinements(new Dictionary<string, AreaCalibration>
        {
            ["AreaEltibule"] = MakeCal(residual: 5.0, scale: 1.5),
        });

        // Legacy edit while downgraded — math differs.
        var imported = svc.ImportUserRefinements(new Dictionary<string, AreaCalibration>
        {
            ["AreaEltibule"] = MakeCal(residual: 5.0, scale: 2.7),
        });

        imported.Should().Be(1);
        svc.GetCalibration("AreaEltibule")!.Scale.Should().Be(2.7);
    }

    [Fact]
    public void ImportUserRefinements_is_silent_no_Changed_event()
    {
        // Migration runs on the ThreadPool inside host.StartAsync; firing
        // Changed there would cross-thread any UI subscriber attached during
        // module bootstrap (PR review #6). The contract on the interface says
        // ImportUserRefinements does not raise; this test pins it.
        var svc = new MapCalibrationService(
            new Dictionary<string, AreaCalibration>(),
            new UserRefinementStore(_tempDir),
            goodResidualThresholdPx: 12.0);

        var fired = 0;
        svc.Changed += (_, _) => fired++;

        svc.ImportUserRefinements(new Dictionary<string, AreaCalibration>
        {
            ["AreaEltibule"] = MakeCal(residual: 5.0, scale: 1.5),
            ["AreaSerbule"] = MakeCal(residual: 5.0, scale: 2.0),
        });

        fired.Should().Be(0);
    }

    [Fact]
    public void Active_calibration_after_high_residual_save_with_baseline_falls_to_baseline()
    {
        // Round-1 review #1 → round-2 review #1 + #5: the SHARED service's
        // GetCalibration honours stacking precedence and returns the baseline
        // when the user's solve has too-high residual. The wizard does NOT
        // call GetCalibration to surface "your solve was good" — it consumes
        // the AreaCalibrationService.CalibrateCurrentArea return value, which
        // is the solver output (covered by the Legolas-side test). The two
        // questions ("what did you solve" vs "what's rendered") are
        // intentionally separate; this test pins the GetCalibration side.
        var baseline = new Dictionary<string, AreaCalibration>
        {
            ["AreaEltibule"] = MakeCal(residual: 4.0, scale: 1.0) with { Source = CalibrationSource.BundledBaseline },
        };
        var svc = new MapCalibrationService(baseline, new UserRefinementStore(_tempDir), goodResidualThresholdPx: 12.0);

        svc.SaveUserRefinement("AreaEltibule", MakeCal(residual: 25.0, scale: 2.0));

        var active = svc.GetCalibration("AreaEltibule");
        active.Should().NotBeNull();
        active!.Source.Should().Be(CalibrationSource.BundledBaseline);
        active.Scale.Should().Be(1.0);

        // The losing user refinement is still discoverable via GetAllSources
        // (debug surface for "what did the user actually solve").
        svc.GetAllSources("AreaEltibule")
            .Should().ContainSingle(s => s.Source == CalibrationSource.UserRefinement && s.Scale == 2.0);
    }

    [Fact]
    public void ImportUserRefinements_ULP_drift_does_not_re_import_on_next_run()
    {
        // Round-2 review #4: a one-ULP wobble between the legacy entry's
        // doubles and the stored doubles must not be treated as "different
        // math" — otherwise every cold start writes the file again.
        var svc = new MapCalibrationService(
            new Dictionary<string, AreaCalibration>(),
            new UserRefinementStore(_tempDir),
            goodResidualThresholdPx: 12.0);

        var baseScale = 1.7333333333333334;
        svc.ImportUserRefinements(new Dictionary<string, AreaCalibration>
        {
            ["AreaEltibule"] = MakeCal(residual: 5.0, scale: baseScale),
        });

        // Same logical value, shifted by one ULP — mimics a JSON round-trip
        // wobble or cross-JIT codegen drift.
        var wobbled = Math.BitIncrement(baseScale);
        svc.ImportUserRefinements(new Dictionary<string, AreaCalibration>
        {
            ["AreaEltibule"] = MakeCal(residual: 5.0, scale: wobbled),
        }).Should().Be(0);
    }

    [Fact]
    public void Save_rolls_back_in_memory_state_when_Persist_throws()
    {
        // Round-2 review #2 (deeper concern): Save mutates _refinements before
        // Persist. If Persist throws (disk full / AV lock / OneDrive
        // placeholder) the in-memory state must roll back so same-session
        // reads see the failure, not a value that vanishes on next process
        // boot. We provoke the throw by locking the file open externally for
        // exclusive write — the temp-write attempt inside Persist fails.
        var store = new UserRefinementStore(_tempDir);
        var initial = MakeCal(residual: 5.0, scale: 1.0);
        store.Save("AreaEltibule", initial);

        // Hold the .tmp path exclusively so the next Save's File.WriteAllText
        // throws when it tries to open it.
        var tmpPath = Path.Combine(_tempDir, "refinements.json.tmp");
        using (var lockHandle = new FileStream(tmpPath, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            FluentActions.Invoking(() => store.Save("AreaEltibule", MakeCal(residual: 5.0, scale: 99.0)))
                .Should().Throw<IOException>();
        }

        // Same-session read returns the original value, not the rolled-back attempt.
        store.TryGet("AreaEltibule", out var current).Should().BeTrue();
        current.Scale.Should().Be(1.0);
    }

    [Fact]
    public void ImportFromLegacy_rolls_back_in_memory_state_when_Persist_throws()
    {
        // Round-3 review #3: the whole-batch all-or-nothing invariant on the
        // migration path is distinct from Save's per-key rollback — the
        // snapshot is the whole pre-import dictionary, restored atomically.
        // Provoke a persist throw by locking refinements.json.tmp exclusively
        // and confirm neither new entry leaks into TryGet after the throw.
        var store = new UserRefinementStore(_tempDir);
        var preexisting = MakeCal(residual: 5.0, scale: 1.0);
        store.Save("AreaEltibule", preexisting);

        var tmpPath = Path.Combine(_tempDir, "refinements.json.tmp");
        using (var lockHandle = new FileStream(tmpPath, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            FluentActions.Invoking(() => store.ImportFromLegacy(new Dictionary<string, AreaCalibration>
            {
                ["AreaSerbule"] = MakeCal(residual: 5.0, scale: 2.0),
                ["AreaGoblinDungeon"] = MakeCal(residual: 5.0, scale: 3.0),
            })).Should().Throw<IOException>();
        }

        // Neither new entry leaked into the in-memory dictionary.
        store.TryGet("AreaSerbule", out _).Should().BeFalse();
        store.TryGet("AreaGoblinDungeon", out _).Should().BeFalse();
        // The pre-existing entry survived intact.
        store.TryGet("AreaEltibule", out var elt).Should().BeTrue();
        elt.Scale.Should().Be(1.0);
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
