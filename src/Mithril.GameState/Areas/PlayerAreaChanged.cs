using Mithril.WorldSim;

namespace Mithril.GameState.Areas;

/// <summary>
/// Player area notification. Two shapes, distinguished by <see cref="Kind"/>:
///
/// <list type="bullet">
///   <item><see cref="PlayerAreaChangeKind.Snapshot"/> — synthetic replay
///   fired by <see cref="IPlayerAreaState.Subscribe"/> on attach so late
///   subscribers observe the same view already-attached handlers see. Never
///   published on the world bus.</item>
///   <item><see cref="PlayerAreaChangeKind.Changed"/> — genuine transition
///   observed from a <c>LOADING LEVEL</c> line. Folder-emitted from
///   <see cref="PlayerAreaTracker.Apply"/> and published on
///   <see cref="IPlayerWorld.Bus"/> as <c>Frame&lt;PlayerAreaChanged&gt;</c>.</item>
/// </list>
///
/// <para>Implements <see cref="IChangeEvent"/> so the world's bus routes
/// folder emissions through the same publish path as other folders' change
/// events (per the "Decisions ratified post-#642" section of
/// <c>docs/world-simulator.md</c>).</para>
/// </summary>
/// <param name="Kind">Discriminator — <see cref="PlayerAreaChangeKind.Snapshot"/>
/// for replay-on-attach, <see cref="PlayerAreaChangeKind.Changed"/> for a
/// genuine transition.</param>
/// <param name="Previous">The area key before this transition, or
/// <c>null</c> for the first observed transition / the
/// <see cref="PlayerAreaChangeKind.Snapshot"/> form.</param>
/// <param name="Current">The area key after this transition (or the
/// current area on a snapshot replay), or <c>null</c> if the player is at
/// character-select / disconnected / pre-first observation.</param>
/// <param name="At">Event time the transition represents — the parsed
/// <c>LOADING LEVEL</c> line's timestamp (NEVER wall-clock — principle 13).
/// On a snapshot replay, carries the most-recent envelope timestamp the
/// tracker has applied; <see cref="DateTimeOffset.MinValue"/> when nothing
/// has been observed yet.</param>
public sealed record PlayerAreaChanged(
    PlayerAreaChangeKind Kind,
    string? Previous,
    string? Current,
    DateTimeOffset At) : IChangeEvent;
