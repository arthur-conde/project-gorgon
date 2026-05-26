using Arda.Dispatch;
using Arda.World.Player.Events;
using Mithril.Shared.Diagnostics;
using Mithril.Shared.Logging;
using Microsoft.Extensions.Hosting;

namespace Mithril.GameState.Effects;

/// <summary>
/// Hosted-service implementation of <see cref="IPlayerEffectsStateService"/>.
/// Subscribes to Arda domain events (<see cref="EffectsAdded"/>,
/// <see cref="EffectsRemoved"/>, <see cref="EffectNameUpdated"/>) via
/// <see cref="IDomainEventSubscriber"/>. Maintains the live catalog-id-keyed
/// map plus an internal event log used to atomically replay history to late
/// subscribers (post-#585 React-channel contract).
///
/// <para><b>Threading.</b> A single <c>_lock</c> guards <c>_active</c>,
/// <c>_eventLog</c>, <c>_handlers</c>, <c>_unnamed</c>, and
/// <c>_instanceToCatalog</c>. Both the Arda dispatch thread (via domain event
/// callbacks) and <see cref="Subscribe"/> callers take it; live dispatch
/// happens under the lock so the Subscribe-vs-live-event race is impossible
/// (the new subscriber either ran its replay before the next event fires, or
/// attached after — never in between).</para>
///
/// <para><b>Lifecycle.</b> <see cref="StartAsync"/> subscribes eagerly to the
/// domain bus. Subscriptions are synchronous — no background task is needed.
/// Live game-state shared across multiple downstream consumers (Pippin
/// Gourmand, Vampirism sun-damage, Saruman Words-of-Power — see issue #590
/// "Consumers") that must populate at shell startup independent of any
/// module activation gate.</para>
/// </summary>
public sealed class PlayerEffectsStateService : IHostedService, IPlayerEffectsStateService, IDisposable
{
    /// <summary>
    /// Soft cap on the internal event-log size. PG sessions emit ~250 effect
    /// verbs per the wiki captures, so 10,000 is generous (a couple of orders
    /// of magnitude above expected). One-time <see cref="DiagnosticLevel.Warn"/>
    /// if exceeded — the cap protects the log from unbounded growth on a
    /// degenerate stream, not from normal use.
    /// </summary>
    internal const int EventLogSoftCap = 10_000;

    private readonly IDomainEventSubscriber _domainBus;
    private readonly IDiagnosticsSink? _diag;

    private readonly object _lock = new();
    private readonly Dictionary<int, EffectState> _active = new();
    private readonly List<EffectEvent> _eventLog = new();
    private readonly List<Action<EffectEvent>> _handlers = new();

    private readonly Stack<int> _unnamed = new();

    private readonly Dictionary<long, int> _instanceToCatalog = new();

    private IDisposable? _addedSub;
    private IDisposable? _removedSub;
    private IDisposable? _namedSub;
    private bool _eventLogCapWarned;

    public PlayerEffectsStateService(IDomainEventSubscriber domainBus, IDiagnosticsSink? diag = null)
    {
        _domainBus = domainBus;
        _diag = diag;
    }

    public bool TryGet(int catalogId, out EffectState state)
    {
        lock (_lock)
        {
            return _active.TryGetValue(catalogId, out state);
        }
    }

    public IReadOnlyDictionary<int, EffectState> ActiveEffects
    {
        get
        {
            lock (_lock)
            {
                return new Dictionary<int, EffectState>(_active);
            }
        }
    }

    public IDisposable Subscribe(
        Action<EffectEvent> handler,
        ReplayMode replay = ReplayMode.FromSessionStart)
    {
        ArgumentNullException.ThrowIfNull(handler);
        lock (_lock)
        {
            if (replay == ReplayMode.FromSessionStart)
            {
                foreach (var evt in _eventLog)
                {
                    Invoke(handler, evt);
                }
            }
            _handlers.Add(handler);
            return new Subscription(this, handler);
        }
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _diag?.Info("GameState.Effects",
            "Subscribing to Arda domain events (EffectsAdded / EffectsRemoved / EffectNameUpdated)");

        _addedSub = _domainBus.Subscribe<EffectsAdded>(OnEffectsAdded);
        _removedSub = _domainBus.Subscribe<EffectsRemoved>(OnEffectsRemoved);
        _namedSub = _domainBus.Subscribe<EffectNameUpdated>(OnEffectNameUpdated);

        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        _addedSub?.Dispose();
        _removedSub?.Dispose();
        _namedSub?.Dispose();
        _addedSub = null;
        _removedSub = null;
        _namedSub = null;
        return Task.CompletedTask;
    }

    public void Dispose()
    {
        _addedSub?.Dispose();
        _removedSub?.Dispose();
        _namedSub?.Dispose();
        _addedSub = null;
        _removedSub = null;
        _namedSub = null;
    }

