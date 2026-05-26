namespace Arda.World.Player;

/// <summary>
/// Read-only view of the current NPC interaction context.
/// </summary>
public interface INpcState
{
    /// <summary>
    /// The entity key of the NPC currently being interacted with (e.g. "NPC_Joe"),
    /// or null if no interaction is active.
    /// </summary>
    string? ActiveNpcKey { get; }

    /// <summary>
    /// The entity ID of the current interaction target, or null if none active.
    /// </summary>
    long? ActiveEntityId { get; }

    /// <summary>
    /// The favor value reported at interaction start for the active NPC.
    /// </summary>
    double? ActiveFavor { get; }
}
