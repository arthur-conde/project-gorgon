using Mithril.Shared.Diagnostics;
using Mithril.Shared.Logging;
using Microsoft.Extensions.Hosting;

namespace Mithril.GameState.Sessions;

/// <summary>
/// Hosted-service implementation of <see cref="IGameSessionService"/> and
/// <see cref="ISessionAnchor"/>. Tails <see cref="IPlayerLogStream"/>, parses
/// the login banner via <see cref="LoginBannerParser"/>, and publishes session
/// transitions to subscribers + the anchor consumer.
///
/// Threading: ingestion runs on the hosted-service loop thread; subscriber
/// dispatch happens under <c>_lock</c> both during replay (on the subscriber's
/// thread) and during live publish (on the ingestion thread). Subscribers
/// doing non-trivial work should dispatch off-thread immediately.
///
/// Idempotency: a banner whose parsed <see cref="GameSession.SessionId"/>
/// equals the current one (replay-on-relaunch within the same PG session) is
/// dropped at this layer — neither <see cref="SessionStarted"/> nor
/// <see cref="ISessionAnchor.AnchorChanged"/> re-fires.
/// </summary>
public sealed class GameSessionService : BackgroundService, IGameSessionService, ISessionAnchor
{
    private readonly IPlayerLogStream _stream;
    private readonly IDiagnosticsSink? _diag;

    private readonly object _lock = new();
    private readonly List<Action<GameSession>> _handlers = [];
    private GameSession? _current;

    public GameSessionService(IPlayerLogStream stream, IDiagnosticsSink? diag = null)
    {
        _stream = stream;
        _diag = diag;
    }

    public GameSession? Current
    {
        get { lock (_lock) return _current; }
    }

    public DateTime? LoggedInUtc
    {
        get { lock (_lock) return _current?.LoggedInUtc; }
    }

    public event EventHandler<GameSession>? SessionStarted;
    public event EventHandler? AnchorChanged;

    public IDisposable Subscribe(Action<GameSession> handler)
    {
        ArgumentNullException.ThrowIfNull(handler);
        lock (_lock)
        {
            // Replay the current session before going live so the subscriber
            // observes the same `Current` view that already-attached handlers
            // see. Mirrors InventoryService.Subscribe / QuestService.Subscribe.
            if (_current is not null) Invoke(handler, _current);
            _handlers.Add(handler);
            return new Subscription(this, handler);
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _diag?.Info("Session", "Subscribing to Player.log for login banner");
        await foreach (var raw in _stream.SubscribeAsync(stoppingToken).ConfigureAwait(false))
        {
            if (!LoginBannerParser.TryParse(raw.Line, out var session)) continue;
            Publish(session);
        }
    }

    private void Publish(GameSession session)
    {
        Action<GameSession>[] toFire;
        EventHandler<GameSession>? sessionStartedSnapshot;
        EventHandler? anchorChangedSnapshot;
        lock (_lock)
        {
            if (_current is not null && _current.SessionId == session.SessionId)
            {
                // Replay of an already-known banner. Idempotent: no event,
                // no anchor flip, no subscriber dispatch.
                return;
            }

            _current = session;
            toFire = _handlers.ToArray();
            sessionStartedSnapshot = SessionStarted;
            anchorChangedSnapshot = AnchorChanged;
            _diag?.Info("Session",
                $"Login observed: {session.CharacterName} at {session.LoggedInUtc:O} (offset {session.TimezoneOffset})");
        }

        foreach (var h in toFire) Invoke(h, session);
        sessionStartedSnapshot?.Invoke(this, session);
        anchorChangedSnapshot?.Invoke(this, EventArgs.Empty);
    }

    private void Invoke(Action<GameSession> handler, GameSession session)
    {
        try { handler(session); }
        catch (Exception ex) { _diag?.Warn("Session", $"Subscriber threw: {ex.Message}"); }
    }

    private sealed class Subscription : IDisposable
    {
        private GameSessionService? _owner;
        private readonly Action<GameSession> _handler;

        public Subscription(GameSessionService owner, Action<GameSession> handler)
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
