using Arda.Dispatch;
using Arda.World.Chat.Events;
using Arda.World.Player.Events;

namespace Arda.Composition.Internal;

/// <summary>
/// Fuses <see cref="SessionStarted"/> (player log, carries character name) and
/// <see cref="ChatSessionIdentified"/> (chat log, carries server + timezone) into
/// a single <see cref="ComposedSession"/>. Builds the session incrementally —
/// either source can arrive first.
/// </summary>
internal sealed class SessionComposer : ISessionComposer, IDisposable
{
    private readonly IDomainEventPublisher _bus;
    private IDisposable? _sessionSub;
    private IDisposable? _chatSub;

    private string? _character;
    private DateTimeOffset? _loggedInAt;
    private string? _server;
    private TimeSpan _timezoneOffset;
    private bool _hasChatBanner;

    public ComposedSession? Current { get; private set; }

    public SessionComposer(IDomainEventBus bus)
    {
        _bus = bus;
        _sessionSub = bus.Subscribe<SessionStarted>(OnSessionStarted);
        _chatSub = bus.Subscribe<ChatSessionIdentified>(OnChatSession);
    }

    private void OnSessionStarted(SessionStarted evt)
    {
        _character = evt.CharacterName;
        _loggedInAt = evt.Metadata.Timestamp ?? evt.Metadata.ReadOn;
        TryBuild();
    }

    private void OnChatSession(ChatSessionIdentified evt)
    {
        _server = evt.Server;
        _timezoneOffset = evt.TimezoneOffset;
        _hasChatBanner = true;
        if (_character is null)
            _character = evt.Character;
        TryBuild();
    }

    private void TryBuild()
    {
        if (_character is null || _loggedInAt is null)
            return;

        var sessionId = $"{_character}:{_loggedInAt:yyyyMMddHHmmss}";
        var session = new ComposedSession(
            _character,
            _hasChatBanner ? _server : null,
            _loggedInAt.Value,
            _timezoneOffset,
            sessionId);

        Current = session;
        _bus.Publish(new SessionEstablished(session));
    }

    public void Dispose()
    {
        _sessionSub?.Dispose();
        _chatSub?.Dispose();
        _sessionSub = null;
        _chatSub = null;
    }
}
