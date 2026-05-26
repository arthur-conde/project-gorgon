namespace Mithril.GameState.Areas;

/// <summary>
/// Read surface for the Player.log area folder
/// (<see cref="PlayerAreaTracker"/>) — #775. Matches the world-sim folder-
/// interface convention (<see cref="Mithril.WorldSim.IFolder{TPayload}"/>:
/// <c>I&lt;World&gt;&lt;Domain&gt;State</c>); dual-surface delivery alongside
/// the world bus channel — new consumers may subscribe to
/// <c>IPlayerWorld.Bus.Subscribe&lt;PlayerAreaChanged&gt;</c> directly per
/// design notebook principle 4 (single-world consumers).
///
/// <para><b>Three consumption surfaces, one truth.</b>
/// <list type="bullet">
///   <item><see cref="CurrentArea"/> — synchronous read. Preserved verbatim
///   for the chest-area-stamping consumer (Gandalf), the position-driven
///   debug refresh (Palantir), and the startup area bridge (Legolas).</item>
///   <item><see cref="Subscribe"/> — replay-on-attach callback. Late
///   subscribers receive a synthetic
///   <see cref="PlayerAreaChangeKind.Snapshot"/> notification with the
///   current area, then live <see cref="PlayerAreaChangeKind.Changed"/>
///   notifications on subsequent transitions. Mirrors the pattern in
///   <c>PlayerPinTracker.Subscribe</c> / <c>PlayerWeatherTracker.Subscribe</c>.</item>
///   <item><c>IPlayerWorld.Bus.Subscribe&lt;PlayerAreaChanged&gt;</c> —
///   typed bus channel. Carries only <see cref="PlayerAreaChangeKind.Changed"/>
///   events (the snapshot replay is synthesized inside
///   <see cref="Subscribe"/> and never crosses the world boundary). New
///   cross-cutting consumers should prefer this surface; the bus delivery
///   path is automatic via the folder's <c>Apply</c> return value.</item>
/// </list></para>
///
/// <para>The legacy <see cref="PlayerAreaTracker.Observe"/> push-in path
/// also stays alive for the Legolas + Gandalf bridge consumers that still
/// feed already-classified lines inline; the cross-source-correlation rules
/// continue to apply (double-feed is idempotent under last-writer-wins).</para>
/// </summary>
[Obsolete("Use Arda.World.Player.IMapState (area, position, weather, pins consolidated) instead.")]
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
    /// Subscribe to area notifications. Late subscribers receive a
    /// synthetic <see cref="PlayerAreaChangeKind.Snapshot"/> notification
    /// with the current area before this method returns; subsequent
    /// genuine transitions deliver as
    /// <see cref="PlayerAreaChangeKind.Changed"/> notifications on the
    /// folder's apply thread. Disposing the returned handle removes the
    /// subscription.
    ///
    /// <para>Subscribers must not block — the change-event path runs on
    /// the world's merger thread (or, for the legacy
    /// <see cref="PlayerAreaTracker.Observe"/> bridge, the calling
    /// ingestion thread). Non-trivial / UI work must marshal off.</para>
    /// </summary>
    IDisposable Subscribe(Action<PlayerAreaChanged> handler);
}
