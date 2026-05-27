using Arda.Abstractions.Logs;

namespace Arda.World.Player.Events;

/// <summary>
/// Tier 2 passthrough for non-ErrorMessage <c>ProcessScreenText</c> categories
/// (ImportantInfo, CombatInfo, GeneralInfo). Consumers discriminate by category.
/// Free-text fields use <see cref="ReadOnlyMemory{T}"/> to defer allocation.
/// </summary>
public readonly record struct ScreenTextObserved(
    ReadOnlyMemory<char> Category,
    ReadOnlyMemory<char> Text,
    LogLineMetadata Metadata);
