using Microsoft.Extensions.Logging;
using Legolas.Domain;
using Mithril.Shared.Diagnostics;

namespace Legolas.Services;

/// <summary>
/// Range-only weighted NLS multilateration (#488). Pipeline: closed-form linear
/// initializer → RANSAC consensus over minimal sets → weighted Gauss–Newton/LM
/// refine on the inliers → GDOP gate. Hand-rolled (no NuGet — matches
/// <c>CoordinateProjector.Refit</c>'s closed-form style). At exactly 3
/// non-degenerate samples it degenerates to the old 3-circle answer; strictly
/// ≥ the retired closed form everywhere else.
///
/// <para>The constants below are #488's "decide during build" knobs — chosen
/// from the noise model (integer-metre quantization ≈ ±0.5 m; the practical
/// error budget is position-feeder error, not distance). Tune empirically with
/// field data; they are intentionally conservative.</para>
/// </summary>
public sealed class MultilaterationSolver : IMultilaterationSolver
{
    // ChatLog distance is integer metres → ±0.5 m quantization noise floor.
    private const double QuantizationFloorMetres = 0.5;

    // RANSAC inlier half-band. Must clear the quantization floor with margin
    // for feeder-position error; 3 m ≈ 6× the floor. Open knob (#488).
    private const double InlierBandMetres = 3.0;

    // Position error ÷ range error. >6 means the geometry can't pin the target
    // however good the ranges are (near-collinear samples). Open knob (#488).
    private const double GdopRefuseThreshold = 6.0;

    private const int MaxGaussNewtonIterations = 60;
    private const double ConvergenceMetres = 1e-7;
    private const int MaxRansacTrials = 256;

    // Deterministic RANSAC so the solve is reproducible (tests + same input →
    // same output). Geometry, not luck, decides the result.
    private const int RansacSeed = 488;

    private readonly ILogger? _logger;

    public MultilaterationSolver(ILogger? logger = null) => _logger = logger;

    public MultilaterationResult Solve(IReadOnlyList<MultilaterationSample> samples)
    {
        ArgumentNullException.ThrowIfNull(samples);

        if (samples.Count < 3)
        {
            _logger?.LogDiagnosticTrace("Legolas.Multilateration",
                $"Insufficient samples: {samples.Count} (need ≥3).");
            return new MultilaterationResult(
                null, double.PositiveInfinity, double.PositiveInfinity,
                AllFalse(samples.Count), MultilaterationQuality.Insufficient,
                "Take at least 3 distance readings from well-separated spots.");
        }

        var band = Math.Max(InlierBandMetres, 2 * QuantizationFloorMetres);

        // ---- RANSAC consensus (skipped at n==3: no redundancy to reject) ----
        bool[] inliers;
        if (samples.Count == 3)
        {
            inliers = new[] { true, true, true };
        }
        else
        {
            inliers = BestConsensus(samples, band)
                      ?? Enumerable.Repeat(true, samples.Count).ToArray();
        }

        var inlierList = new List<MultilaterationSample>(samples.Count);
        for (var i = 0; i < samples.Count; i++)
            if (inliers[i]) inlierList.Add(samples[i]);

        // A consensus that collapsed below 3 can't be solved on its own; fall
        // back to the full set so we still return a (low-confidence) estimate.
        if (inlierList.Count < 3)
        {
            inlierList.Clear();
            inlierList.AddRange(samples);
            for (var i = 0; i < inliers.Length; i++) inliers[i] = true;
        }

        // ---- Initialize + refine ----
        if (LinearInitialize(inlierList) is not { } init)
            init = Centroid(inlierList);

        if (GaussNewton(inlierList, init) is not { } fix)
        {
            _logger?.LogDiagnosticWarn("Legolas.Multilateration", "Gauss–Newton produced no finite estimate.");
            return new MultilaterationResult(
                null, double.PositiveInfinity, double.PositiveInfinity,
                inliers, MultilaterationQuality.NoSolution,
                "Couldn't resolve a location — readings may be inconsistent.");
        }

        var residualRms = WeightedResidualRms(inlierList, fix);
        var gdop = Gdop(inlierList, fix);
        var point = new WorldCoord(fix.X, 0, fix.Z);

        if (!double.IsFinite(gdop) || gdop > GdopRefuseThreshold)
        {
            _logger?.LogDiagnosticInfo("Legolas.Multilateration",
                $"Low-confidence geometry: GDOP={gdop:0.0}, residRMS={residualRms:0.0}m, n={inlierList.Count}.");
            return new MultilaterationResult(
                point, gdop, residualRms, inliers,
                MultilaterationQuality.LowConfidenceGeometry,
                "Poor measurement geometry — take another reading from a spot " +
                "roughly perpendicular to the others (≥30 m away), then re-check.");
        }

        _logger?.LogDiagnosticTrace("Legolas.Multilateration",
            $"Solved ({fix.X:0.0},{fix.Z:0.0}) GDOP={gdop:0.00} residRMS={residualRms:0.00}m " +
            $"inliers={inlierList.Count}/{samples.Count}.");
        return new MultilaterationResult(
            point, gdop, residualRms, inliers, MultilaterationQuality.Solved, null);
    }