    private void OnEffectsAdded(EffectsAdded evt)
    {
        var ts = evt.Metadata.Timestamp ?? evt.Metadata.ReadOn;
        var sourceCharId = evt.SourceCharId;

        lock (_lock)
        {
            foreach (var catalogId in evt.CatalogIds)
            {
                if (_active.TryGetValue(catalogId, out var existing))
                {
                    _active[catalogId] = existing with { AppliedAt = ts };
                    _diag?.Trace("GameState.Effects",
                        $"Add-reemit catalog={catalogId} source={sourceCharId}");
                    continue;
                }

                var state = new EffectState(
                    CatalogId: catalogId,
                    InstanceId: null,
                    DisplayName: null,
                    SourceCharId: sourceCharId,
                    AppliedAt: ts);
                _active[catalogId] = state;
                _unnamed.Push(catalogId);

                var effectEvt = new EffectEvent(EffectEventKind.Added, state, ts);
                AppendEventLog(effectEvt);
                _diag?.Trace("GameState.Effects",
                    $"Add    catalog={catalogId} source={sourceCharId} (total={_active.Count})");
                Fire(effectEvt);
            }
        }
    }

    private void OnEffectsRemoved(EffectsRemoved evt)
    {
        var ts = evt.Metadata.Timestamp ?? evt.Metadata.ReadOn;

        lock (_lock)
        {
            foreach (var instanceId in evt.InstanceIds)
            {
                int? matchedCatalogId = null;
                foreach (var (catalogId, state) in _active)
                {
                    if (state.InstanceId == instanceId)
                    {
                        matchedCatalogId = catalogId;
                        break;
                    }
                }

                if (matchedCatalogId is null)
                {
                    _diag?.Warn("GameState.Effects",
                        $"Remove instance={instanceId} — no entry with that InstanceId in live set, ignored");
                    continue;
                }

                var removed = _active[matchedCatalogId.Value];
                _active.Remove(matchedCatalogId.Value);
                _instanceToCatalog.Remove(instanceId);
                var effectEvt = new EffectEvent(EffectEventKind.Removed, removed, ts);
                AppendEventLog(effectEvt);
                _diag?.Trace("GameState.Effects",
                    $"Remove catalog={matchedCatalogId.Value} instance={instanceId} (total={_active.Count})");
                Fire(effectEvt);
            }
        }
    }

    private void OnEffectNameUpdated(EffectNameUpdated evt)
    {
        var ts = evt.Metadata.Timestamp ?? evt.Metadata.ReadOn;
        var instanceId = evt.InstanceId;
        var displayName = evt.DisplayName;

        lock (_lock)
        {
            if (_instanceToCatalog.TryGetValue(instanceId, out var knownCatalogId))
            {
                if (_active.TryGetValue(knownCatalogId, out var existing))
                {
                    if (existing.DisplayName == displayName)
                    {
                        _diag?.Trace("GameState.Effects",
                            $"Update catalog={knownCatalogId} instance={instanceId} name unchanged, no event");
                        return;
                    }
                    var renamed = existing with { DisplayName = displayName };
                    _active[knownCatalogId] = renamed;
                    var renameEvt = new EffectEvent(EffectEventKind.DisplayNameChanged, renamed, ts);
                    AppendEventLog(renameEvt);
                    _diag?.Trace("GameState.Effects",
                        $"Update catalog={knownCatalogId} instance={instanceId} rename=\"{displayName}\" (was \"{existing.DisplayName}\")");
                    Fire(renameEvt);
                    return;
                }
                _instanceToCatalog.Remove(instanceId);
                _diag?.Trace("GameState.Effects",
                    $"Update instance={instanceId} — stale bridge dropped, falling back to stack");
            }

            while (_unnamed.Count > 0)
            {
                var catalogId = _unnamed.Pop();
                if (!_active.TryGetValue(catalogId, out var state))
                    continue;
                if (state.InstanceId is not null)
                    continue;

                var updated = state with { InstanceId = instanceId, DisplayName = displayName };
                _active[catalogId] = updated;
                _instanceToCatalog[instanceId] = catalogId;
                var effectEvt = new EffectEvent(EffectEventKind.DisplayNameChanged, updated, ts);
                AppendEventLog(effectEvt);
                _diag?.Trace("GameState.Effects",
                    $"Update catalog={catalogId} instance={instanceId} name=\"{displayName}\"");
                Fire(effectEvt);
                return;
            }

            _diag?.Warn("GameState.Effects",
                $"Update instance={instanceId} name=\"{displayName}\" — no un-named candidate, dropped");
        }
    }

    private void AppendEventLog(EffectEvent evt)
    {
        _eventLog.Add(evt);
        if (_eventLog.Count > EventLogSoftCap && !_eventLogCapWarned)
        {
            _eventLogCapWarned = true;
            _diag?.Warn("GameState.Effects",
                $"Event-log size exceeded soft cap ({EventLogSoftCap}). "
                + "Replay-on-Subscribe will continue to deliver every event in order; "
                + "log keeps growing (no drop). Investigate if not expected.");
        }
    }

    private void Fire(EffectEvent evt)
    {
        foreach (var handler in _handlers) Invoke(handler, evt);
    }

    private void Invoke(Action<EffectEvent> handler, EffectEvent evt)
    {
        try { handler(evt); }
        catch (Exception ex) { _diag?.Warn("GameState.Effects", $"Subscriber threw: {ex.Message}"); }
    }

    private sealed class Subscription : IDisposable
    {
        private PlayerEffectsStateService? _owner;
        private readonly Action<EffectEvent> _handler;

        public Subscription(PlayerEffectsStateService owner, Action<EffectEvent> handler)
        {
            _owner = owner;
            _handler = handler;
        }

        public void Dispose()
        {
            var owner = Interlocked.Exchange(ref _owner, null);
            if (owner is null) return;
            lock (owner._lock)
            {
                owner._handlers.Remove(_handler);
            }
        }
    }
}
