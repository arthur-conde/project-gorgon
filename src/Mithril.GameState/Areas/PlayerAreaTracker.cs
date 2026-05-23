using Mithril.GameState.Areas.Parsing;
using Mithril.Shared.Diagnostics;
using Mithril.WorldSim;

namespace Mithril.GameState.Areas;

/// <summary>
/// Holds the player's current area code, parsed from
/// <c>LOADING LEVEL Area&lt;Name&gt;</c> log lines via
/// <see cref="AreaTransitionParser"/>. Shared live game-state: consumed by
/// Gandalf (chest commits stamp <c>LearnedChest.Area</c>) and Legolas
/// (per-area survey-projection calibration), among others.
///
/// <para><b>World-simulator migration (#775).</b> Pre-migration this class was
/// a <see cref="Microsoft.Extensions.Hosting.BackgroundService"/> that owned
/// its own L1 SystemSignal subscription and self-seeded via a reverse-scan of
/// <c>Player.log</c>. Post-migration it is an <see cref="IFolder{TPayload}"/>
/// registered with <c>IPlayerWorld</c> for the <see cref="AreaLoadingFrame"/>
/// payload type: a sibling <see cref="Producers.AreaLoadingFrameProducer"/>
/// owns the L1 subscription, parses every
/// <see cref="Mithril.Shared.Logging.SystemSignalKind.AreaLoading"/> envelope
/// via <see cref="AreaTransitionParser"/>, and emits one
/// <see cref="AreaLoadingFrame"/> per transition. The world drives
/// <see cref="Apply"/> per applied frame in source order; the folder returns
/// <see cref="PlayerAreaChanged"/> change events which the world publishes on
/// its bus (<c>IPlayerWorld.Bus.Subscribe&lt;PlayerAreaChanged&gt;</c>) in
/// addition to fanning out to legacy <see cref="Subscribe"/> handlers.</para>
///
/// <para><b>Three consumer channels, one truth.</b>
/// <see cref="CurrentArea"/> (synchronous read) and <see cref="Subscribe"/>
/// (legacy callback with replay-on-attach) remain the idiomatic surfaces for
/// snapshot / event delivery. The world's bus carries the same
/// <see cref="PlayerAreaChanged"/> stream — restricted to
/// <see cref="PlayerAreaChangeKind.Changed"/>-kind events (snapshot replays
/// are synthesized inside <see cref="Subscribe"/> and never cross the world
/// boundary) — for cross-cutting consumers that prefer the architectural
/// <c>IPlayerWorld.Bus.Subscribe&lt;PlayerAreaChanged&gt;</c> path. Both
/// channels fire on identical content for genuine transitions; back-compat
/// consumers keep working; new code can choose either.</para>
///
/// <para><b>Legacy <see cref="Observe"/> push-in.</b> Legolas's
/// <c>PlayerLogIngestionService</c> and Gandalf's <c>LootIngestionService</c>
/// still feed already-classified lines inline during the migration window.
/// The double-feed (live envelope routed through the producer + Observe
/// push-in) is <b>state-idempotent</b> — both paths converge on the same
/// string-equality, last-writer-wins <see cref="CurrentArea"/>, and the
/// legacy <see cref="Subscribe"/> handlers fire from either path. Retirement
/// of <see cref="Observe"/> is owed once the two callers migrate to the bus
/// event (separate follow-on under
/// <a href="https://github.com/moumantai-gg/mithril/issues/774">#774</a>).</para>
///
/// <para><b>Asymmetry: bus emission is NOT idempotent across the double-feed.</b>
/// A transition routed through the producer → merger → <see cref="Apply"/>
/// path publishes a <see cref="PlayerAreaChanged"/> frame on
/// <c>IPlayerWorld.Bus</c>. The same transition routed through
/// <see cref="Observe"/> does not — <see cref="ApplyTransition"/> returns the
/// change list, but the legacy callers discard it (there's no world to hand it
/// to from a push-in context). So if <see cref="Observe"/> lands FIRST for a
/// given transition, the subsequent <see cref="Apply"/> sees the state already
/// matches and returns empty, and the bus emits ZERO frames for that
/// transition; if <see cref="Apply"/> lands first, the bus emits one frame
/// and <see cref="Observe"/> is the no-op. <see cref="CurrentArea"/> and the
/// <see cref="Subscribe"/> callback path are unaffected — both fire from
/// either path. The asymmetry only matters for bus subscribers that count
/// emissions (e.g., a hypothetical "area-transitions-per-session" metric);
/// consumers that read state-on-event don't notice. Resolves automatically
/// once the two <see cref="Observe"/> callers migrate to the bus event per
/// the #774 follow-on.</para>
///
/// <para><b>Threading.</b> The world drives <see cref="Apply"/> from its
/// merger thread; folder mutations + legacy handler dispatch run under
/// <see cref="_lock"/>. <see cref="Observe"/> is called from the legacy
/// ingestion paths on their own pump threads; the same lock serialises with
/// the world-driven path so the double-feed stays race-free.
/// <see cref="CurrentArea"/> reads take the same short critical section.
/// <see cref="Subscribe"/> replays and attaches atomically under the lock
/// the dispatch loop fires under, which closes the late-subscribe race.</para>
/// </summary>
public sealed class PlayerAreaTracker : IFolder<AreaLoadingFrame>, IPlayerAreaState
{
    private readonly AreaTransitionParser _parser;
    private readonly IDiagnosticsSink? _diag;
    private readonly object _lock = new();
    private readonly List<Action<PlayerAreaChanged>> _handlers = [];
    private string? _currentArea;

