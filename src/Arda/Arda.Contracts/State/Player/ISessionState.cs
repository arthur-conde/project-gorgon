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
}
