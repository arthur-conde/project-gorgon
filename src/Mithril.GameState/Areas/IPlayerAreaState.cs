namespace Mithril.GameState.Areas;

/// <summary>
/// Read surface for the Player.log area folder (<see cref="PlayerAreaTracker"/>) —
/// #775. Matches the world-sim folder-interface convention
/// (<see cref="Mithril.WorldSim.IFolder{TPayload}"/>: <c>I&lt;World&gt;&lt;Domain&gt;State</c>);
/// dual-surface delivery alongside the world bus channel — new consumers may
/// subscribe to <c>IPlayerWorld.Bus.Subscribe&lt;PlayerAreaChanged&gt;</c>
/// directly per design notebook principle 4 (single-world consumers).
///
/// <para><b>Synchronous read preserved.</b> <see cref="CurrentArea"/> stays
/// available for the chest-area-stamping consumer (Gandalf) and the position-
/// driven debug refresh (Palantir): both depend on a poll-on-demand surface and
/// neither needs the change event. The legacy
/// <see cref="PlayerAreaTracker.Observe(string, DateTime)"/> push-in path also
/// stays alive for the Legolas + Gandalf bridge consumers that still feed
/// already-classified lines inline; the cross-source-correlation rules
/// continue to apply (double-feed is idempotent under last-writer-wins).</para>
/// </summary>
public interface IPlayerAreaState
{
    /// <summary>
    /// Latest area key parsed from a <c>LOADING LEVEL Area*</c> line, or
    /// <c>null</c> if the player is at character-select / disconnected /
    /// before the first observed transition. Consumers should treat
    /// <c>null</c> as "current area is unknown" — chest commits during a
    /// null-area window persist with <c>Area = null</c> and self-heal on
    /// the next portal.
    /// </summary>
    string? CurrentArea { get; }

    /// <summary>
    /// Raised whenever the folder applies a frame whose
    /// <see cref="AreaLoadingFrame.AreaKey"/> differs from the prior state.
    /// Identical-area re-emits (zone replay) produce no event. Fires
    /// synchronously inside <see cref="PlayerAreaTracker.Apply"/> on the
    /// world's merger thread — subscribers must not block; non-trivial / UI
    /// work must marshal off.
    /// </summary>
    event EventHandler<PlayerAreaChanged>? AreaChanged;
}
