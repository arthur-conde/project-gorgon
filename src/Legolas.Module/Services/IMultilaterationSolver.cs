using Legolas.Domain;

namespace Legolas.Services;

/// <summary>
/// One range observation for the solve: a known player world position
/// <c>(X, Z)</c> (ground plane, engine units), the noisy measured
/// <see cref="Distance"/> to the unknown target (ChatLog metres, ≈±0.5 m
/// quantization), and an inverse-variance <see cref="Weight"/> derived from the
/// position feeder's confidence. The solver is <b>source-agnostic</b> — it sees
/// only this tuple, never which feeder produced the position (#488).
/// </summary>
public readonly record struct MultilaterationSample(
    double X,
    double Z,
    double Distance,
    double Weight);

public enum MultilaterationQuality
{
    /// <summary>A confident fix: enough samples, acceptable geometry.</summary>
    Solved,

    /// <summary>Geometry is too poorly conditioned (near-collinear samples /
    /// high GDOP) to trust. <see cref="MultilaterationResult.Point"/> is still
    /// the best estimate but should be surfaced with the
    /// <see cref="MultilaterationResult.Guidance"/> hint, not navigated to.</summary>
    LowConfidenceGeometry,

    /// <summary>Fewer than three samples — range-only multilateration is
    /// underdetermined.</summary>
    Insufficient,

    /// <summary>The numerics failed (degenerate / non-finite) — no estimate.</summary>
    NoSolution,
}

/// <summary>
/// Result of one motherlode solve. <see cref="Point"/> is planar (Y = 0); the
/// caller projects it to overlay pixels via the area calibration only for
/// display. <see cref="Gdop"/> is the geometric dilution of precision (position
/// error ÷ range error; dimensionless). <see cref="ResidualRms"/> is the
/// weighted RMS of <c>‖M−Pᵢ‖−dᵢ</c> over the inliers — a large irreducible
/// value flags an elevation (2D-vs-3D) mismatch (#488, diagnostic not fix).
/// </summary>
public sealed record MultilaterationResult(
    WorldCoord? Point,
    double Gdop,
    double ResidualRms,
    IReadOnlyList<bool> Inliers,
    MultilaterationQuality Quality,
    string? Guidance);

/// <summary>
/// Range-only weighted nonlinear-least-squares multilateration (#488). Replaces
/// the retired closed-form 3-circle <c>TrilaterationSolver</c>: ≥3 unbounded
/// samples, closed-form linear initializer, weighted Gauss–Newton/LM refine,
/// RANSAC outlier rejection, and a GDOP gate. Solves in world space; n
/// motherlodes are independent solves over a shared sample set.
/// </summary>
public interface IMultilaterationSolver
{
    MultilaterationResult Solve(IReadOnlyList<MultilaterationSample> samples);
}
