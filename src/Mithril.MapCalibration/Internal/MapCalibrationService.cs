using Microsoft.Extensions.Logging;

namespace Mithril.MapCalibration.Internal;

/// <summary>
/// Default <see cref="IMapCalibrationService"/> implementation. Composes the
/// bundled baseline catalogue with the per-user refinement store and resolves
/// the active transform per the precedence rules documented on
/// <see cref="IMapCalibrationService"/>.
///
/// <para>The service is thread-safe: reads take a short lock against the
/// refinement-store dictionary; writes go through <see cref="UserRefinementStore"/>
/// (which serialises persistence under its own lock).</para>
/// </summary>
internal sealed class MapCalibrationService : IMapCalibrationService
{
    private readonly IReadOnlyDictionary<string, AreaCalibration> _baseline;
    private readonly UserRefinementStore _userStore;
    private readonly double _goodResidualThresholdPx;
    private readonly ILogger? _logger;
    private readonly object _eventGate = new();

    public MapCalibrationService(
        IReadOnlyDictionary<string, AreaCalibration> baseline,
        UserRefinementStore userStore,
        double goodResidualThresholdPx,
        ILogger? logger = null)
    {
        _baseline = baseline;
        _userStore = userStore;
        _goodResidualThresholdPx = goodResidualThresholdPx;
        _logger = logger;
    }

    public event EventHandler<string>? Changed;

    public bool IsCalibrated(string areaKey) => GetCalibration(areaKey) is not null;

    public AreaCalibration? GetCalibration(string areaKey)
    {
        if (string.IsNullOrWhiteSpace(areaKey)) return null;

        if (_userStore.TryGet(areaKey, out var user)
            && user.ResidualPixels <= _goodResidualThresholdPx)
        {
            return user;
        }

        // CommunitySync slot reserved here; not yet wired.

        if (_baseline.TryGetValue(areaKey, out var baseline)) return baseline;

        // A user refinement above the threshold loses to a usable baseline.
        // When no baseline exists, fall back to the user refinement anyway —
        // a high-residual transform is better than nothing for the consumer's
        // degradation UX (chip + render).
        if (_userStore.TryGet(areaKey, out var fallbackUser)) return fallbackUser;

        return null;
    }

    public PixelPoint? WorldToWindow(string areaKey, WorldCoord world, double currentZoom) =>
        GetCalibration(areaKey)?.WorldToWindow(world, currentZoom);

    public WorldCoord? WindowToWorld(string areaKey, PixelPoint pixel, double currentZoom) =>
        GetCalibration(areaKey)?.WindowToWorld(pixel, currentZoom);

    public IReadOnlyDictionary<string, AreaCalibration> AllCalibrations
    {
        get
        {
            // Union of areas across both stores; the active record is whichever
            // source GetCalibration would pick for each.
            var keys = new HashSet<string>(_baseline.Keys, StringComparer.Ordinal);
            foreach (var key in _userStore.All.Keys) keys.Add(key);
            var result = new Dictionary<string, AreaCalibration>(keys.Count, StringComparer.Ordinal);
            foreach (var key in keys)
            {
                if (GetCalibration(key) is { } cal) result[key] = cal;
            }
            return result;
        }
    }

    public IReadOnlyList<AreaCalibration> GetAllSources(string areaKey)
    {
        if (string.IsNullOrWhiteSpace(areaKey)) return Array.Empty<AreaCalibration>();

        var sources = new List<AreaCalibration>(capacity: 2);
        if (_userStore.TryGet(areaKey, out var user)) sources.Add(user);
        if (_baseline.TryGetValue(areaKey, out var baseline)) sources.Add(baseline);
        // CommunitySync slot reserved here; not yet wired.
        return sources;
    }

    public void SaveUserRefinement(string areaKey, AreaCalibration calibration)
    {
        if (string.IsNullOrWhiteSpace(areaKey))
            throw new ArgumentException("areaKey required", nameof(areaKey));
        ArgumentNullException.ThrowIfNull(calibration);

        _userStore.Save(areaKey, calibration);
        _logger?.LogInformation("Saved user refinement for {AreaKey} (residual {Residual:F2}px, references {Count}).",
            areaKey, calibration.ResidualPixels, calibration.ReferenceCount);
        RaiseChanged(areaKey);
    }

    public void ClearUserRefinement(string areaKey)
    {
        if (string.IsNullOrWhiteSpace(areaKey)) return;
        if (_userStore.Remove(areaKey))
        {
            _logger?.LogInformation("Cleared user refinement for {AreaKey}.", areaKey);
            RaiseChanged(areaKey);
        }
    }

    private void RaiseChanged(string areaKey)
    {
        EventHandler<string>? handler;
        lock (_eventGate) handler = Changed;
        handler?.Invoke(this, areaKey);
    }
}
