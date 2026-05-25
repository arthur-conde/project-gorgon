namespace Arda.World.Chat;

/// <summary>
/// Read-only view of the current chat session identity (character + server).
/// </summary>
public interface IChatSessionState
{
    /// <summary>The currently logged-in character name, or null if no banner observed yet.</summary>
    string? Character { get; }

    /// <summary>The server name from the login banner.</summary>
    string? Server { get; }

    /// <summary>The timezone offset parsed from the login banner.</summary>
    TimeSpan? TimezoneOffset { get; }
}
