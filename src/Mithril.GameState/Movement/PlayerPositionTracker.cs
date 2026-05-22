using Mithril.Shared.Diagnostics;
using Mithril.Shared.Logging;
using Microsoft.Extensions.Hosting;

namespace Mithril.GameState.Movement;

/// <summary>
/// Hosted-service implementation of <see cref="IPlayerPositionTracker"/>.
/// Subscribes to L1's unified classified pipe (#556 Phase 3), parses
/// <c>ProcessNewPosition</c> on the <see cref="LocalPlayerLogLine"/>
/// payload and the local player's <c>ProcessAddPlayer</c> on the
/// <see cref="SystemSignalLogLine"/> { <see cref="SystemSignalKind.PlayerAdded"/> }
/// payload via <see cref="PlayerPositionParser"/>, and holds the latest
/// <see cref="PlayerPosition"/> (coords + the line's UTC instant + source).
/// The <c>ProcessAddPlayer</c> spawn line is the live-replay seed point, so
/// position is populated at session start rather than null until the first
/// teleport — last-writer-wins keeps it advancing as the player relogs/zones.
///
/// <para><b>Why a self-feeding BackgroundService.</b> Mirrors
/// <see cref="Sessions.GameSessionService"/> / <c>PlayerQuestJournalService</c>: the
/// position is shared live game-state and must be available to consumers
/// (e.g. Palantir's debug surface) without depending on any other module
/// being activated.</para>
///
/// <para><b>Why the unified pipe and not the LocalPlayer typed pipe.</b>
/// L0.5 routes <c>LocalPlayer: ProcessAddPlayer(…)</c> to the
/// <see cref="ISystemSignalLogStream"/> { <see cref="SystemSignalKind.PlayerAdded"/> }
/// pipe — distinct from the LocalPlayer typed pipe — even though the
/// originating actor envelope says <c>LocalPlayer:</c>. Subscribing through
/// the L0.5 unified pipe is the simplest way to see both verb classes in
/// source-Sequence order through a single subscription, eliminating the
/// cross-pipe two-pump race documented in #556.</para>
///
/// <para><b>No area-tracker dependency.</b> The
/// <see cref="Areas.PlayerAreaTracker"/> self-feeds via L1 since Phase 2
/// (#556 / PR #568); this tracker no longer warms it. Combat envelopes
/// silently no-op on the unified pipe (no <c>switch</c> case matches them
/// — by design; a future <c>default</c> warning would flood the diag log
/// under heavy combat).</para>
///
/// <para><b>Threading.</b> The L1 driver's <c>InlineBridge</c> invokes the
/// handler on its pump thread; <see cref="Current"/> reads and subscriber
/// dispatch happen under <c>_lock</c>. Subscribers doing non-trivial work
/// should marshal off-thread immediately (the Palantir VM dispatches to
/// the UI thread).</para>
/// </summary>
public sealed class PlayerPositionTracker : BackgroundService, IPlayerPositionTracker
{
    private readonly ILogStreamDriver _driver;
    private readonly PlayerPositionParser _parser;
    private readonly IDiagnosticsSink? _diag;

    private readonly object _lock = new();
    private readonly List<Action<PlayerPosition>> _handlers = [];
    private PlayerPosition? _current;
    private ILogSubscription? _subscription;

    private const string DiagCategory = "GameState.Position";

    public PlayerPositionTracker(
        ILogStreamDriver driver,
        PlayerPositionParser parser,
        IDiagnosticsSink? diag = null)
    {
        _driver = driver;
        _parser = parser;
        _diag = diag;
    }

    public PlayerPosition? Current
    {
        get { lock (_lock) return _current; }
    }

