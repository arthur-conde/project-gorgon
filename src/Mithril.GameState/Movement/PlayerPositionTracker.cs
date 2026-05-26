using Arda.Dispatch;
using Arda.World.Player.Events;
using Microsoft.Extensions.Hosting;
using Mithril.Shared.Diagnostics;

namespace Mithril.GameState.Movement;

/// <summary>
/// Hosted-service implementation of <see cref="IPlayerPositionTracker"/>.
/// Subscribes to <see cref="PlayerPositionChanged"/> events via the Arda domain
/// event bus and holds the latest <see cref="PlayerPosition"/> (coords + the
/// line's UTC instant + source).
///
/// <para><b>Why a self-feeding BackgroundService.</b> Mirrors
/// <see cref="Sessions.GameSessionService"/> / <c>PlayerQuestJournalService</c>: the
/// position is shared live game-state and must be available to consumers
/// (e.g. Palantir's debug surface) without depending on any other module
/// being activated.</para>
///
/// <para><b>Threading.</b> The domain event bus invokes the handler on its
/// publisher's thread; <see cref="Current"/> reads and subscriber dispatch
/// happen under <c>_lock</c>. Subscribers doing non-trivial work should
/// marshal off-thread immediately (the Palantir VM dispatches to the UI
/// thread).</para>
/// </summary>
public sealed class PlayerPositionTracker : BackgroundService, IPlayerPositionTracker
{
    private readonly IDomainEventSubscriber _bus;
    private readonly IDiagnosticsSink? _diag;

    private readonly object _lock = new();
    private readonly List<Action<PlayerPosition>> _handlers = [];
    private PlayerPosition? _current;
    private IDisposable? _subscription;

    private const string DiagCategory = "GameState.Position";

    public PlayerPositionTracker(
        IDomainEventSubscriber bus,
        IDiagnosticsSink? diag = null)
    {
        _bus = bus;
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
            if (_current is not null) Invoke(handler, _current);
            _handlers.Add(handler);
            return new HandlerSubscription(this, handler);
        }
    }

    public override Task StartAsync(CancellationToken cancellationToken)
    {
        _diag?.Info(DiagCategory,
            "Subscribing to Arda domain bus for PlayerPositionChanged");

        _subscription = _bus.Subscribe<PlayerPositionChanged>(OnPositionChanged);

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

    private void OnPositionChanged(PlayerPositionChanged evt)
    {
        var measuredAt = evt.Metadata.Timestamp ?? evt.Metadata.ReadOn;
        var source = evt.Source switch
        {
            PositionSource.Movement => PlayerPositionSource.Movement,
            PositionSource.Spawn => PlayerPositionSource.Spawn,
            _ => PlayerPositionSource.Movement,
        };

        Publish(new PlayerPosition(evt.X, evt.Y, evt.Z, measuredAt, source));
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
        catch (Exception ex) { _diag?.Warn(DiagCategory, $"Subscriber threw: {ex.Message}"); }
    }

    private sealed class HandlerSubscription : IDisposable
    {
        private PlayerPositionTracker? _owner;
        private readonly Action<PlayerPosition> _handler;

        public HandlerSubscription(PlayerPositionTracker owner, Action<PlayerPosition> handler)
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
