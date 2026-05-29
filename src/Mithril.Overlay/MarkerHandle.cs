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
///
/// <para><b>Opacity contract.</b> The underlying <c>Guid</c> identity and
/// the constructor are <see langword="internal"/> so consumers can only
/// receive handles from <see cref="IWorldOverlayMarkers.AddMarker"/>;
/// comparison via <c>==</c> / <see cref="Equals(MarkerHandle)"/> and
/// debugging via <see cref="ToString"/> stay public. Forging a handle from
/// outside the assembly is not part of the contract.</para>
/// </summary>
public readonly struct MarkerHandle : IEquatable<MarkerHandle>
{
    /// <summary>Stable per-process identity of the marker. Internal so the
    /// underlying <c>Guid</c> can't be fished out by consumers &#8212;
    /// handles must remain opaque value-tokens.</summary>
    internal Guid Id { get; }

    /// <summary>Internal so only the registry can mint handles. Consumers
    /// receive instances from <see cref="IWorldOverlayMarkers.AddMarker"/>.</summary>
    internal MarkerHandle(Guid id)
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