    public IDisposable Subscribe(Action<PlayerPosition> handler)
    {
        ArgumentNullException.ThrowIfNull(handler);
        lock (_lock)
        {
            // Replay the current position before going live so a late
            // subscriber observes the same view already-attached handlers
            // see. Mirrors GameSessionService.Subscribe.
            if (_current is not null) Invoke(handler, _current);
            _handlers.Add(handler);
            return new Subscription(this, handler);
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _diag?.Info(DiagCategory,
            "Subscribing to L1 driver (unified classified pipe) for ProcessNewPosition + ProcessAddPlayer");

        _subscription = _driver.Subscribe<IClassifiedPlayerLogLine>(
            envelope =>
            {
                // Risk 4 (#556 brainstorm): L0.5 routes ProcessAddPlayer to
                // SystemSignal { Kind: PlayerAdded } — NOT to the LocalPlayer
                // pipe — even though the actor envelope says LocalPlayer.
                // ProcessNewPosition lives on the LocalPlayer pipe. So the
                // tracker must accept both payload classes off the unified
                // pipe to recover the full pre-#556 behaviour.
                switch (envelope.Payload)
                {
                    case LocalPlayerLogLine l:
                        // Bare verb (envelope eaten) — the parser's
                        // ProcessNewPosition regex doesn't anchor on the
                        // actor token, so this works on Data directly.
                        if (_parser.TryParse(l.Data, l.Timestamp.UtcDateTime)
                            is PlayerPositionEvent posEvt)
                        {
                            Publish(new PlayerPosition(
                                posEvt.X, posEvt.Y, posEvt.Z,
                                ToOffset(posEvt.Timestamp), posEvt.Source));
                        }
                        break;

                    case SystemSignalLogLine { Kind: SystemSignalKind.PlayerAdded } s:
                        // Spawn seed. The parser's TryParseSpawnFromData
                        // skips the actor-token gate (L0.5 already enforced
                        // it before routing this envelope).
                        if (_parser.TryParseSpawnFromData(s.Data, s.Timestamp.UtcDateTime)
                            is PlayerPositionEvent spawnEvt)
                        {
                            Publish(new PlayerPosition(
                                spawnEvt.X, spawnEvt.Y, spawnEvt.Z,
                                ToOffset(spawnEvt.Timestamp), spawnEvt.Source));
                        }
                        break;

                    // CombatActorLogLine / other SystemSignal kinds silently
                    // no-op. The unified pipe carries everything in source
                    // order; we only care about our two verbs. A default
                    // warning here would flood the diag log under heavy
                    // combat or session-banner traffic.
                }
                return ValueTask.CompletedTask;
            },
            new LogSubscriptionOptions
            {
                ReplayMode = ReplayMode.FromSessionStart,
                DeliveryContext = DeliveryContext.Inline,
                DiagnosticCategory = DiagCategory,
            });

        // Park until host stop; the L1 subscription runs its own pump.
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

    private void Publish(PlayerPosition position)
    {
        Action<PlayerPosition>[] toFire;
        lock (_lock)
        {
            _current = position;
            toFire = _handlers.ToArray();
        }
        foreach (var h in toFire) Invoke(h, position);
    }

    private void Invoke(Action<PlayerPosition> handler, PlayerPosition position)
    {
        try { handler(position); }
        catch (Exception ex) { _diag?.Warn("GameState.Position", $"Subscriber threw: {ex.Message}"); }
    }

    /// <summary>
    /// Player.log timestamps are normalized upstream by the L0 source
    /// clock (<c>PlayerLogClock</c>, #513) — <c>raw.Timestamp</c> arrives
    /// as a UTC <see cref="DateTimeOffset"/>; we pass its
    /// <c>.UtcDateTime</c> into the parser, which surfaces its event's
    /// <see cref="DateTime"/> as Unspecified-kind. Stamp the kind
    /// defensively here so the lifted <see cref="DateTimeOffset"/>
    /// always has offset +00:00 rather than the host's local offset.
    /// </summary>
    private static DateTimeOffset ToOffset(DateTime ts) =>
        new(DateTime.SpecifyKind(ts, DateTimeKind.Utc));

    private sealed class Subscription : IDisposable
    {
        private PlayerPositionTracker? _owner;
        private readonly Action<PlayerPosition> _handler;

        public Subscription(PlayerPositionTracker owner, Action<PlayerPosition> handler)
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
