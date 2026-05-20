using Mithril.Shared.Diagnostics;
using Mithril.Shared.Logging;
using Microsoft.Extensions.Hosting;

namespace Mithril.GameState.Sessions;

/// <summary>
/// Hosted-service implementation of <see cref="IGameSessionService"/>.
/// Consumes the L0.5 (#532) <see cref="ISystemSignalLogStream"/> via the L1
/// (#550) <see cref="ILogStreamDriver"/> — subscribes for
/// <see cref="SystemSignalLogLine"/>, filters for
/// <see cref="SystemSignalKind.LoginBanner"/>, parses the banner body via
/// <see cref="LoginBannerParser"/>, and publishes session transitions to
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
/// <para>Threading: the L1 driver delivers envelopes inline (the
/// archetype-A default), so handler invocation runs on the driver's pump
/// thread; subscriber dispatch happens under <c>_lock</c> both during replay
/// (on the subscriber's thread) and during live publish (on the pump
/// thread). Subscribers doing non-trivial work should dispatch off-thread
/// immediately.</para>
///
/// <para>Idempotency: a banner whose parsed <see cref="GameSession.SessionId"/>
/// equals the current one (replay-on-relaunch within the same PG session) is
/// dropped at this layer — neither <see cref="SessionStarted"/> nor the
/// anchor's <c>AnchorChanged</c> re-fires.</para>
///
/// <para>Containment: the L1 driver wraps each handler invocation in
/// try/catch + rate-limited Warn, retiring the per-service
/// <see cref="ThrottledWarn"/> instance this service used to hold (#550
/// capability C). Failures surface on
/// <c>IDiagnosticsSink</c> under the <c>Session</c> category via the
/// driver's <see cref="LogSubscriptionOptions.DiagnosticCategory"/>
/// override.</para>
/// </summary>
public sealed class GameSessionService : BackgroundService, IGameSessionService
{
    private readonly ILogStreamDriver _driver;
    private readonly SessionAnchor? _anchor;
    private readonly IDiagnosticsSink? _diag;

    private readonly object _lock = new();
    private readonly List<Action<GameSession>> _handlers = [];
    private GameSession? _current;
    private ILogSubscription? _subscription;

    public GameSessionService(
        ILogStreamDriver driver,
        SessionAnchor? anchor = null,
        IDiagnosticsSink? diag = null)
    {
        _driver = driver;
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
        _diag?.Info("Session", "Subscribing to L1 driver (SystemSignal pipe) for login banner");
        // archetype-A defaults — FromSessionStart replay + Inline delivery.
        // Containment is owned by the driver; no per-service try/catch around
        // the handler body (capability C of #550). A degraded subscription
        // surfaces on IAttentionAggregator via the L1 attention source.
        _subscription = _driver.Subscribe<SystemSignalLogLine>(
            envelope =>
            {
                var signal = envelope.Payload;
                // L0.5 already classified the line as a system signal — we only
                // care about LoginBanner here. Other kinds (AreaLoading,
                // PlayerAdded, SessionLifecycle) flow past without us.
                if (signal.Kind != SystemSignalKind.LoginBanner) return ValueTask.CompletedTask;
                if (LoginBannerParser.TryParse(signal.Data, out var session))
                {
                    Publish(session);
                }
                return ValueTask.CompletedTask;
            },
            new LogSubscriptionOptions
            {
                ReplayMode = ReplayMode.FromSessionStart,
                DeliveryContext = DeliveryContext.Inline,
                DiagnosticCategory = "Session",
            });

        // Park until the host stops. The L1 subscription runs its own pump
        // on a Task.Run; ExecuteAsync's job is to dispose it on shutdown.
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
