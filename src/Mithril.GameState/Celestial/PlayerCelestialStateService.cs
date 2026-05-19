using Mithril.GameState.Celestial.Parsing;
using Mithril.Shared.Diagnostics;
using Mithril.Shared.Logging;
using Microsoft.Extensions.Hosting;

namespace Mithril.GameState.Celestial;

/// <summary>
/// Hosted-service implementation of <see cref="IPlayerCelestialState"/>.
/// Tails <see cref="IPlayerLogStream"/>, parses
/// <c>ProcessSetCelestialInfo</c> via <see cref="CelestialLogParser"/>, and
/// holds the latest <see cref="CelestialInfo"/> (phase + the line's UTC
/// instant). Last-writer-wins keeps it advancing across phase roll-overs and
/// relogs.
///
/// <para><b>Why a self-feeding BackgroundService.</b> Mirrors
/// <see cref="Movement.PlayerPositionTracker"/>: the lunar phase is shared
/// live game-state and must be available to consumers (Palantir's debug
/// surface today; potential Silmarillion "active now" / Gandalf phase alarms
/// later) without depending on any other module being activated. Unlike
/// position, there is no second source line and no area to warm, so the loop
/// is a plain parse-and-publish.</para>
///
/// <para><b>Threading.</b> Ingestion runs on the hosted-service loop thread;
/// <see cref="Current"/> reads and subscriber dispatch happen under
/// <c>_lock</c>. Subscribers doing non-trivial work should marshal off-thread
/// immediately (the Palantir VM dispatches to the UI thread).</para>
/// </summary>
public sealed class PlayerCelestialStateService : BackgroundService, IPlayerCelestialState
{
    private readonly IPlayerLogStream _stream;
    private readonly CelestialLogParser _parser;
    private readonly IDiagnosticsSink? _diag;

    private readonly object _lock = new();
    private readonly List<Action<CelestialInfo>> _handlers = [];
    private readonly HashSet<string> _reportedUnknownTokens = new(StringComparer.Ordinal);
    private CelestialInfo? _current;

    public PlayerCelestialStateService(
        IPlayerLogStream stream,
        CelestialLogParser parser,
        IDiagnosticsSink? diag = null)
    {
        _stream = stream;
        _parser = parser;
        _diag = diag;
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
            // Replay the current phase before going live so a late subscriber
            // observes the same view already-attached handlers see. Mirrors
            // PlayerPositionTracker.Subscribe.
            if (_current is not null) Invoke(handler, _current);
            _handlers.Add(handler);
            return new Subscription(this, handler);
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _diag?.Info("GameState.Celestial", "Subscribing to Player.log for ProcessSetCelestialInfo");
        await foreach (var raw in _stream.SubscribeAsync(stoppingToken).ConfigureAwait(false))
        {
            try
            {
                if (_parser.TryParse(raw.Line, raw.Timestamp.UtcDateTime) is CelestialInfoEvent evt)
                {
                    if (evt.Phase == MoonPhase.Unknown)
                        ReportUnknownToken(evt.RawPhase);

                    Publish(new CelestialInfo(evt.Phase, evt.RawPhase, ToOffset(evt.Timestamp)));
                    _diag?.Trace("GameState.Celestial",
                        $"Moon phase: {evt.RawPhase} ({evt.Phase}) @ {evt.Timestamp:O}");
                }
            }
            catch (Exception ex)
            {
                _diag?.Warn("GameState.Celestial", $"Ingestion error: {ex.Message}");
            }
        }
    }

    /// <summary>
    /// PG emitted a celestial token that maps to no <see cref="MoonPhase"/>
    /// member — the canonical enum / token map is missing a case (a game
    /// patch added a phase, or a quarter token's spelling differs from the
    /// assumed astronomical name). Logged at <see cref="DiagnosticLevel.Error"/>
    /// because it is a code-level gap we want surfaced loudly, not a transient
    /// runtime hiccup. Deduped per distinct raw token: PG re-emits
    /// <c>ProcessSetCelestialInfo</c> on every login + phase roll-over, so an
    /// unmapped token would otherwise flood the Error log indefinitely.
    /// </summary>
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
        private PlayerCelestialStateService? _owner;
        private readonly Action<CelestialInfo> _handler;

        public Subscription(PlayerCelestialStateService owner, Action<CelestialInfo> handler)
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
