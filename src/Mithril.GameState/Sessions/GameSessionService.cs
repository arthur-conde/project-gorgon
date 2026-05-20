using Mithril.Shared.Diagnostics;
using Mithril.Shared.Logging;
using Microsoft.Extensions.Hosting;

namespace Mithril.GameState.Sessions;

/// <summary>
/// Hosted-service implementation of <see cref="IGameSessionService"/>.
/// Consumes the L0.5 (#532) <see cref="ISystemSignalLogStream"/> — filters
/// for <see cref="SystemSignalKind.LoginBanner"/>, parses the banner body
/// via <see cref="LoginBannerParser"/>, and publishes session transitions to
/// subscribers + the shared <see cref="SessionAnchor"/> consumer. This is the
/// L0.5 migration reference; pre-L0.5 the service consumed
/// <see cref="IPlayerLogStream"/> and matched the banner shape itself.
///
/// <para>The anchor is pushed-to, not implemented-by, to avoid a DI cycle:
/// the streams that consume the anchor are in <c>Mithril.Shared</c>, and
/// this service consumes the streams — wiring the anchor through the service
/// would form stream → anchor → service → stream. <see cref="SessionAnchor"/>
/// is a Mithril.Shared leaf that both depend on; only this service writes to
/// it.</para>
///
/// <para>Threading: ingestion runs on the hosted-service loop thread; subscriber
/// dispatch happens under <c>_lock</c> both during replay (on the subscriber's
/// thread) and during live publish (on the ingestion thread). Subscribers
/// doing non-trivial work should dispatch off-thread immediately.</para>
///
/// <para>Idempotency: a banner whose parsed <see cref="GameSession.SessionId"/>
/// equals the current one (replay-on-relaunch within the same PG session) is
/// dropped at this layer — neither <see cref="SessionStarted"/> nor the
/// anchor's <c>AnchorChanged</c> re-fires.</para>
/// </summary>
public sealed class GameSessionService : BackgroundService, IGameSessionService
{
    private readonly ISystemSignalLogStream _stream;
    private readonly SessionAnchor? _anchor;
    private readonly IDiagnosticsSink? _diag;
    private readonly ThrottledWarn _warn;

    private readonly object _lock = new();
    private readonly List<Action<GameSession>> _handlers = [];
    private GameSession? _current;

    public GameSessionService(
        ISystemSignalLogStream stream,
        SessionAnchor? anchor = null,
        IDiagnosticsSink? diag = null)
    {
        _stream = stream;
        _anchor = anchor;
        _diag = diag;
        _warn = new ThrottledWarn(diag, "Session");
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
        _diag?.Info("Session", "Subscribing to L0.5 system-signal pipe for login banner");
        await foreach (var signal in _stream.SubscribeAsync(stoppingToken).ConfigureAwait(false))
        {
            // L0.5 already classified the line as a system signal — we only
            // care about LoginBanner here. Other kinds (AreaLoading,
            // PlayerAdded, SessionLifecycle) flow past without us.
            if (signal.Kind != SystemSignalKind.LoginBanner) continue;
            try
            {
                if (!LoginBannerParser.TryParse(signal.Data, out var session)) continue;
                Publish(session);
            }
            catch (Exception ex) { _warn.Warn($"Ingestion error: {ex.Message}"); }
        }
    }

    private void Publish(GameSession session)
    {
        Action<GameSession>[] toFire;
        EventHandler<GameSession>? sessionStartedSnapshot;
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
            _diag?.Info("Session",
                $"Login observed: {session.CharacterName} at {session.LoggedInUtc:O} (offset {session.TimezoneOffset})");
        }

        // Push to the shared anchor so the sequencer re-anchors the date for
        // subsequent log lines. SessionAnchor.SetLoggedInUtc is no-op when the
        // value is unchanged, so we don't need to gate it here.
        _anchor?.SetLoggedInUtc(session.LoggedInUtc);

        foreach (var h in toFire) Invoke(h, session);
        sessionStartedSnapshot?.Invoke(this, session);
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