    // ---- RANSAC: best 3-subset consensus ---------------------------------

    private static bool[]? BestConsensus(IReadOnlyList<MultilaterationSample> s, double band)
    {
        var n = s.Count;
        var best = (bool[]?)null;
        var bestCount = 2;            // require strictly >2 to accept a model
        var bestResidual = double.PositiveInfinity;

        void Consider(int a, int b, int c)
        {
            var minimal = new[] { s[a], s[b], s[c] };
            if (LinearInitialize(minimal) is not { } init) return;
            if (GaussNewton(minimal, init) is not { } fix) return;

            var mask = new bool[n];
            var count = 0;
            var resid = 0.0;
            for (var i = 0; i < n; i++)
            {
                var e = Math.Abs(Range(fix, s[i]) - s[i].Distance);
                if (e <= band) { mask[i] = true; count++; resid += e; }
            }
            if (count > bestCount || (count == bestCount && resid < bestResidual))
            {
                best = mask;
                bestCount = count;
                bestResidual = resid;
            }
        }

        // Exhaustive for small n (the common case — a hunt is a handful of
        // spots); deterministic random sampling once the triple count blows up.
        long triples = (long)n * (n - 1) * (n - 2) / 6;
        if (triples <= MaxRansacTrials)
        {
            for (var a = 0; a < n - 2; a++)
                for (var b = a + 1; b < n - 1; b++)
                    for (var c = b + 1; c < n; c++)
                        Consider(a, b, c);
        }
        else
        {
            var rng = new Random(RansacSeed);
            for (var t = 0; t < MaxRansacTrials; t++)
            {
                int a = rng.Next(n), b = rng.Next(n), c = rng.Next(n);
                if (a == b || b == c || a == c) continue;
                Consider(a, b, c);
            }
        }
        return best;
    }

    // ---- Closed-form linear initializer ----------------------------------
    // Subtract circle equations against a reference sample → an overdetermined
    // linear system A·m = rhs; solve via the 2×2 normal equations. This is the
    // retired 3-circle algebra demoted to an initializer, generalized to n.

    private static (double X, double Z)? LinearInitialize(IReadOnlyList<MultilaterationSample> s)
    {
        var r = 0;
        var xr = s[r].X; var zr = s[r].Z; var dr = s[r].Distance;
        double ata00 = 0, ata01 = 0, ata11 = 0, atb0 = 0, atb1 = 0;

        for (var i = 0; i < s.Count; i++)
        {
            if (i == r) continue;
            var a0 = 2 * (s[i].X - xr);
            var a1 = 2 * (s[i].Z - zr);
            var rhs = (s[i].X * s[i].X - xr * xr)
                    + (s[i].Z * s[i].Z - zr * zr)
                    - (s[i].Distance * s[i].Distance - dr * dr);
            ata00 += a0 * a0; ata01 += a0 * a1; ata11 += a1 * a1;
            atb0 += a0 * rhs; atb1 += a1 * rhs;
        }

        var det = ata00 * ata11 - ata01 * ata01;
        if (Math.Abs(det) < 1e-9) return null;   // collinear reference geometry
        var x = (atb0 * ata11 - ata01 * atb1) / det;
        var z = (ata00 * atb1 - atb0 * ata01) / det;
        return double.IsFinite(x) && double.IsFinite(z) ? (x, z) : null;
    }

    private static (double X, double Z) Centroid(IReadOnlyList<MultilaterationSample> s)
    {
        double x = 0, z = 0;
        foreach (var p in s) { x += p.X; z += p.Z; }
        return (x / s.Count, z / s.Count);
    }

    // ---- Weighted Gauss–Newton with Levenberg–Marquardt damping ----------

