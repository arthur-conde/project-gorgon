using Arda.Abstractions.Logs;

namespace Arda.World.Player.Events;

/// <summary>
/// Emitted when the full recipe set is loaded (login, zone transition).
/// </summary>
public readonly record struct RecipesLoaded(int Count, LogLineMetadata Metadata);
