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
/// owns the L1 subscription (and the pre-drain seed emission), parses every
/// <see cref="Mithril.Shared.Logging.SystemSignalKind.AreaLoading"/> envelope
/// via <see cref="AreaTransitionParser"/>, and emits one
/// <see cref="AreaLoadingFrame"/> per transition. The world drives
/// <see cref="Apply"/> per applied frame in source order; the folder returns
/// <see cref="PlayerAreaChanged"/> change events which the world publishes on
/// its bus (<c>IPlayerWorld.Bus.Subscribe&lt;PlayerAreaChanged&gt;</c>) in
/// addition to raising the legacy <see cref="AreaChanged"/> event.</para>
///
/// <para><b>Dual-surface back-compat.</b> <see cref="CurrentArea"/>
/// (synchronous read) is preserved verbatim — Gandalf's chest-commit path and
/// Palantir's debug refresh both rely on it. <see cref="Observe"/> also stays
/// alive: Legolas's <c>PlayerLogIngestionService</c> and Gandalf's
/// <c>LootIngestionService</c> still feed already-classified lines inline
/// during the migration window. The double-feed (live envelope routed through
/// the producer + Observe push-in) is idempotent — both paths converge on the
/// same string-equality, last-writer-wins state. Retirement of <c>Observe</c>
/// is owed once the two callers migrate to the bus event (separate follow-on
/// under #774); deleting it now would break the bridges the user's scope note
/// explicitly puts out of this PR.</para>
///
/// <para><b>Threading.</b> The world drives <see cref="Apply"/> from its
/// merger thread; folder mutations + change-event dispatch run under
/// <see cref="_lock"/>. <see cref="Observe"/> is called from the legacy
/// ingestion paths (Legolas/Gandalf bridges) on their own pump threads;
/// the same lock serialises with the world-driven path so the double-feed
/// stays race-free. <see cref="CurrentArea"/> reads take the same short
/// critical section.</para>
/// </summary>
public sealed class PlayerAreaTracker : IFolder<AreaLoadingFrame>, IPlayerAreaState
{
    private readonly AreaTransitionParser _parser;
    private readonly IDiagnosticsSink? _diag;
    private readonly object _lock = new();
    private string? _currentArea;

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
    public event EventHandler<PlayerAreaChanged>? AreaChanged;

    /// <summary>
    /// Apply one area-loading frame to internal state. Returns a single
    /// <see cref="PlayerAreaChanged"/> when the frame's
    /// <see cref="AreaLoadingFrame.AreaKey"/> differs from the prior value;
    /// empty otherwise. Identical-area re-emits (zone replay) are no-ops, so
    /// consumers stay quiet through the L1 backlog drain. The clock parameter
    /// is unused — the change event carries the frame's own timestamp so the
    /// state derivation stays replay-deterministic over the source stream
    /// (principle 5 + principle 13).
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
    /// should subscribe to <see cref="AreaChanged"/> or the world bus instead.
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
    /// Shared mutation path. Returns the change-event list both call sites
    /// honour: the folder hands it to the world for bus publication; the
    /// legacy <see cref="Observe"/> path drops it (the legacy callers
    /// pre-date the bus and only observe state via <see cref="CurrentArea"/>).
    /// Either way the <see cref="AreaChanged"/> event fires for any
    /// subscriber that has attached to the dual-surface event channel.
    /// </summary>
    private IReadOnlyList<IChangeEvent> ApplyTransition(string? areaKey, DateTimeOffset at)
    {
        PlayerAreaChanged? change = null;
        lock (_lock)
        {
            if (_currentArea == areaKey) return Array.Empty<IChangeEvent>();
            var previous = _currentArea;
            _currentArea = areaKey;
            change = new PlayerAreaChanged(previous, areaKey, at);
            _diag?.Trace(DiagCategory,
                $"Player area transition → {areaKey ?? "(none)"} at {at:O}");
        }
        RaiseAreaChanged(change);
        return new IChangeEvent[] { change };
    }

    private void RaiseAreaChanged(PlayerAreaChanged change)
    {
        var handler = AreaChanged;
        if (handler is null) return;
        try { handler.Invoke(this, change); }
        catch (Exception ex)
        {
            _diag?.Warn(DiagCategory, $"AreaChanged subscriber threw: {ex.Message}");
        }
    }
}