    private static (double X, double Z)? GaussNewton(
        IReadOnlyList<MultilaterationSample> s, (double X, double Z) init)
    {
        var mx = init.X; var mz = init.Z;
        var lambda = 1e-3;
        var cost = Cost(s, mx, mz);

        for (var iter = 0; iter < MaxGaussNewtonIterations; iter++)
        {
            double h00 = 0, h01 = 0, h11 = 0, g0 = 0, g1 = 0;
            foreach (var p in s)
            {
                var dx = mx - p.X;
                var dz = mz - p.Z;
                var range = Math.Sqrt(dx * dx + dz * dz);
                if (range < 1e-9) range = 1e-9;          // guard a sample at M
                var jx = dx / range;
                var jz = dz / range;
                var res = range - p.Distance;
                var w = p.Weight > 0 ? p.Weight : 1.0;
                h00 += w * jx * jx; h01 += w * jx * jz; h11 += w * jz * jz;
                g0 += w * jx * res; g1 += w * jz * res;
            }

            // LM: damp the diagonal, accept only if cost improves.
            for (var attempt = 0; attempt < 12; attempt++)
            {
                var a00 = h00 * (1 + lambda);
                var a11 = h11 * (1 + lambda);
                var det = a00 * a11 - h01 * h01;
                if (Math.Abs(det) < 1e-12) { lambda *= 4; continue; }
                var stepX = -(g0 * a11 - h01 * g1) / det;
                var stepZ = -(a00 * g1 - g0 * h01) / det;
                var nx = mx + stepX;
                var nz = mz + stepZ;
                if (!double.IsFinite(nx) || !double.IsFinite(nz)) { lambda *= 4; continue; }

                var newCost = Cost(s, nx, nz);
                if (newCost <= cost)
                {
                    mx = nx; mz = nz;
                    var moved = Math.Sqrt(stepX * stepX + stepZ * stepZ);
                    cost = newCost;
                    lambda = Math.Max(lambda * 0.4, 1e-9);
                    if (moved < ConvergenceMetres)
                        return double.IsFinite(mx) && double.IsFinite(mz) ? (mx, mz) : null;
                    break;
                }
                lambda *= 4;
                if (attempt == 11)
                    return double.IsFinite(mx) && double.IsFinite(mz) ? (mx, mz) : null;
            }
        }
        return double.IsFinite(mx) && double.IsFinite(mz) ? (mx, mz) : null;
    }

    private static double Cost(IReadOnlyList<MultilaterationSample> s, double mx, double mz)
    {
        var c = 0.0;
        foreach (var p in s)
        {
            var r = Math.Sqrt((mx - p.X) * (mx - p.X) + (mz - p.Z) * (mz - p.Z)) - p.Distance;
            var w = p.Weight > 0 ? p.Weight : 1.0;
            c += w * r * r;
        }
        return c;
    }

    private static double WeightedResidualRms(
        IReadOnlyList<MultilaterationSample> s, (double X, double Z) m)
    {
        double sw = 0, swr2 = 0;
        foreach (var p in s)
        {
            var r = Range(m, p) - p.Distance;
            var w = p.Weight > 0 ? p.Weight : 1.0;
            sw += w; swr2 += w * r * r;
        }
        return sw > 0 ? Math.Sqrt(swr2 / sw) : double.PositiveInfinity;
    }

    // GDOP from the unweighted line-of-sight geometry: G = Σ JᵢᵀJᵢ with unit
    // Jᵢ, so √trace(G⁻¹) is the classic dimensionless dilution.
    private static double Gdop(IReadOnlyList<MultilaterationSample> s, (double X, double Z) m)
    {
        double g00 = 0, g01 = 0, g11 = 0;
        foreach (var p in s)
        {
            var dx = m.X - p.X;
            var dz = m.Z - p.Z;
            var range = Math.Sqrt(dx * dx + dz * dz);
            if (range < 1e-9) continue;
            var jx = dx / range;
            var jz = dz / range;
            g00 += jx * jx; g01 += jx * jz; g11 += jz * jz;
        }
        var det = g00 * g11 - g01 * g01;
        if (Math.Abs(det) < 1e-12) return double.PositiveInfinity;
        var cov00 = g11 / det;
        var cov11 = g00 / det;
        var trace = cov00 + cov11;
        return trace >= 0 ? Math.Sqrt(trace) : double.PositiveInfinity;
    }

    private static double Range((double X, double Z) m, MultilaterationSample p) =>
        Math.Sqrt((m.X - p.X) * (m.X - p.X) + (m.Z - p.Z) * (m.Z - p.Z));

    private static bool[] AllFalse(int n) => new bool[n];
}
