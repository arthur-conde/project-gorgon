using Arda.Abstractions.Logs;

namespace Arda.World.Player.Events;

/// <summary>
/// Emitted when a recipe is learned or its lifetime completion count changes.
/// <see cref="Count"/> is the new absolute lifetime count (0 = just learned, never crafted).
/// </summary>
public readonly record struct RecipeUpdated(int RecipeId, int Count, LogLineMetadata Metadata);
