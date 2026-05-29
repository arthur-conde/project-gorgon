namespace Mithril.Tools.MapCalibrationWpf.Services;

using Mithril.MapCalibration;
using Mithril.Tools.MapCalibration.Common;

/// <summary>
/// Stamps the solved <see cref="AreaCalibration"/> with the BundledBaseline
/// metadata and writes it to <c>src/Mithril.MapCalibration/BundledData/
/// map-calibration-baseline.json</c> via <see cref="BaselineFile.UpsertAnchor"/>.
///
/// <para>The same path the CLI writes to (resolved via <see cref="RepoPaths"/>)
/// so the WPF tool's commit lands exactly where the existing baseline test
/// suite reads from.</para>
/// </summary>
public sealed class WorkspaceCommitService
{
    public void Commit(string area, AreaCalibration calibration)
    {
        var stamped = calibration with
        {
            Source = CalibrationSource.BundledBaseline,
            CalibrationZoom = 1.0,
            SchemaVersion = 1,
        };
        BaselineFile.UpsertAnchor(RepoPaths.BaselineJsonPath(), area, stamped);
    }

    /// <summary>
    /// Reads the currently-stored anchor for <paramref name="area"/>. Returns
    /// null when no anchor exists yet — the workspace treats that as "dirty"
    /// (commit-enabled).
    /// </summary>
    public AreaCalibration? ReadStored(string area)
    {
        try
        {
            return BaselineFile.TryReadAnchor(RepoPaths.BaselineJsonPath(), area);
        }
        catch (UserFacingException)
        {
            // Baseline file missing (running outside the repo); pretend "no
            // stored anchor" so the commit button surfaces a useful error
            // when the user clicks it.
            return null;
        }
    }
}
