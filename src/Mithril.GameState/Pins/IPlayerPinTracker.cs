namespace Mithril.GameState.Pins;

/// <summary>How a <see cref="PinSetChanged"/> notification arose.</summary>
public enum PinSetChange
{
    /// <summary>Replayed synchronously to a new subscriber — the full
    /// current-area set as it already stands (mirrors
    /// <c>IPlayerPositionTracker.Subscribe</c>'s replay).</summary>
    Snapshot,

    /// <summary>A single pin was placed (a genuinely new coordinate, or a
    /// changed label/appearance at an existing one). Idempotent re-adds from
    /// a login / area-entry replay burst do <b>not</b> raise this.</summary>
    Added,

    /// <summary>A single pin was removed.</summary>
    Removed,

    /// <summary>The player changed area: the previous area's set was dropped
    /// and <see cref="PinSetChanged.Pins"/> is the new (initially empty)
    /// set.</summary>
    AreaChanged,
}

/// <summary>
/// A change to the current area's pin set. <see cref="Pins"/> is always the
/// full set <em>after</em> the change (an immutable snapshot, safe to hold);
/// <see cref="Pin"/> is the single affected pin for
/// <see cref="PinSetChange.Added"/>/<see cref="PinSetChange.Removed"/> and
/// <c>null</c> otherwise. <see cref="ObservedAt"/> is the source log line's
/// UTC instant as a <see cref="DateTimeOffset"/> (Snapshot replays use the
/// subscribe instant).
/// </summary>
/// <param name="Kind">Why this notification arose.</param>
/// <param name="Area">The area key the set belongs to (may be <c>null</c> if
/// the player's area is unknown).</param>
/// <param name="Pin">The single affected pin for
/// <see cref="PinSetChange.Added"/>/<see cref="PinSetChange.Removed"/>;
/// <c>null</c> for <see cref="PinSetChange.Snapshot"/> /
/// <see cref="PinSetChange.AreaChanged"/>.</param>
/// <param name="Pins">The full current-area set <em>after</em> the change —
/// an immutable snapshot, safe to retain.</param>
/// <param name="ObservedAt">UTC instant of the source log line (or the
/// subscribe instant for a <see cref="PinSetChange.Snapshot"/> replay).</param>
public sealed record PinSetChanged(
    PinSetChange Kind,
    string? Area,
    MapPin? Pin,
    IReadOnlyList<MapPin> Pins,
    DateTimeOffset ObservedAt);

/// <summary>
/// Shared live game-state: the local player's <b>area-scoped</b> map-pin set,
/// owned authoritatively here (#468). Mirrors
/// <see cref="Mithril.GameState.Movement.IPlayerPositionTracker"/> — a current
/// snapshot plus a replay-on-<see cref="Subscribe"/> handler so late
/// subscribers see the same view already-attached ones do.
///
/// <para><b>Why the service owns the set.</b> <c>ProcessMapPinAdd</c>
/// bulk-replays on every login / area entry and there is no clear/edit verb.
/// Centralising lifecycle here (replay = idempotent upsert keyed by
/// coordinate, area transition = swap) means every consumer reads a correct
/// current set instead of each re-deriving it behind its own arm-gate.</para>
/// </summary>
public interface IPlayerPinTracker
{
    /// <summary>The area the tracked set belongs to (the shared
    /// <c>PlayerAreaTracker</c> key), or <c>null</c> if unknown.</summary>
    string? CurrentArea { get; }

    /// <summary>The current area's pins — an immutable snapshot, empty before
    /// the first observed pin / after an area change.</summary>
    IReadOnlyList<MapPin> CurrentAreaPins { get; }

    /// <summary>
    /// Register a handler. The current set is replayed synchronously as a
    /// <see cref="PinSetChange.Snapshot"/> before the call returns; subsequent
    /// changes are delivered live until the returned token is disposed.
    /// Handlers run on the ingestion thread — marshal off-thread for
    /// non-trivial / UI work (mirrors the position tracker's contract).
    /// </summary>
    IDisposable Subscribe(Action<PinSetChanged> handler);
}
