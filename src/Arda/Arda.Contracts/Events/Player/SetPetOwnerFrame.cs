using Arda.Abstractions.Logs;

namespace Arda.World.Player.Events;

public readonly record struct SetPetOwnerFrame(
    long EntityId,
    LogLineMetadata Metadata);
