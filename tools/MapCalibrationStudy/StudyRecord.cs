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
