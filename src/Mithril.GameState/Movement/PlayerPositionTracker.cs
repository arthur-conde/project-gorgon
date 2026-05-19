using Mithril.GameState.Areas;
using Mithril.Shared.Diagnostics;
using Mithril.Shared.Logging;
using Microsoft.Extensions.Hosting;

namespace Mithril.GameState.Movement;

/// <summary>
/// Hosted-service implementation of <see cref="IPlayerPositionTracker"/>.
/// Tails <see cref="IPlayerLogStream"/>, parses <c>ProcessNewPosition</c> and
/// the local player's <c>ProcessAddPlayer</c> via
/// <see cref="PlayerPositionParser"/>, and holds the latest
/// <see cref="PlayerPosition"/> (coords + the line's UTC instant + source).
/// The <c>ProcessAddPlayer</c> spawn line is the live-replay seed point, so
/// position is populated at session start rather than null until the first
/// teleport — last-writer-wins keeps it advancing as the player relogs/zones.
///
/// <para><b>Why a self-feeding BackgroundService.</b> Mirrors
/// <see cref="Sessions.GameSessionService"/> / <c>QuestService</c>: the
/// position is shared live game-state and must be available to consumers
/// (e.g. Palantir's debug surface) without depending on any other module
/// being activated. Unlike the passive <see cref="PlayerAreaTracker"/>
/// (which is fed by Legolas/Gandalf consumers), this owns its own
/// subscription.</para>
///
/// <para><b>Also warms the shared area tracker.</b> The <c>LOADING LEVEL</c>
/// area line lives in the same Player.log, so this loop also feeds the
/// shared <see cref="PlayerAreaTracker"/> (and reverse-scan-seeds it at
/// startup, since the live replay window opens <em>after</em> the current
/// area's <c>LOADING LEVEL</c> line). Double-observing the tracker is
/// idempotent — Legolas/Gandalf may also feed it when active; last-writer
/// wins on the same key. This makes the area half of Palantir's
/// map+position view populate standalone.</para>
///
/// <para><b>Threading.</b> Ingestion runs on the hosted-service loop thread;
/// <see cref="Current"/> reads and subscriber dispatch happen under
/// <c>_lock</c>. Subscribers doing non-trivial work should marshal off-thread
/// immediately (the Palantir VM dispatches to the UI thread).</para>
/// </summary>
public sealed class PlayerPositionTracker : BackgroundService, IPlayerPositionTracker
{
    private readonly IPlayerLogStream _stream;
    private readonly PlayerPositionParser _parser;
    private readonly PlayerAreaTracker _areaTracker;
    private readonly IDiagnosticsSink? _diag;

    private readonly object _lock = new();
    private readonly List<Action<PlayerPosition>> _handlers = [];
    private PlayerPosition? _current;

    public PlayerPositionTracker(
        IPlayerLogStream stream,
        PlayerPositionParser parser,
        PlayerAreaTracker areaTracker,
        IDiagnosticsSink? diag = null)
    {
        _stream = stream;
        _parser = parser;
        _areaTracker = areaTracker;
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
        // Area is seeded by the shared PlayerAreaTracker itself (one-shot,
        // owned — mithril#514); this service only feeds/reads it.
        _diag?.Info("GameState.Position", "Subscribing to Player.log for ProcessNewPosition");
        await foreach (var raw in _stream.SubscribeAsync(stoppingToken).ConfigureAwait(false))
        {
            try
            {
                // Same line stream carries area transitions; keep the shared
                // tracker warm even when no other module is feeding it.
                _areaTracker.Observe(raw);

                if (_parser.TryParse(raw.Line, raw.Timestamp) is PlayerPositionEvent evt)
                    Publish(new PlayerPosition(evt.X, evt.Y, evt.Z, ToOffset(evt.Timestamp), evt.Source));
            }
            catch (Exception ex)
            {
                _diag?.Warn("GameState.Position", $"Ingestion error: {ex.Message}");
            }
        }
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
    /// Player.log timestamps are reconstructed as UTC <see cref="DateTime"/>s
    /// by <c>LogLineTimestampSequencer</c>. Stamp the kind defensively in case
    /// a caller passes an Unspecified-kind value (tests), so the offset is
    /// always +00:00 rather than the host's local offset.
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
