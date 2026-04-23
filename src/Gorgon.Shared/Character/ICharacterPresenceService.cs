namespace Gorgon.Shared.Character;

/// <summary>
/// Reads and writes per-character <see cref="CharacterPresence"/>. The service itself
/// stamps <see cref="CharacterPresence.LastActiveAt"/> on switch-away and graceful shutdown;
/// consumers typically only read via <see cref="GetLastActiveAt"/>.
/// </summary>
public interface ICharacterPresenceService
{
    /// <summary>Last-active timestamp for a specific character, or null if never stamped.</summary>
    DateTimeOffset? GetLastActiveAt(string character, string server);
}
