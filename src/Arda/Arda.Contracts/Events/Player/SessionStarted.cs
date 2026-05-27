using Arda.Abstractions.Logs;

namespace Arda.World.Player.Events;

/// <summary>
/// Emitted when the local player spawns in-world, extracted from
/// <c>ProcessAddPlayer</c>. Carries the character name so downstream
/// consumers can track which character is active.
/// </summary>
public readonly record struct SessionStarted(
    string CharacterName,
    LogLineMetadata Metadata);
