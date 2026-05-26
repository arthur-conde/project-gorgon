using Arda.Abstractions.Logs;

namespace Arda.World.Player.Events;

/// <summary>
/// Emitted when the full quest journal is loaded via
/// <c>ProcessLoadQuests</c> (login / zone transition). The
/// <see cref="Count"/> reflects the total quests in the snapshot.
/// </summary>
public readonly record struct QuestsLoaded(int Count, LogLineMetadata Metadata);
