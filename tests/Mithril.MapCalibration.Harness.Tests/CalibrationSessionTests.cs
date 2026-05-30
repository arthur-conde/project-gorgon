using FluentAssertions;
using Mithril.MapCalibration;
using Mithril.Tools.MapCalibration.Common;
using Mithril.Tools.MapCalibration.Harness;
using Xunit;

namespace Mithril.Tools.MapCalibration.Harness.Tests;

public class CalibrationSessionTests
{
    // Ground-truth transform mirrored from LandmarkCalibrationSolverTests:
    // North = +Z, pixel-Y grows downward.
    private const double Scale = 1.7;
    private static readonly double Rot = Math.PI / 5;
    private static readonly PixelPoint Origin = new(640, 360);

    private static PixelPoint Project(double x, double z)
    {
        var cos = Math.Cos(Rot);
        var sin = Math.Sin(Rot);
        var rotE = x * cos + z * sin;
        var rotN = -x * sin + z * cos;
        return new PixelPoint(Origin.X + Scale * rotE, Origin.Y - Scale * rotN);
    }

    // Signed world coords (negative X/Z are real PG positions).
    private static readonly (double X, double Z)[] World =
    {
        (-677.0, 803.0),
        (1138.0, 1367.0),
        (-1459.0, -860.0),
        (254.0, -55.0),
    };

    private static CalibrationContext ContextWithLandmarks(params (double X, double Z)[] worlds)
    {
        var landmarks = worlds
            .Select((p, i) => new LandmarkRef("Npc", $"L{i}", new WorldCoord(p.X, 0, p.Z)))
            .ToList();
        return new CalibrationContext("TestArea", landmarks, null, null, (1280, 720), null);
    }

    private static CalibrationRef RefFor(double x, double z, PixelPoint texture, bool enabled = true) => new()
    {
        Name = "L",
        Kind = "Npc",
        Source = CalibrationRefSource.Manual,
        Confidence = 1.0,
        World = new WorldCoord(x, 0, z),
        TexturePixel = texture,
        Enabled = enabled,
    };

    [Fact]
    public void Known_correspondences_recover_a_known_calibration()
    {
        var session = new CalibrationSession(ContextWithLandmarks(World));
        foreach (var (x, z) in World)
            session.Refs.Add(RefFor(x, z, Project(x, z)));

        session.ReSolve();

        session.Calibration.Should().NotBeNull();
        var cal = session.Calibration!;
        cal.Scale.Should().BeApproximately(Scale, 1e-6);
        cal.RotationRadians.Should().BeApproximately(Rot, 1e-6);
        cal.OriginX.Should().BeApproximately(Origin.X, 1e-4);
        cal.OriginY.Should().BeApproximately(Origin.Y, 1e-4);
        cal.ResidualPixels.Should().BeApproximately(0, 1e-6);
        cal.ReferenceCount.Should().Be(4);
    }

    [Fact]
    public void Residuals_are_filled_per_enabled_ref()
    {
        var session = new CalibrationSession(ContextWithLandmarks(World));
        foreach (var (x, z) in World)
            session.Refs.Add(RefFor(x, z, Project(x, z)));

        session.ReSolve();

        session.Refs.Should().OnlyContain(r => r.ResidualPx != null);
        session.Refs.Should().OnlyContain(r => r.ResidualPx < 1e-6);
    }

    [Fact]
    public void Projections_are_refreshed_for_all_landmarks_not_just_refs()
    {
        // 4 landmarks in context, but only 2 are ref'd.
        var session = new CalibrationSession(ContextWithLandmarks(World));
        session.Refs.Add(RefFor(World[0].X, World[0].Z, Project(World[0].X, World[0].Z)));
        session.Refs.Add(RefFor(World[1].X, World[1].Z, Project(World[1].X, World[1].Z)));

        session.ReSolve();

        session.Projections.Should().HaveCount(4);
        // Landmarks that are also enabled refs carry a residual; the rest don't.
        session.Projections.Count(p => p.ResidualPx != null).Should().Be(2);
        // The projected pixel for a ref'd landmark lands on its true pixel.
        var p0 = session.Projections.First(p => p.Name == "L0");
        var truth = Project(World[0].X, World[0].Z);
        p0.TexturePixel.X.Should().BeApproximately(truth.X, 1e-6);
        p0.TexturePixel.Y.Should().BeApproximately(truth.Y, 1e-6);
    }

    [Fact]
    public void Fewer_than_two_enabled_refs_yields_null_calibration()
    {
        var session = new CalibrationSession(ContextWithLandmarks(World));
        session.Refs.Add(RefFor(World[0].X, World[0].Z, Project(World[0].X, World[0].Z)));

        session.ReSolve();

        session.Calibration.Should().BeNull();
        session.Projections.Should().BeEmpty();
        session.Refs.Should().OnlyContain(r => r.ResidualPx == null);
    }

    [Fact]
    public void Disabling_a_ref_below_the_threshold_nulls_the_calibration()
    {
        var session = new CalibrationSession(ContextWithLandmarks(World[0], World[1]));
        session.Refs.Add(RefFor(World[0].X, World[0].Z, Project(World[0].X, World[0].Z)));
        session.Refs.Add(RefFor(World[1].X, World[1].Z, Project(World[1].X, World[1].Z)));
        session.ReSolve();
        session.Calibration.Should().NotBeNull();

        session.Refs[0].Enabled = false;
        session.ReSolve();

        session.Calibration.Should().BeNull();
    }

