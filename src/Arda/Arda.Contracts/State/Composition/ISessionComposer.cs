namespace Arda.Composition;

/// <summary>
/// Composed session identity from cross-source fusion:
/// <see cref="Arda.World.Player.Events.SessionStarted"/> (player log) +
/// <see cref="Arda.World.Chat.Events.ChatSessionIdentified"/> (chat log).
/// </summary>
public readonly record struct ComposedSession(
    string CharacterName,
    string? Server,
    DateTimeOffset LoggedInAt,
    TimeSpan TimezoneOffset,
    string SessionId);

/// <summary>
/// L4 cross-source composer that fuses player session and chat login banner
/// data into a single <see cref="ComposedSession"/>.
/// </summary>
public interface ISessionComposer
{
    /// <summary>Current session, or <c>null</c> before first login is observed.</summary>
    ComposedSession? Current { get; }

    /// <summary>Fires after any mutation (session established or updated).</summary>
    event Action? StateChanged;
}
