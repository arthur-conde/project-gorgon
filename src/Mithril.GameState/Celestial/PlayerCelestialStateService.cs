using Arda.Abstractions.Logs;
using Arda.Dispatch;
using Arda.World.Player.Events;
using Mithril.Shared.Diagnostics;

namespace Mithril.GameState.Celestial;

/// <summary>
/// Implementation of <see cref="IPlayerCelestialState"/>. Subscribes to the
/// Arda domain event bus for <see cref="CelestialInfoChanged"/> events (emitted
/// by Arda's L3 celestial handler when <c>ProcessSetCelestialInfo</c> is parsed)
/// and holds the latest <see cref="CelestialInfo"/> (phase + the line's UTC
/// instant). Last-writer-wins keeps it advancing across phase roll-overs and
/// relogs.
///
/// <para><b>Threading.</b> The Arda domain bus delivers events synchronously on
/// the merger pump thread; <see cref="Current"/> reads and subscriber dispatch
/// happen under <c>_lock</c>. Subscribers doing non-trivial work should marshal
/// off-thread immediately (the Palantir VM dispatches to the UI thread).</para>
/// </summary>
public sealed class PlayerCelestialStateService : IPlayerCelestialState, IDisposable
{
    private readonly IDiagnosticsSink? _diag;
    private readonly IDisposable _domainSub;

    private readonly object _lock = new();
    private readonly List<Action<CelestialInfo>> _handlers = [];
    private readonly HashSet<string> _reportedUnknownTokens = new(StringComparer.Ordinal);
    private CelestialInfo? _current;

    public PlayerCelestialStateService(
        IDomainEventSubscriber bus,
        IDiagnosticsSink? diag = null)
    {
        ArgumentNullException.ThrowIfNull(bus);
        _diag = diag;
        _domainSub = bus.Subscribe<CelestialInfoChanged>(OnCelestialInfoChanged);
    }

    public CelestialInfo? Current
    {
        get { lock (_lock) return _current; }
    }

    public IDisposable Subscribe(Action<CelestialInfo> handler)
    {
        ArgumentNullException.ThrowIfNull(handler);
        lock (_lock)
        {
            if (_current is not null) Invoke(handler, _current);
            _handlers.Add(handler);
            return new HandlerSubscription(this, handler);
        }
    }

    public void Dispose()
    {
        _domainSub.Dispose();
    }

    private void OnCelestialInfoChanged(CelestialInfoChanged evt)
    {
        var phase = MoonPhaseExtensions.ParsePhase(evt.RawPhase);
        if (phase == MoonPhase.Unknown)
            ReportUnknownToken(evt.RawPhase);

        var ts = evt.Metadata.Timestamp ?? evt.Metadata.ReadOn;
        var info = new CelestialInfo(phase, evt.RawPhase, ts);

        Publish(info);
        _diag?.Trace("GameState.Celestial",
            $"Moon phase: {evt.RawPhase} ({phase}) @ {ts:O}");
    }

    private void ReportUnknownToken(string rawToken)
    {
        bool firstSighting;
        lock (_lock) { firstSighting = _reportedUnknownTokens.Add(rawToken); }
        if (!firstSighting) return;

        _diag?.Error("GameState.Celestial",
            $"Unmapped celestial token '{rawToken}' — no MoonPhase enum member. " +
            "Add it to MoonPhase + MoonPhaseExtensions.ByToken / DisplayName " +
            "(value preserved as raw passthrough until then).");
    }

    private void Publish(CelestialInfo info)
    {
        Action<CelestialInfo>[] toFire;
        lock (_lock)
        {
            _current = info;
            toFire = _handlers.ToArray();
        }
        foreach (var h in toFire) Invoke(h, info);
    }

    private void Invoke(Action<CelestialInfo> handler, CelestialInfo info)
    {
        try { handler(info); }
        catch (Exception ex) { _diag?.Warn("GameState.Celestial", $"Subscriber threw: {ex.Message}"); }
    }

    private sealed class HandlerSubscription : IDisposable
    {
        private PlayerCelestialStateService? _owner;
        private readonly Action<CelestialInfo> _handler;

        public HandlerSubscription(PlayerCelestialStateService owner, Action<CelestialInfo> handler)
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