    [Fact]
    public void Disabling_a_ref_changes_the_fit()
    {
        // Two consistent refs + a third that is consistent too → exact fit.
        // Then move the third onto a slightly different transform so it pulls
        // the least-squares solution; disabling it must change the result.
        var session = new CalibrationSession(ContextWithLandmarks(World));
        session.Refs.Add(RefFor(World[0].X, World[0].Z, Project(World[0].X, World[0].Z)));
        session.Refs.Add(RefFor(World[1].X, World[1].Z, Project(World[1].X, World[1].Z)));
        // A third ref placed off-true so it perturbs the fit.
        var off = Project(World[2].X, World[2].Z);
        session.Refs.Add(RefFor(World[2].X, World[2].Z, new PixelPoint(off.X + 40, off.Y - 30)));
        session.ReSolve();
        var withPerturbed = session.Calibration!;
        withPerturbed.Scale.Should().NotBeApproximately(Scale, 1e-6); // perturbed

        session.Refs[2].Enabled = false;
        session.ReSolve();

        session.Calibration!.Scale.Should().BeApproximately(Scale, 1e-6); // back to clean
    }

    [Fact]
    public void A_bad_ref_inflates_residual_and_disabling_it_restores_the_fit()
    {
        var session = new CalibrationSession(ContextWithLandmarks(World));
        foreach (var (x, z) in World)
            session.Refs.Add(RefFor(x, z, Project(x, z)));
        // Deliberately misplace the last ref well off true.
        var bad = Project(World[3].X, World[3].Z);
        session.Refs[3].TexturePixel = new PixelPoint(bad.X + 80, bad.Y + 80);
        session.ReSolve();

        session.Calibration!.ResidualPixels.Should().BeGreaterThan(5);

        session.Refs[3].Enabled = false;
        session.ReSolve();

        session.Calibration!.ResidualPixels.Should().BeApproximately(0, 1e-6);
        // Remaining enabled refs now fit exactly.
        session.Refs.Where(r => r.Enabled).Should().OnlyContain(r => r.ResidualPx < 1e-6);
    }

    [Fact]
    public void Nudge_re_solves()
    {
        var session = new CalibrationSession(ContextWithLandmarks(World));
        foreach (var (x, z) in World)
            session.Refs.Add(RefFor(x, z, Project(x, z)));
        session.ReSolve();
        session.Calibration!.ResidualPixels.Should().BeApproximately(0, 1e-6);

        // Nudge one ref off true; the re-solve must surface a non-zero residual.
        session.NudgeSelected(session.Refs[0], dx: 50, dy: -50);

        session.Refs[0].TexturePixel.X.Should().BeApproximately(Project(World[0].X, World[0].Z).X + 50, 1e-9);
        session.Calibration!.ResidualPixels.Should().BeGreaterThan(5);
    }

    [Fact]
    public void Accept_with_world_adds_enabled_ref_and_re_solves()
    {
        var session = new CalibrationSession(ContextWithLandmarks(World));

        session.Accept(new CandidateRef(
            Project(World[0].X, World[0].Z),
            new WorldCoord(World[0].X, 0, World[0].Z),
            LandmarkId: "L0", SuggestedName: "L0", Kind: "Npc",
            Source: CalibrationRefSource.Manual, Confidence: 1.0));
        session.Accept(new CandidateRef(
            Project(World[1].X, World[1].Z),
            new WorldCoord(World[1].X, 0, World[1].Z),
            LandmarkId: "L1", SuggestedName: "L1", Kind: "Npc",
            Source: CalibrationRefSource.Manual, Confidence: 1.0));

        session.Refs.Should().HaveCount(2);
        session.Refs.Should().OnlyContain(r => r.Enabled);
        session.Calibration.Should().NotBeNull();
        session.Calibration!.ResidualPixels.Should().BeApproximately(0, 1e-6);
    }

    [Fact]
    public void Accept_without_world_adds_a_disabled_ref()
    {
        var session = new CalibrationSession(ContextWithLandmarks(World));

        session.Accept(new CandidateRef(
            new PixelPoint(10, 20), World: null,
            LandmarkId: null, SuggestedName: null, Kind: "Unknown",
            Source: CalibrationRefSource.GreenPixel, Confidence: 0.8));

        session.Refs.Should().HaveCount(1);
        session.Refs[0].Enabled.Should().BeFalse();
        session.Calibration.Should().BeNull(); // <2 enabled
    }

    [Fact]
    public void Remove_re_solves()
    {
        var session = new CalibrationSession(ContextWithLandmarks(World));
        foreach (var (x, z) in World)
            session.Refs.Add(RefFor(x, z, Project(x, z)));
        session.ReSolve();

        // Drop to a single ref → null calibration.
        session.Remove(session.Refs[0]);
        session.Remove(session.Refs[0]);
        session.Remove(session.Refs[0]);

        session.Refs.Should().HaveCount(1);
        session.Calibration.Should().BeNull();
    }
}
