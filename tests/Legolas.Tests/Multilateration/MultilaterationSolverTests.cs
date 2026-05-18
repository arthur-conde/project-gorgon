using FluentAssertions;
using Legolas.Services;

namespace Legolas.Tests.Multilateration;

/// <summary>
/// #488 — range-only weighted-NLS multilateration. Replaces the retired
/// closed-form 3-circle solver tests. Covers known-point recovery, unbounded
/// ≥3 samples, inverse-variance weighting, RANSAC outlier rejection, the GDOP
/// gate, and the live equal-distance (1285/1285) case.
/// </summary>
public class MultilaterationSolverTests
{
    private readonly MultilaterationSolver _solver = new();

    private static MultilaterationSample S(double x, double z, double d, double w = 1.0) =>
        new(x, z, d, w);

    private static double Dist(double x, double z, double tx, double tz) =>
        Math.Sqrt((x - tx) * (x - tx) + (z - tz) * (z - tz));

    [Fact]
    public void Recovers_a_known_target_from_three_exact_ranges()
    {
        (double X, double Z) t = (137, -245);
        var s = new[]
        {
            S(0, 0, Dist(0, 0, t.X, t.Z)),
            S(400, 0, Dist(400, 0, t.X, t.Z)),
            S(0, -400, Dist(0, -400, t.X, t.Z)),
        };

        var r = _solver.Solve(s);

        r.Quality.Should().Be(MultilaterationQuality.Solved);
        r.Point.Should().NotBeNull();
        r.Point!.Value.X.Should().BeApproximately(t.X, 1e-3);
        r.Point.Value.Z.Should().BeApproximately(t.Z, 1e-3);
    }

    [Fact]
    public void Uses_all_samples_unbounded_and_averages_out_quantization_noise()
    {
        (double X, double Z) t = (512, 333);
        // 8 spots, each distance rounded to integer metres (±0.5 m floor).
        var pts = new (double X, double Z)[]
        {
            (0,0),(900,0),(0,800),(900,800),(450,-200),(-200,450),(1100,400),(400,1200),
        };
        var s = pts.Select(p => S(p.X, p.Z, Math.Round(Dist(p.X, p.Z, t.X, t.Z)))).ToArray();

        var r = _solver.Solve(s);

        r.Quality.Should().Be(MultilaterationQuality.Solved);
        // With 8 over-determined readings the fit is well within the noise floor.
        r.Point!.Value.X.Should().BeApproximately(t.X, 2.0);
        r.Point.Value.Z.Should().BeApproximately(t.Z, 2.0);
        r.Inliers.Should().HaveCount(8);
    }

    [Fact]
    public void Ransac_rejects_a_grossly_wrong_reading()
    {
        (double X, double Z) t = (250, 250);
        var pts = new (double X, double Z)[]
        {
            (0,0),(600,0),(0,600),(600,600),(300,-300),
        };
        var s = pts.Select(p => S(p.X, p.Z, Dist(p.X, p.Z, t.X, t.Z))).ToList();
        // Poison one reading by +400 m (mis-paired distance).
        s[2] = S(pts[2].X, pts[2].Z, Dist(pts[2].X, pts[2].Z, t.X, t.Z) + 400);

        var r = _solver.Solve(s);

        r.Quality.Should().Be(MultilaterationQuality.Solved);
        r.Inliers[2].Should().BeFalse("the poisoned reading must be rejected");
        r.Point!.Value.X.Should().BeApproximately(t.X, 3.0);
        r.Point.Value.Z.Should().BeApproximately(t.Z, 3.0);
    }

    [Fact]
    public void Weighting_pulls_the_fit_toward_high_confidence_samples()
    {
        // Two consistent high-weight ranges + one mildly off low-weight range.
        (double X, double Z) t = (100, 100);
        var good = new[]
        {
            S(0, 0, Dist(0, 0, t.X, t.Z), 1.0),
            S(300, 0, Dist(300, 0, t.X, t.Z), 1.0),
            S(0, 300, Dist(0, 300, t.X, t.Z), 1.0),
        };
        var withNoisyLowWeight = good.Append(S(150, -150, Dist(150, -150, t.X, t.Z) + 25, 0.05)).ToArray();

        var r = _solver.Solve(withNoisyLowWeight);

        r.Point!.Value.X.Should().BeApproximately(t.X, 2.0);
        r.Point.Value.Z.Should().BeApproximately(t.Z, 2.0);
    }

    [Fact]
    public void Near_collinear_geometry_is_gated_with_actionable_guidance()
    {
        (double X, double Z) t = (50, 500);
        // All three spots strung along the X axis → poor cross-range geometry.
        var s = new[]
        {
            S(0, 0, Dist(0, 0, t.X, t.Z)),
            S(100, 0, Dist(100, 0, t.X, t.Z)),
            S(200, 0, Dist(200, 0, t.X, t.Z)),
        };

        var r = _solver.Solve(s);

        r.Quality.Should().Be(MultilaterationQuality.LowConfidenceGeometry);
        r.Gdop.Should().BeGreaterThan(6.0);
        r.Guidance.Should().NotBeNullOrEmpty();
    }

    [Fact]
    public void Fewer_than_three_samples_is_insufficient()
    {
        var r = _solver.Solve(new[] { S(0, 0, 100), S(50, 0, 80) });

        r.Quality.Should().Be(MultilaterationQuality.Insufficient);
        r.Point.Should().BeNull();
        r.Guidance.Should().NotBeNullOrEmpty();
    }

    [Fact]
    public void Live_equal_distance_1285_over_1285_case_resolves()
    {
        // The acceptance case: two readings both 1285 m (the live capture that
        // tripped the old closed form). Three well-separated spots all 1285 m
        // from the target — equal radii, non-degenerate centres.
        (double X, double Z) t = (1285, 0);
        var s = new[]
        {
            S(0, 0, 1285),
            S(t.X + 1285 * Math.Cos(2.094), t.Z + 1285 * Math.Sin(2.094), 1285),
            S(t.X + 1285 * Math.Cos(-2.094), t.Z + 1285 * Math.Sin(-2.094), 1285),
        };

        var r = _solver.Solve(s);

        r.Quality.Should().Be(MultilaterationQuality.Solved);
        r.Point!.Value.X.Should().BeApproximately(t.X, 1e-2);
        r.Point.Value.Z.Should().BeApproximately(t.Z, 1e-2);
    }

    [Fact]
    public void N_motherlodes_decouple_over_a_shared_position_set()
    {
        var spots = new (double X, double Z)[] { (0, 0), (800, 0), (0, 800), (800, 800) };
        (double X, double Z) a = (200, 600);
        (double X, double Z) b = (650, 150);

        var ra = _solver.Solve(spots.Select(p => S(p.X, p.Z, Dist(p.X, p.Z, a.X, a.Z))).ToArray());
        var rb = _solver.Solve(spots.Select(p => S(p.X, p.Z, Dist(p.X, p.Z, b.X, b.Z))).ToArray());

        ra.Point!.Value.X.Should().BeApproximately(a.X, 1e-2);
        ra.Point.Value.Z.Should().BeApproximately(a.Z, 1e-2);
        rb.Point!.Value.X.Should().BeApproximately(b.X, 1e-2);
        rb.Point.Value.Z.Should().BeApproximately(b.Z, 1e-2);
    }
}
