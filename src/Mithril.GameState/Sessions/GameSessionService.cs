using Mithril.GameState.Servers;
using Mithril.GameState.Sessions.Parsing;
using Mithril.Shared.Diagnostics;
using Mithril.Shared.Logging;
using Microsoft.Extensions.Hosting;

namespace Mithril.GameState.Sessions;

/// <summary>
/// Hosted-service implementation of <see cref="IGameSessionService"/>.
/// Consumes the L0.5 (#532) <see cref="ISystemSignalLogStream"/> via the L1
/// (#550) <see cref="ILogStreamDriver"/> — subscribes for
/// <see cref="SystemSignalLogLine"/>, dispatches on
/// <see cref="SystemSignalKind"/>:
/// <list type="bullet">
///   <item><see cref="SystemSignalKind.LoginBanner"/> — parses the banner
///   via <see cref="LoginBannerParser"/>, joins against the pending
///   connect-event URL (if any) and the
///   <see cref="IServerCatalogService"/> catalog, publishes the session.</item>
///   <item><see cref="SystemSignalKind.ConnectionEvent"/> — parses the
///   preamble <c>EVENT(Ok): connected, url=…, port=…</c> line via
///   <see cref="ConnectionEventParser"/> and stashes the URL as a pending
///   "next session's server hint" for the banner observation that follows.</item>
/// </list>
/// This is the L0.5 migration reference; pre-L0.5 the service consumed
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
/// <para><b>Server identity (#611).</b> The connect line arrives first
/// (preamble, ~17 minutes before the banner in some captures); the banner
/// lands later. The service pairs them within a session adjacency: a
/// connect-event URL captured during replay/live is held as
/// <see cref="_pendingConnectUrl"/> and consumed by the next banner
/// observation. A banner that arrives without a preceding connect (cold-
/// start mid-PG-session, where L0's seed skips the preamble) publishes a
/// session with <c>Server = null</c>; a connect that arrives without a
/// matching catalog entry (PG patch breaking the JSON, or the same
/// cold-start case) also publishes <c>null</c>. Both honest-empty paths
/// surface on diagnostics.</para>
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
    private readonly IServerCatalogService? _serverCatalog;
    private readonly ConnectionEventParser _connectionParser;
    private readonly SessionAnchor? _anchor;
    private readonly IDiagnosticsSink? _diag;

    private readonly object _lock = new();
    private readonly List<Action<GameSession>> _handlers = [];
    private GameSession? _current;
    private ILogSubscription? _subscription;

    // Set by the most-recently-observed EVENT(Ok): connected line. Consumed
    // by the next banner publication, then cleared. Guarded by _lock.
    // Stays null when the L0 seed skipped the preamble (cold-start mid-
    // PG-session) — that's the documented "Server = null" path.
    private string? _pendingConnectUrl;
    private int? _pendingConnectPort;

    public GameSessionService(
        ILogStreamDriver driver,
        IServerCatalogService? serverCatalog = null,
        SessionAnchor? anchor = null,
        IDiagnosticsSink? diag = null)
    {
        _driver = driver;
        _serverCatalog = serverCatalog;
        _connectionParser = new ConnectionEventParser();
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
        _diag?.Info("Session", "Subscribing to L1 driver (SystemSignal pipe) for login banner + connection event");
        // archetype-A defaults — FromSessionStart replay + Inline delivery.
        // Containment is owned by the driver; no per-service try/catch around
        // the handler body (capability C of #550). A degraded subscription
        // surfaces on IAttentionAggregator via the L1 attention source.
        _subscription = _driver.Subscribe<SystemSignalLogLine>(
            envelope =>
            {
                var signal = envelope.Payload;
                switch (signal.Kind)
                {
                    case SystemSignalKind.ConnectionEvent:
                        HandleConnectionEvent(signal);
                        break;
                    case SystemSignalKind.LoginBanner:
                        if (LoginBannerParser.TryParse(signal.Data, out var session))
                        {
                            Publish(session);
                        }
                        break;
                    // Other kinds (AreaLoading, PlayerAdded, SessionLifecycle,
                    // Servers) flow past without us — they're handled by their
                    // dedicated services.
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

    private void HandleConnectionEvent(SystemSignalLogLine signal)
    {
        // The L0.5 classifier strips the "EVENT(Ok): " envelope, so the
        // parser sees the bare "connected, url=…, port=…" body. The signal's
        // timestamp is a best-effort wall-clock stamp from the L0 source
        // clock (no [ts] prefix on preamble lines); we only need the
        // url + port so the stamp is informational.
        var ts = signal.Timestamp.UtcDateTime;
        if (_connectionParser.TryParse(signal.Data, ts) is not ConnectionEvent evt)
        {
            _diag?.Warn("Session",
                "Failed to parse EVENT(Ok): connected payload; server identity will be unknown for the next banner. " +
                "Sample: " +
                (signal.Data.Length > 160 ? signal.Data.Substring(0, 160) + "…" : signal.Data));
            return;
        }

        lock (_lock)
        {
            _pendingConnectUrl = evt.Url;
            _pendingConnectPort = evt.Port;
        }
        _diag?.Info("Session", $"Connect observed: url={evt.Url} port={evt.Port}");
    }

    private void Publish(GameSession session)
    {
        Action<GameSession>[] toFire;
        EventHandler<GameSession>? sessionStartedSnapshot;
        GameSession resolved;

        lock (_lock)
        {
            // Resolve the server identity (if any) and clear the pending
            // connect under the same lock so a concurrent ConnectionEvent
            // observation can't race with the banner publication.
            ServerEntry? server = null;
            var connectUrl = _pendingConnectUrl;
            _pendingConnectUrl = null;
            _pendingConnectPort = null;

            if (connectUrl is not null && _serverCatalog is not null)
            {
                server = _serverCatalog.Get(connectUrl);
                if (server is null)
                {
                    _diag?.Warn("Session",
                        $"Connect URL '{connectUrl}' has no matching ServerCatalog entry; " +
                        "GameSession.Server will be null for this session. " +
                        "(Catalog may be empty if Mithril attached mid-PG-session — L0 seed skips the preamble.)");
                }
            }
            resolved = session with { Server = server };

            if (_current is not null && _current.SessionId == resolved.SessionId)
            {
                // Replay of an already-known banner. Idempotent: no event,
                // no anchor flip, no subscriber dispatch. Note that the
                // resolved.Server may differ from _current.Server in a
                // pathological case (catalog re-populated after the first
                // observation); we preserve the first observation under
                // SessionId-equality semantics — Server is part of the same
                // logical session.
                return;
            }

            _current = resolved;
            toFire = _handlers.ToArray();
            sessionStartedSnapshot = SessionStarted;
            _diag?.Info("Session",
                $"Login observed: {resolved.CharacterName} at {resolved.LoggedInUtc:O} (offset {resolved.TimezoneOffset})" +
                (resolved.Server is not null ? $" on {resolved.Server.Name} ({resolved.Server.Url})" : " (server unknown)"));
        }

        // Push to the shared anchor so the sequencer re-anchors the date for
        // subsequent log lines. SessionAnchor.SetLoggedInUtc is no-op when the
        // value is unchanged, so we don't need to gate it here.
        _anchor?.SetLoggedInUtc(resolved.LoggedInUtc);

        foreach (var h in toFire) Invoke(h, resolved);
        sessionStartedSnapshot?.Invoke(this, resolved);
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
