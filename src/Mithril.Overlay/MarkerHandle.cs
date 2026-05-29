namespace Mithril.Overlay;

/// <summary>
/// World-coord marker handle. Returned by
/// <see cref="IWorldOverlayMarkers.AddMarker"/>; passed back to
/// <see cref="IWorldOverlayMarkers.RemoveMarker"/> /
/// <see cref="IWorldOverlayMarkers.UpdateMarker"/>. Opaque to consumers
/// &#8212; the registry chooses how to materialise the id.
///
/// <para>A <see langword="readonly"/> struct rather than a record struct so
/// the surface stays minimal (no synthesized <c>ToString</c> / deconstruct
/// / value-based equality boilerplate the contract doesn't promise).</para>
/// </summary>
public readonly struct MarkerHandle : IEquatable<MarkerHandle>
{
    /// <summary>Stable per-process identity of the marker.</summary>
    public Guid Id { get; }

    public MarkerHandle(Guid id)
    {
        Id = id;
    }

    public bool Equals(MarkerHandle other) => Id.Equals(other.Id);

    public override bool Equals(object? obj) => obj is MarkerHandle other && Equals(other);

    public override int GetHashCode() => Id.GetHashCode();

    public override string ToString() => $"MarkerHandle({Id:N})";

    public static bool operator ==(MarkerHandle left, MarkerHandle right) => left.Equals(right);

    public static bool operator !=(MarkerHandle left, MarkerHandle right) => !left.Equals(right);
}
