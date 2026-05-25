using Arda.Abstractions.Logs;

namespace Arda.World.Player.Events;

/// <summary>
/// Emitted when the player interacts with any entity (NPC, chest, plant, object).
/// Downstream subscribers filter by <see cref="IsNpc"/> or name prefix.
/// </summary>
public readonly record struct InteractionStarted(
    long EntityId,
    string Name,
    double Favor,
    bool IsNpc,
    LogLineMetadata Metadata);
