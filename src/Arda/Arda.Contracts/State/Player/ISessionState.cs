namespace Arda.World.Player;

/// <summary>
/// Read-only view of the current play session — which character is logged in.
/// </summary>
public interface ISessionState
{
    /// <summary>
    /// The active character's name, or <c>null</c> if no character is logged in.
    /// </summary>
    string? ActiveCharacter { get; }

    /// <summary>
    /// Timestamp of the <c>ProcessAddPlayer</c> line that established the session,
    /// or <c>null</c> if no session has started yet.
    /// </summary>
    DateTimeOffset? LoggedInAt { get; }
}
