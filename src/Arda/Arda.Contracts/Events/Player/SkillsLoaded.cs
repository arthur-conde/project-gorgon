using Arda.Abstractions.Logs;

namespace Arda.World.Player.Events;

/// <summary>
/// Emitted when the full skill table is loaded (login, zone transition).
/// The <see cref="Count"/> reflects the total number of skills in the snapshot.
/// </summary>
public readonly record struct SkillsLoaded(int Count, LogLineMetadata Metadata);
