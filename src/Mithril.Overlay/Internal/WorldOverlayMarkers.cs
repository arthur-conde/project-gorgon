using Microsoft.Extensions.Logging;
using Mithril.MapCalibration;

namespace Mithril.Overlay.Internal;

/// <summary>
/// Thread-safe marker registry behind <see cref="IWorldOverlayMarkers"/>.
/// Producers (any thread) call Add/Remove/Update; the renderer consumes a
/// snapshot of the current area on each tick. v1 keeps the implementation
/// lock-guarded: marker volume is bounded by Legolas/Gwaihir + a handful of
/// future consumers (tens to low hundreds), not a hot path that needs a
/// lock-free queue.
///
/// <para>Per-area scoping: each entry carries the area key it was registered
/// for. <see cref="CurrentAreaMarkers"/> filters by <see cref="CurrentArea"/>
/// at snapshot time &#8212; markers added for area A are silently absent when
/// the player is in area B, and re-appear without re-registration when the
/// player returns to A.</para>
/// </summary>
internal sealed class WorldOverlayMarkers : IWorldOverlayMarkers
{
    private readonly Dictionary<MarkerHandle, Entry> _markers = new();
    // O(N) on the insertion-order list for Remove; acceptable at expected
    // scale (low-hundreds markers per consumer). Promote to
    // LinkedList<MarkerHandle> + Dictionary<Handle, Node> if a consumer hits
    // 10k and the linear remove becomes hot in a perf trace.
    private readonly List<MarkerHandle> _insertionOrder = new();
    private readonly Lock _lock = new();
    private readonly ILogger? _logger;
    private bool _firstMarkerLogged;

    /// <summary>Current area key (drives the <see cref="CurrentAreaMarkers"/>
    /// filter). Setter is the projection driver's responsibility &#8212;
    /// updated each tick from <c>IAreaState</c>. <c>null</c> when the player
    /// is not in-world.</summary>
    public string? CurrentArea { get; set; }

    public WorldOverlayMarkers(ILogger? logger = null)
    {
        _logger = logger;
    }

    public MarkerHandle AddMarker(string areaKey, double worldX, double worldZ, IMarkerStyle style)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(areaKey);
        ArgumentNullException.ThrowIfNull(style);
        if (!double.IsFinite(worldX) || !double.IsFinite(worldZ))
            throw new ArgumentException(
                $"World coords must be finite; got ({worldX}, {worldZ}). NaN/Infinity would poison the per-area projection downstream.");

        var handle = new MarkerHandle(Guid.NewGuid());
        lock (_lock)
        {
            _markers[handle] = new Entry(areaKey, worldX, worldZ, style);
            _insertionOrder.Add(handle);
            if (!_firstMarkerLogged)
            {
                _firstMarkerLogged = true;
                _logger?.LogInformation(
                    "WorldOverlayMarkers: first marker registered for area {AreaKey} (style={StyleType})",
                    areaKey, style.GetType().Name);
            }
            else
            {
                _logger?.LogTrace(
                    "WorldOverlayMarkers: added marker {Handle} for area {AreaKey} (style={StyleType})",
                    handle, areaKey, style.GetType().Name);
            }
        }
        return handle;
    }

    public void RemoveMarker(MarkerHandle handle)
    {
        lock (_lock)
        {
            if (_markers.Remove(handle))
            {
                _insertionOrder.Remove(handle);
                _logger?.LogTrace("WorldOverlayMarkers: removed marker {Handle}", handle);
            }
        }
    }

    public void UpdateMarker(MarkerHandle handle, double worldX, double worldZ)
    {
        if (!double.IsFinite(worldX) || !double.IsFinite(worldZ))
            throw new ArgumentException(
                $"World coords must be finite; got ({worldX}, {worldZ}). NaN/Infinity would poison the per-area projection downstream.");

        lock (_lock)
        {
            if (!_markers.TryGetValue(handle, out var existing)) return;
            _markers[handle] = existing with { WorldX = worldX, WorldZ = worldZ };
            _logger?.LogTrace(
                "WorldOverlayMarkers: updated marker {Handle} -> ({WorldX}, {WorldZ})",
                handle, worldX, worldZ);
        }
    }

    public IReadOnlyList<MarkerSnapshot> CurrentAreaMarkers
    {
        get
        {
            var area = CurrentArea;
            if (string.IsNullOrEmpty(area)) return Array.Empty<MarkerSnapshot>();

            lock (_lock)
            {
                if (_insertionOrder.Count == 0)
                    return Array.Empty<MarkerSnapshot>();

                var result = new List<MarkerSnapshot>(_insertionOrder.Count);
                foreach (var handle in _insertionOrder)
                {
                    var entry = _markers[handle];
                    if (string.Equals(entry.AreaKey, area, StringComparison.Ordinal))
                        result.Add(new MarkerSnapshot(
                            handle,
                            new WorldCoord(entry.WorldX, 0, entry.WorldZ),
                            entry.Style));
                }
                return result;
            }
        }
    }

    /// <summary>Snapshot of every registered marker regardless of area &#8212;
    /// internal helper for diagnostics / Palantir surfaces; not part of the
    /// public <see cref="IWorldOverlayMarkers"/> contract.</summary>
    internal IReadOnlyList<(MarkerHandle Handle, string AreaKey, double WorldX, double WorldZ, IMarkerStyle Style)> AllMarkers
    {
        get
        {
            lock (_lock)
            {
                if (_insertionOrder.Count == 0)
                    return Array.Empty<(MarkerHandle, string, double, double, IMarkerStyle)>();
                var result = new List<(MarkerHandle, string, double, double, IMarkerStyle)>(_insertionOrder.Count);
                foreach (var handle in _insertionOrder)
                {
                    var entry = _markers[handle];
                    result.Add((handle, entry.AreaKey, entry.WorldX, entry.WorldZ, entry.Style));
                }
                return result;
            }
        }
    }

    private readonly record struct Entry(string AreaKey, double WorldX, double WorldZ, IMarkerStyle Style);
}