    // Most-recent envelope timestamp the tracker has applied (area
    // transition observed via Apply or Observe). Subscribe-replay stamps
    // its synthetic Snapshot notification with this so late subscribers
    // observe the same envelope-time view already-attached handlers were
    // given (principle 13: never leak wall clock into world-event-driven
    // state). Mirrors PlayerPinTracker._lastObservedAt /
    // PlayerWeatherTracker._lastObservedAt. Default (DateTimeOffset.MinValue)
    // when no envelope has been applied yet — the snapshot's
    // Current is also null in that case, so the stamp is never meaningful
    // in isolation.
    private DateTimeOffset _lastObservedAt;

    private const string DiagCategory = "GameState.Area";

    public PlayerAreaTracker(
        AreaTransitionParser parser,
        IDiagnosticsSink? diag = null)
    {
        _parser = parser ?? throw new ArgumentNullException(nameof(parser));
        _diag = diag;
    }

    /// <inheritdoc/>
    public string? CurrentArea
    {
        get { lock (_lock) return _currentArea; }
    }

    /// <inheritdoc/>
    public IDisposable Subscribe(Action<PlayerAreaChanged> handler)
    {
        ArgumentNullException.ThrowIfNull(handler);
        lock (_lock)
        {
            // Replay the current area as a Snapshot before going live so a
            // late subscriber observes the same view already-attached
            // handlers see. The stamp comes from the most-recent envelope
            // the tracker has applied (NOT wall-clock — principle 13);
            // see _lastObservedAt's field doc.
            Invoke(handler, new PlayerAreaChanged(
                PlayerAreaChangeKind.Snapshot,
                Previous: null,
                Current: _currentArea,
                At: _lastObservedAt));
            _handlers.Add(handler);
            return new Subscription(this, handler);
        }
    }

    /// <summary>
    /// Apply one area-loading frame to internal state. Returns a single
    /// <see cref="PlayerAreaChanged"/> (<see cref="PlayerAreaChangeKind.Changed"/>)
    /// when the frame's <see cref="AreaLoadingFrame.AreaKey"/> differs from
    /// the prior value; empty otherwise. Identical-area re-emits (zone
    /// replay) are no-ops, so consumers stay quiet through the L1 backlog
    /// drain. The clock parameter is unused — the change event carries the
    /// frame's own timestamp so state derivation stays replay-deterministic
    /// over the source stream (principle 5 + principle 13).
    /// </summary>
    public IReadOnlyList<IChangeEvent> Apply(Frame<AreaLoadingFrame> frame, IWorldClock clock)
    {
        _ = clock;
        return ApplyTransition(frame.Payload.AreaKey, frame.Timestamp);
    }

    /// <summary>
    /// Feed one log line through the area parser. Idempotent for unrelated
    /// lines (the parser's substring fast-path returns null without touching
    /// state). Legacy push-in for the Gandalf/Legolas bridges; new consumers
    /// should subscribe via <see cref="Subscribe"/> or the world bus instead.
    /// </summary>
    public void Observe(string line, DateTime timestamp)
    {
        if (_parser.TryParse(line, timestamp) is AreaTransitionEvent evt)
        {
            ApplyTransition(evt.AreaKey, new DateTimeOffset(
                DateTime.SpecifyKind(timestamp, DateTimeKind.Utc)));
        }
    }

    /// <summary>
    /// Shared mutation path. Returns the change-event list the folder hands
    /// to the world for bus publication; the legacy <see cref="Subscribe"/>
    /// handlers fire from the same path under <see cref="_lock"/>.
    /// </summary>
    private IReadOnlyList<IChangeEvent> ApplyTransition(string? areaKey, DateTimeOffset at)
    {
        PlayerAreaChanged change;
        Action<PlayerAreaChanged>[] toFire;
        lock (_lock)
        {
            if (_currentArea == areaKey) return Array.Empty<IChangeEvent>();
            var previous = _currentArea;
            _currentArea = areaKey;
            _lastObservedAt = at;
            change = new PlayerAreaChanged(
                PlayerAreaChangeKind.Changed, previous, areaKey, at);
            _diag?.Trace(DiagCategory,
                $"Player area transition → {areaKey ?? "(none)"} at {at:O}");
            toFire = _handlers.ToArray();
        }
        foreach (var h in toFire) Invoke(h, change);
        return new IChangeEvent[] { change };
    }

    private void Invoke(Action<PlayerAreaChanged> handler, PlayerAreaChanged change)
    {
        try { handler(change); }
        catch (Exception ex)
        {
            _diag?.Warn(DiagCategory, $"Subscriber threw: {ex.Message}");
        }
    }

    private sealed class Subscription : IDisposable
    {
        private PlayerAreaTracker? _owner;
        private readonly Action<PlayerAreaChanged> _handler;

        public Subscription(PlayerAreaTracker owner, Action<PlayerAreaChanged> handler)
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
