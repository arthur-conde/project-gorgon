using Mithril.MapCalibration;

namespace Mithril.Overlay.Tests.Fakes;

/// <summary>
/// Minimal test double for <see cref="IMapCalibrationService"/>. Maps every
/// world point to a pixel via <c>(x, z)</c> (i.e. identity transform). Areas
/// listed in <see cref="CalibratedAreas"/> are calibrated; all others
/// return null from both <c>WorldToWindow</c> and <c>IsCalibrated</c>.
/// </summary>
internal sealed class FakeMapCalibrationService : IMapCalibrationService
{
    public HashSet<string> CalibratedAreas { get; } = new(StringComparer.Ordinal);

    /// <summary>Optional override: project world (x, z) -&gt; pixel for a calibrated area.
    /// Default is identity (x -&gt; X, z -&gt; Y). Returning null from the override
    /// short-circuits the per-marker projection (exercises the null-skip
    /// branch in <c>OverlayWindowService.ProjectMarkers</c>).</summary>
    public Func<string, WorldCoord, double, PixelPoint?>? Projector { get; set; }

    public bool IsCalibrated(string areaKey) => CalibratedAreas.Contains(areaKey);

    public PixelPoint? WorldToWindow(string areaKey, WorldCoord world, double currentZoom)
    {
        if (!IsCalibrated(areaKey)) return null;
        return Projector is { } p ? p(areaKey, world, currentZoom) : new PixelPoint(world.X, world.Z);
    }

    public WorldCoord? WindowToWorld(string areaKey, PixelPoint pixel, double currentZoom) => null;
    public AreaCalibration? GetCalibration(string areaKey) => null;
    public IReadOnlyDictionary<string, AreaCalibration> AllCalibrations { get; } = new Dictionary<string, AreaCalibration>();
    public IReadOnlyList<AreaCalibration> GetAllSources(string areaKey) => Array.Empty<AreaCalibration>();
    public void SaveUserRefinement(string areaKey, AreaCalibration calibration) { }
    public void ClearUserRefinement(string areaKey) { }
    public int ImportUserRefinements(IReadOnlyDictionary<string, AreaCalibration> source) => 0;
    public event EventHandler<string>? Changed { add { } remove { } }
}
