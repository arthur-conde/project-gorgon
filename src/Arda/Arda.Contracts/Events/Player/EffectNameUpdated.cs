using Arda.Abstractions.Logs;

namespace Arda.World.Player.Events;

/// <summary>
/// Emitted when the game engine names (or renames) an effect instance via
/// <c>ProcessUpdateEffectName</c>. Carries the per-application instance ID
/// and the display name string.
/// </summary>
public readonly record struct EffectNameUpdated(
    long InstanceId,
    string DisplayName,
    LogLineMetadata Metadata);
