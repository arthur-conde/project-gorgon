using Mithril.GameState.Celestial.Parsing;
using Mithril.Shared.Diagnostics;
using Mithril.Shared.Logging;
using Microsoft.Extensions.Hosting;

namespace Mithril.GameState.Celestial;

/// <summary>
/// Hosted-service implementation of <see cref="IPlayerCelestialState"/>.
/// Subscribes to the L1 (#550) driver's LocalPlayer pipe, parses
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
/// <para><b>Threading.</b> The L1 driver delivers envelopes on its pump
/// thread (archetype-A default = <c>DeliveryContext.Inline</c>);
/// <see cref="Current"/> reads and subscriber dispatch happen under
/// <c>_lock</c>. Subscribers doing non-trivial work should marshal off-thread
/// immediately (the Palantir VM dispatches to the UI thread).</para>
/// </summary>
public sealed class PlayerCelestialStateService : BackgroundService, IPlayerCelestialState
{
    private readonly ILogStreamDriver _driver;
    private readonly CelestialLogParser _parser;
    private readonly IDiagnosticsSink? _diag;

    private readonly object _lock = new();
    private readonly List<Action<CelestialInfo>> _handlers = [];
    private readonly HashSet<string> _reportedUnknownTokens = new(StringComparer.Ordinal);
    private CelestialInfo? _current;
    private ILogSubscription? _subscription;

    public PlayerCelestialStateService(
        ILogStreamDriver driver,
        CelestialLogParser parser,
        IDiagnosticsSink? diag = null)
    {
        _driver = driver;
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
        _diag?.Info("GameState.Celestial",
            "Subscribing to L1 driver (LocalPlayer pipe) for ProcessSetCelestialInfo");
        // archetype-A defaults — FromSessionStart replay + Inline delivery.
        // The parser consumes the envelope-stripped LocalPlayerLogLine.Data
        // directly — L0.5 (#532) owns actor classification, downstream
        // never re-matches the envelope (#550 PR #555 review).
        _subscription = _driver.Subscribe<LocalPlayerLogLine>(
            envelope =>
            {
                var ts = envelope.Payload.Timestamp.UtcDateTime;
                if (_parser.TryParse(envelope.Payload.Data, ts) is CelestialInfoEvent evt)
                {
                    if (evt.Phase == MoonPhase.Unknown)
                        ReportUnknownToken(evt.RawPhase);

                    Publish(new CelestialInfo(evt.Phase, evt.RawPhase, ToOffset(evt.Timestamp)));
                    _diag?.Trace("GameState.Celestial",
                        $"Moon phase: {evt.RawPhase} ({evt.Phase}) @ {evt.Timestamp:O}");
                }
                return ValueTask.CompletedTask;
            },
            new LogSubscriptionOptions
            {
                ReplayMode = ReplayMode.FromSessionStart,
                DeliveryContext = DeliveryContext.Inline,
                DiagnosticCategory = "GameState.Celestial",
            });

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
