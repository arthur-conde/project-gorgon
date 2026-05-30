using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using Mithril.MapCalibration;
using Mithril.Tools.MapCalibration.Common;

namespace Mithril.Tools.MapCalibration.Harness;

/// <summary>
/// The headless calibration engine: owns the live ref set, the solved
/// <see cref="Calibration"/>, and the projection overlay. <see cref="ReSolve"/>,
/// the per-ref residual math, and the projection refresh are lifted from the
/// merged <c>AreaWorkspaceViewModel</c> and made headless so they're unit-tested
/// for the first time. The solve goes through
/// <see cref="LandmarkCalibrationSolver.Solve"/> unchanged (it self-corrects
/// handedness by trying both reflections — don't bypass it).
///
/// <para>This type also doubles as the <see cref="ICandidateSink"/> a method
/// emits into: <see cref="ICandidateSink.Emit"/> routes through
/// <see cref="Accept"/>.</para>
/// </summary>
public sealed partial class CalibrationSession : ObservableObject, ICandidateSink
{
    private readonly CalibrationContext _ctx;

    public CalibrationSession(CalibrationContext ctx)
    {
        _ctx = ctx ?? throw new ArgumentNullException(nameof(ctx));
        Area = ctx.Area;
    }

    public string Area { get; }

    public ObservableCollection<CalibrationRef> Refs { get; } = new();

    [ObservableProperty]
    private AreaCalibration? _calibration;

    public ObservableCollection<ProjectionMarker> Projections { get; } = new();

    /// <summary>
    /// Materialises a <see cref="CalibrationRef"/> from a <see cref="CandidateRef"/>
    /// and re-solves. A candidate with no <see cref="CandidateRef.World"/> is
    /// added as a disabled ref (it carries no world coord, so it can't yet
    /// participate in the fit — the user assigns its world position later, then
    /// enables it).
    /// </summary>
    public void Accept(CandidateRef candidate)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        var hasWorld = candidate.World is not null;
        var calRef = new CalibrationRef
        {
            Name = candidate.SuggestedName ?? candidate.LandmarkId ?? "(unnamed)",
            Kind = candidate.Kind,
            Source = candidate.Source,
            Confidence = candidate.Confidence,
            World = candidate.World ?? default,
            TexturePixel = candidate.TexturePixel,
            Enabled = hasWorld,
        };
        Refs.Add(calRef);
        ReSolve();
    }

    /// <summary>Removes a ref from the live set and re-solves.</summary>
    public void Remove(CalibrationRef r)
    {
        if (r is null) return;
        if (Refs.Remove(r)) ReSolve();
    }

    /// <summary>
    /// Correction primitive: nudges a ref's <see cref="CalibrationRef.TexturePixel"/>
    /// by (<paramref name="dx"/>, <paramref name="dy"/>) and re-solves.
    /// </summary>
    public void NudgeSelected(CalibrationRef r, double dx, double dy)
    {
        if (r is null || !Refs.Contains(r)) return;
        r.TexturePixel = new PixelPoint(r.TexturePixel.X + dx, r.TexturePixel.Y + dy);
        ReSolve();
    }

    /// <summary>
    /// Solves the calibration from the <b>enabled</b> refs only, fills each
    /// enabled ref's <see cref="CalibrationRef.ResidualPx"/>, and refreshes the
    /// projection overlay for every landmark in the context. With fewer than two
    /// enabled refs the calibration is null, the projection overlay is cleared,
    /// and all residuals are nulled. Lifted verbatim from
    /// <c>AreaWorkspaceViewModel.ReSolve</c>.
    /// </summary>
    public void ReSolve()
    {
        var enabled = Refs.Where(r => r.Enabled).ToList();
        if (enabled.Count < 2)
        {
            Calibration = null;
            foreach (var r in Refs) r.ResidualPx = null;
            Projections.Clear();
            return;
        }

        var solverRefs = enabled
            .Select(r => new LandmarkCalibrationSolver.Reference(
                r.World.X, r.World.Z, r.TexturePixel))
            .ToList();
        var cal = LandmarkCalibrationSolver.Solve(solverRefs);
        Calibration = cal;

        if (cal is null)
        {
            foreach (var r in Refs) r.ResidualPx = null;
            Projections.Clear();
            return;
        }

        // Disabled refs carry no residual; enabled refs get the projected error.
        foreach (var r in Refs)
        {
            if (!r.Enabled)
            {
                r.ResidualPx = null;
                continue;
            }
            var projected = cal.WorldToWindow(r.World);
            var dx = projected.X - r.TexturePixel.X;
            var dy = projected.Y - r.TexturePixel.Y;
            r.ResidualPx = Math.Sqrt(dx * dx + dy * dy);
        }

        RefreshProjections(cal);
    }

    private void RefreshProjections(AreaCalibration cal)
    {
        Projections.Clear();
        foreach (var landmark in _ctx.Landmarks)
        {
            var px = cal.WorldToWindow(landmark.World);
            var refMatch = RefForLandmark(landmark);
            Projections.Add(new ProjectionMarker(
                landmark.Name, landmark.Type, px, refMatch?.ResidualPx));
        }
    }

    private CalibrationRef? RefForLandmark(LandmarkRef landmark) =>
        Refs.FirstOrDefault(r =>
            r.Enabled
            && Math.Abs(r.World.X - landmark.World.X) < 1e-6
            && Math.Abs(r.World.Z - landmark.World.Z) < 1e-6);

    void ICandidateSink.Emit(CandidateRef candidate) => Accept(candidate);

    void ICandidateSink.EmitBatch(IEnumerable<CandidateRef> candidates)
    {
        ArgumentNullException.ThrowIfNull(candidates);
        foreach (var c in candidates) Accept(c);
    }
}
