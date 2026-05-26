using Arda.Abstractions.Logs;
using Arda.Dispatch;
using Arda.World.Chat.Events;
using Mithril.GameState.Servers;
using Mithril.Shared.Diagnostics;
using Mithril.Shared.Logging;
using Microsoft.Extensions.Hosting;

namespace Mithril.GameState.Sessions;

/// <summary>
/// Hosted-service implementation of <see cref="IGameSessionService"/>.
/// Subscribes to <see cref="ChatSessionIdentified"/> domain events via the
/// Arda <see cref="IDomainEventSubscriber"/> bus. The chat login banner carries
/// character name, server name, and timezone offset; the line's
/// <see cref="LogLineMetadata.Timestamp"/> provides the authoritative login
/// instant (UTC).
///
/// <para><b>Server identity.</b> The chat banner's <c>Server</c> field is a
/// human-facing name (e.g. "Laeth"). The service resolves it to a
/// <see cref="ServerEntry"/> via <see cref="IServerCatalogService.All"/> (name
/// match). When the catalog is empty (cold-start mid-PG-session where L0's
/// seed skips the preamble and the <c>Servers:</c> line never reaches the
/// ingest), or when no entry matches the name, the session publishes with
/// <c>Server = null</c>. Consumers must handle this explicitly.</para>
///
/// <para>The anchor is pushed-to, not implemented-by, to avoid a DI cycle:
/// the streams that consume the anchor are in <c>Mithril.Shared</c>, and
/// this service consumes the streams — wiring the anchor through the service
/// would form stream → anchor → service → stream. <see cref="SessionAnchor"/>
/// is a Mithril.Shared leaf that both depend on; only this service writes to
/// it.</para>
///
/// <para><b>Threading.</b> The domain event bus invokes the handler on its
/// publisher's thread (the Arda world's dispatch thread); subscriber dispatch
/// happens under <c>_lock</c> both during replay (on the subscriber's thread)
/// and during live publish (on the dispatch thread). Subscribers doing
/// non-trivial work should dispatch off-thread immediately.</para>
///
/// <para><b>Idempotency.</b> A banner whose derived
/// <see cref="GameSession.SessionId"/> equals the current one (replay-on-
/// relaunch within the same PG session) is dropped — neither
/// <see cref="SessionStarted"/> nor the anchor's <c>AnchorChanged</c>
/// re-fires.</para>
/// </summary>
public sealed class GameSessionService : BackgroundService, IGameSessionService
{
    private readonly IDomainEventSubscriber _bus;
    private readonly IServerCatalogService? _serverCatalog;
    private readonly SessionAnchor? _anchor;
    private readonly IDiagnosticsSink? _diag;

    private readonly object _lock = new();
    private readonly List<Action<GameSession>> _handlers = [];
    private GameSession? _current;
    private IDisposable? _subscription;

    public GameSessionService(
        IDomainEventSubscriber bus,
        IServerCatalogService? serverCatalog = null,
        SessionAnchor? anchor = null,
        IDiagnosticsSink? diag = null)
    {
        _bus = bus;
        _serverCatalog = serverCatalog;
        _anchor = anchor;
        _diag = diag;
    }

    public GameSession? Current
    {
        get { lock (_lock) return _current; }
    }

    public event EventHandler<GameSession>? SessionStarted;

    public IDisposable Subscribe(Action<GameSession> handler)
    {
        ArgumentNullException.ThrowIfNull(handler);
        lock (_lock)
        {
            if (_current is not null) Invoke(handler, _current);
            _handlers.Add(handler);
            return new HandlerSubscription(this, handler);
        }
    }

    public override Task StartAsync(CancellationToken cancellationToken)
    {
        _diag?.Info("Session",
            "Subscribing to Arda domain bus for ChatSessionIdentified");

        _subscription = _bus.Subscribe<ChatSessionIdentified>(OnChatSessionIdentified);

        return base.StartAsync(cancellationToken);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try { await Task.Delay(Timeout.Infinite, stoppingToken).ConfigureAwait(false); }
        catch (OperationCanceledException) { /* expected on host stop */ }
        finally
        {
            _subscription?.Dispose();
            _subscription = null;
        }
    }

    public override void Dispose()
    {
        _subscription?.Dispose();
        _subscription = null;
        base.Dispose();
    }

    private void OnChatSessionIdentified(ChatSessionIdentified evt)
    {
        var utc = (evt.Metadata.Timestamp ?? evt.Metadata.ReadOn).UtcDateTime;
        utc = DateTime.SpecifyKind(utc, DateTimeKind.Utc);

        var sessionId = $"{evt.Character}|{utc:O}";
        var session = new GameSession(sessionId, evt.Character, utc, evt.TimezoneOffset);

        Publish(session, evt.Server);
    }

    private void Publish(GameSession session, string? serverName)
    {
        Action<GameSession>[] toFire;
        EventHandler<GameSession>? sessionStartedSnapshot;
        GameSession resolved;

        lock (_lock)
        {
            ServerEntry? server = null;
            if (!string.IsNullOrEmpty(serverName) && _serverCatalog is not null)
            {
                server = _serverCatalog.All
                    .FirstOrDefault(s => string.Equals(s.Name, serverName, StringComparison.OrdinalIgnoreCase));
                if (server is null)
                {
                    _diag?.Warn("Session",
                        $"Server name '{serverName}' has no matching ServerCatalog entry; " +
                        "GameSession.Server will be null for this session. " +
                        "(Catalog may be empty if Mithril attached mid-PG-session.)");
                }
            }
            resolved = session with { Server = server };

            if (_current is not null && _current.SessionId == resolved.SessionId)
            {
                return;
            }

            _current = resolved;
            toFire = _handlers.ToArray();
            sessionStartedSnapshot = SessionStarted;
            _diag?.Info("Session",
                $"Login observed: {resolved.CharacterName} at {resolved.LoggedInUtc:O} (offset {resolved.TimezoneOffset})" +
                (resolved.Server is not null ? $" on {resolved.Server.Name} ({resolved.Server.Url})" : " (server unknown)"));
        }

        _anchor?.SetLoggedInUtc(resolved.LoggedInUtc);

        foreach (var h in toFire) Invoke(h, resolved);
        sessionStartedSnapshot?.Invoke(this, resolved);
    }

    private void Invoke(Action<GameSession> handler, GameSession session)
    {
        try { handler(session); }
        catch (Exception ex) { _diag?.Warn("Session", $"Subscriber threw: {ex.Message}"); }
    }

    private sealed class HandlerSubscription : IDisposable
    {
        private GameSessionService? _owner;
        private readonly Action<GameSession> _handler;

        public HandlerSubscription(GameSessionService owner, Action<GameSession> handler)
        {
            _owner = owner;
            _handler = handler;
        }

        public void Dispose()
        {
            var owner = Interlocked.Exchange(ref _owner, null);
            if (owner is null) return;
            lock (owner._lock) { owner._handlers.Remove(_handler); }
        }
    }
}
