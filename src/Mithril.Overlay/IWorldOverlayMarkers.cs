namespace Mithril.Overlay;

/// <summary>
/// World-coord marker registry. Producers (any thread) call
/// <see cref="AddMarker"/> / <see cref="RemoveMarker"/> /
/// <see cref="UpdateMarker"/>; the overlay renderer (on the dispatcher)
/// consumes <see cref="CurrentAreaMarkers"/> per tick. Implementations must
/// be thread-safe under contention.
///
/// <para>Markers are area-scoped &#8212; the projection has no
/// source-of-truth for cross-area transforms (each area carries its own
/// per-area similarity from <c>IMapCalibrationService</c>). A marker
/// registered for area A is silently absent from
/// <see cref="CurrentAreaMarkers"/> while the player is in area B; re-entering
/// A surfaces it again without re-registration.</para>
/// </summary>
public interface IWorldOverlayMarkers
{
    /// <summary>
    /// Add a world-coord marker. <paramref name="areaKey"/> is the Arda
    /// internal area key (e.g. <c>"AreaEltibule"</c>); the marker is only
    /// drawn while the player is in that area. <paramref name="style"/> is
    /// the consumer-registered opaque style handle dispatched by the
    /// renderer.
    /// </summary>
    /// <returns>A handle for later <see cref="RemoveMarker"/> /
    /// <see cref="UpdateMarker"/> calls.</returns>
    MarkerHandle AddMarker(string areaKey, double worldX, double worldZ, IMarkerStyle style);

    /// <summary>Remove a previously-added marker. No-op if already removed
    /// (or never registered).</summary>
    void RemoveMarker(MarkerHandle handle);

    /// <summary>
    /// Update a marker's world position in place (e.g. a manual nudge
    /// correction). No-op if the handle is unknown.
    /// </summary>
    void UpdateMarker(MarkerHandle handle, double worldX, double worldZ);

    /// <summary>
    /// Snapshot of all currently-registered markers in the current area, in
    /// insertion order. For the renderer to enumerate per tick and for
    /// overlay-state inspection (e.g. Palantir debug surfacing). The snapshot
    /// is a value-time copy &#8212; safe to enumerate without holding a lock.
    /// Empty when no markers are registered, when the player is outside any
    /// area registered to a marker, or when the current area is unknown.
    /// </summary>
    IReadOnlyList<(MarkerHandle Handle, double WorldX, double WorldZ, IMarkerStyle Style)> CurrentAreaMarkers { get; }
}
