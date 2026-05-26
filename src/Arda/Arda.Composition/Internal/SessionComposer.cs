using Arda.Abstractions.Logs;
using Arda.Contracts;
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
    private readonly Func<string?>? _serverFallback;
    private IDisposable? _sessionSub;
    private IDisposable? _chatSub;

    private string? _character;
    private DateTimeOffset? _loggedInAt;
    private string? _server;
    private TimeSpan _timezoneOffset;
    private bool _hasChatBanner;
    private LogLineMetadata _lastMetadata;

    public ComposedSession? Current { get; private set; }
    public event Action? StateChanged;

    public SessionComposer(IDomainEventBus bus, Func<string?>? serverFallback = null)
    {
        _bus = bus;
        _serverFallback = serverFallback;
        _sessionSub = bus.Subscribe<SessionStarted>(OnSessionStarted);
        _chatSub = bus.Subscribe<ChatSessionIdentified>(OnChatSession);
    }

    private void OnSessionStarted(SessionStarted evt)
    {
        _character = evt.CharacterName;
        _loggedInAt = evt.Metadata.Timestamp ?? evt.Metadata.ReadOn;
        _lastMetadata = evt.Metadata;
        TryBuild();
    }

    private void OnChatSession(ChatSessionIdentified evt)
    {
        _server = evt.Server;
        _timezoneOffset = evt.TimezoneOffset;
        _hasChatBanner = true;
        _lastMetadata = evt.Metadata;
        if (_character is null)
            _character = evt.Character;
        TryBuild();
    }

    private void TryBuild()
    {
        if (_character is null || _loggedInAt is null)
            return;

        var sessionId = $"{_character}:{_loggedInAt:yyyyMMddHHmmss}";
        var resolvedServer = _hasChatBanner ? _server : _serverFallback?.Invoke();
        var session = new ComposedSession(
            _character,
            resolvedServer,
            _loggedInAt.Value,
            _timezoneOffset,
            sessionId);

        Current = session;
        _bus.Publish(new SessionEstablished(session, _lastMetadata));
        StateChanged?.Invoke();
    }

    public void Dispose()
    {
        _sessionSub?.Dispose();
        _chatSub?.Dispose();
        _sessionSub = null;
        _chatSub = null;
    }
}
