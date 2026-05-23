using Mithril.WorldSim;

namespace Mithril.GameState.Areas;

/// <summary>
/// Folder-emitted change event: the player's area transitioned. Published on
/// the <see cref="IPlayerWorld"/> bus as <c>Frame&lt;PlayerAreaChanged&gt;</c>
/// (per <c>docs/world-simulator.md</c> "Decisions ratified post-#642") and
/// raised on <see cref="IPlayerAreaState.AreaChanged"/> for back-compat
/// callers that prefer the event surface.
///
/// <para>Emitted from <see cref="PlayerAreaTracker.Apply"/> whenever the
/// frame's <see cref="AreaLoadingFrame.AreaKey"/> differs from the prior
/// state; identical-area re-emits (zone-replay no-ops) produce no change
/// event.</para>
/// </summary>
/// <param name="Previous">The area key before this transition, or
/// <c>null</c> if the player was at character-select / disconnect / pre-first
/// observation.</param>
/// <param name="Current">The area key after this transition, or <c>null</c>
/// if the player just landed on character-select / disconnected.</param>
/// <param name="At">Event time the transition represents — the parsed
/// <c>LOADING LEVEL</c> line's timestamp, never wall-clock (principle 13).</param>
public sealed record PlayerAreaChanged(
    string? Previous,
    string? Current,
    DateTimeOffset At) : IChangeEvent;
