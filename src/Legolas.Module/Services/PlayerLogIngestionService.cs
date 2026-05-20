using System.Windows;
using Legolas.Domain;
using Legolas.Flow;
using Legolas.ViewModels;
using Mithril.GameState.Areas;
using Mithril.Shared.Character;
using Mithril.Shared.Diagnostics;
using Mithril.Shared.Logging;
using Mithril.Shared.Modules;
using Microsoft.Extensions.Hosting;

namespace Legolas.Services;

/// <summary>
/// Legolas-owned LocalPlayer-pipe consumer. Distinct from
/// <see cref="LogIngestionService"/> (which tails the <em>chat</em> log for
/// <c>[Status]</c> survey/collect lines): this service watches the
/// L0.5 <see cref="LocalPlayerLogLine"/> pipe via the L1 (#550)
/// <see cref="ILogStreamDriver"/>, which is where #454's absolute-coordinate
/// signals live.
///
/// <para><b>This phase (Phase 2 of #454) — area context only.</b> The single
/// responsibility wired here is the area→calibration bridge: feed the shared
/// <see cref="PlayerAreaTracker"/> and, whenever the player's area changes,
/// apply that area's persisted <see cref="Domain.AreaCalibration"/> via
/// <see cref="IAreaCalibrationService.SelectArea"/>, plus <c>ProcessMapFx</c>
/// (absolute survey/treasure targets) placement and the #488
/// <c>ProcessDoDelayLoop</c> Motherlode-map use gesture (forwarded to the
/// <see cref="MotherlodeMeasurementCoordinator"/>). Map-pin lifecycle parsing was
/// promoted out to the GameState-tier <c>PlayerPinTracker</c> (#468) — one
/// LocalPlayer-pipe subscription here, consistent with the other consumers.</para>
///
/// <para><b>L1 migration (#550 PR 3, archetype-B).</b> Pre-L1 this service
/// owned its own <c>await foreach</c> over <see cref="IPlayerLogStream"/>,
/// stamped an in-memory <c>_liveSince</c> cutoff after the module gate, and
/// dropped <see cref="MapTargetDetected"/>/<see cref="MotherlodeUseDetected"/>
/// events whose timestamp predated it. The L1 driver replaces both with
/// composition: <see cref="ReplayMode.LiveOnly"/> drops the replay-phase
/// envelopes the upstream emits during session-start drain, and a persisted
/// per-character <c>SkipProcessedHighWater</c> on
/// <see cref="LegolasIngestionState.PlayerLogHighWaterSequence"/> drops live
/// envelopes whose <c>Sequence</c> we've already processed in a prior
/// session — closing the resurrection-on-restart edge the in-memory cutoff
/// never covered. The area bridge survives the <c>LiveOnly</c> tightening
/// because <see cref="PlayerAreaTracker"/> self-seeds (#514) from the most
/// recent <c>LOADING LEVEL</c> line in the log before this subscription
/// reads its first live envelope.</para>
///
/// <para><b>Mixed-handler subscription (#549 Divergence 2 option a).</b>
/// One subscription handles both the area bridge (formerly accepted replay
/// to apply pre-live area state, now satisfied by #514) and the live-only
/// survey/motherlode dispatch. Per #549's recommendation this stays a single
/// subscription — <c>LiveOnly</c> serves both purposes once the tracker
/// self-seeds.</para>
///
/// <para>Gated on the <c>"legolas"</c> module gate, mirroring
/// <see cref="LogIngestionService"/> — Legolas is a lazy module, so log work
/// starts on first tab activation. The ChatLog <c>Entering Area:</c> path in
/// <see cref="LogIngestionService"/> is retained as a complementary fallback;
/// both converge on the same area key (last-writer-wins, idempotent).</para>
///
/// <para><b>Containment.</b> The L1 driver wraps each handler invocation in
/// try/catch + rate-limited Warn, retiring the per-service <c>_diag?.Warn</c>
/// catch this service used to hold (#550 capability C). Failures surface on
/// <c>IDiagnosticsSink</c> under the <c>Legolas.PlayerLog</c> category via
/// the driver's <see cref="LogSubscriptionOptions.DiagnosticCategory"/>
/// override.</para>
///
/// <para><b>Marshalling.</b> <see cref="HandleMapTarget"/> mutates
/// <c>SessionState.Surveys</c> / <c>SelectedSurvey</c> /
/// <c>IsInventoryVisible</c> — all bound to the overlay — and
/// <c>IAreaCalibrationService.SelectArea</c> raises <c>Changed</c> observed
/// by calibration VMs. The L1
/// <see cref="DeliveryContext.Marshaled"/> option marshals every envelope
/// onto the WPF dispatcher before handler invocation, replacing the
/// hand-rolled <c>PostToUi</c> helper (#550 capability E).</para>
/// </summary>
public sealed class PlayerLogIngestionService : BackgroundService
{
    private readonly ILogStreamDriver _driver;
    private readonly PlayerLogParser _parser;
    private readonly PlayerAreaTracker _areaTracker;
    private readonly IAreaCalibrationService _areaCalibration;
    private readonly SurveyFlowController _flow;
    private readonly SessionState _session;
    private readonly MotherlodeMeasurementCoordinator _motherlode;
    private readonly LegolasSettings _settings;
    private readonly ModuleGates _gates;
    private readonly IActiveCharacterService? _activeChar;
    private readonly PerCharacterStore<LegolasIngestionState>? _ingestionStore;
    private readonly IDiagnosticsSink? _diag;

    private string? _lastAppliedArea;
    private ILogSubscription? _subscription;

    // High-water bookkeeping. Updated per envelope under no lock — the L1
    // driver delivers serially per subscription (one envelope at a time
    // through the bridge), so a plain field is race-free here. Persisted
    // on a short debounce so the on-disk value lags the in-memory one by
    // at most _highWaterFlushIntervalMs, and once on graceful shutdown.
    private long _highWaterSequence;
    private long _persistedHighWaterSequence;
    private string? _highWaterCharacter;
    private string? _highWaterServer;
    private readonly System.Timers.Timer _highWaterFlush;
    private const int HighWaterFlushIntervalMs = 1500;

    public PlayerLogIngestionService(
        ILogStreamDriver driver,
        PlayerLogParser parser,
        PlayerAreaTracker areaTracker,
        IAreaCalibrationService areaCalibration,
        SurveyFlowController flow,
        SessionState session,
        MotherlodeMeasurementCoordinator motherlode,
        LegolasSettings settings,
        ModuleGates gates,
        IActiveCharacterService? activeChar = null,
        PerCharacterStore<LegolasIngestionState>? ingestionStore = null,
        IDiagnosticsSink? diag = null)
    {
        _driver = driver;
        _parser = parser;
        _areaTracker = areaTracker;
        _areaCalibration = areaCalibration;
        _flow = flow;
        _session = session;
        _motherlode = motherlode;
        _settings = settings;
        _gates = gates;
        _activeChar = activeChar;
        _ingestionStore = ingestionStore;
        _diag = diag;

        _highWaterFlush = new System.Timers.Timer(HighWaterFlushIntervalMs) { AutoReset = false };
        _highWaterFlush.Elapsed += (_, _) => FlushHighWater();
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await _gates.For("legolas").WaitAsync(stoppingToken).ConfigureAwait(false);

        // Resolve the persisted high-water for the active character (if any)
        // BEFORE we hand the value to the L1 driver — once Subscribe latches
        // SkipProcessedHighWater into its options bag a later character
        // switch can't change the threshold. The character-changed event
        // updates the in-memory write-side so persistence still tracks the
        // active character, but the driver's filter stays whatever was
        // active at gate-open. See "Character-switch caveat" below.
        var initialHighWater = ResolveInitialHighWater();

        // Area is seeded by the shared PlayerAreaTracker itself (one-shot,
        // owned — mithril#514); this consumer only reads it. ApplyAreaIfChanged
        // reads CurrentArea, which triggers PlayerAreaTracker.EnsureSeeded
        // on first call — so even though LiveOnly drops the replay drain
        // we still pick up the current area before any live envelope arrives.
        ApplyAreaIfChanged();

        var dispatcher = Application.Current?.Dispatcher;
        // archetype-B per #549 Legolas/PlayerLog disposition:
        //   LiveOnly        — drops the replay drain so finished-run pins
        //                     don't repopulate. Area bridge no longer needs
        //                     replay (#514 self-seed).
        //   Marshaled       — HandleMapTarget mutates Surveys /
        //                     SelectedSurvey / IsInventoryVisible (bound to
        //                     overlay) and IAreaCalibrationService.SelectArea
        //                     raises Changed observed by calibration VMs.
        //   SkipProcessedHighWater — restart-safe dedup: a Sequence value
        //                     we processed in a prior session is dropped
        //                     before the handler ever sees it. Replaces the
        //                     in-memory _liveSince cutoff with a persisted
        //                     Sequence-based one.
        _subscription = _driver.Subscribe<LocalPlayerLogLine>(
            envelope =>
            {
                var payload = envelope.Payload;
                var ts = payload.Timestamp.UtcDateTime;
                var line = payload.Data;

                // Area first — a target line in the same batch must read the
                // correct current-area calibration. PlayerAreaTracker takes
                // the bare envelope-stripped payload (L0.5 owns the actor
                // envelope; downstream never re-matches it).
                _areaTracker.Observe(line, ts);
                ApplyAreaIfChanged();

                // Map pins (ProcessMapPin{Add,Remove}) are owned by the
                // GameState-tier PlayerPinTracker (#468); this service handles
                // ProcessMapFx absolute targets (#454) and the
                // ProcessDoDelayLoop Motherlode-map use gesture (#488).
                switch (_parser.TryParse(line, ts))
                {
                    case MapTargetDetected mt:
                        HandleMapTarget(mt);
                        break;

                    // The use gesture only matters in Motherlode mode. The
                    // pre-L1 `use.Timestamp >= _liveSince` guard at this
                    // case is now a no-op: LiveOnly drops the replay-phase
                    // backlog and the high-water filter drops re-runs from
                    // a prior Mithril session.
                    case MotherlodeUseDetected use
                        when _session.Mode == SessionMode.Motherlode:
                        var at = new DateTimeOffset(
                            DateTime.SpecifyKind(use.Timestamp, DateTimeKind.Utc));
                        _motherlode.OnUse(at, use.MapName);
                        break;
                }

                // Update the high-water AFTER successful handling so a
                // throwing handler doesn't advance the cursor past a line
                // we never processed. The L1 driver's containment wrap
                // means a throw inside this block is caught by the driver,
                // not propagated to the pump — but the next envelope's
                // Sequence will still be > the one that threw, so a
                // post-handler advance keeps the semantics aligned with
                // "advance when we successfully processed this Sequence".
                AdvanceHighWater(payload.Sequence);

                return ValueTask.CompletedTask;
            },
            new LogSubscriptionOptions
            {
                ReplayMode = ReplayMode.LiveOnly,
                DeliveryContext = dispatcher is null
                    ? DeliveryContext.Inline
                    : DeliveryContext.Marshaled(dispatcher),
                SkipProcessedHighWater = initialHighWater > 0 ? initialHighWater : null,
                DiagnosticCategory = "Legolas.PlayerLog",
            });

        _diag?.Info("Legolas.PlayerLog",
            $"Subscribed to L1 driver (LocalPlayer pipe) — LiveOnly, high-water={initialHighWater}");

        // Park until the host stops. The L1 subscription runs its own pump
        // on a Task.Run; ExecuteAsync's job is to keep us alive long enough
        // to be disposed.
        try { await Task.Delay(Timeout.Infinite, stoppingToken).ConfigureAwait(false); }
        catch (OperationCanceledException) { /* expected on host stop */ }
        finally
        {
            _subscription?.Dispose();
            _subscription = null;
            // Final synchronous flush so an in-flight debounce doesn't lose
            // the last few Sequence advances on a clean shutdown.
            _highWaterFlush.Stop();
            FlushHighWater();
        }
    }

    public override void Dispose()
    {
        _subscription?.Dispose();
        _subscription = null;
        _highWaterFlush.Stop();
        _highWaterFlush.Dispose();
        base.Dispose();
    }

    /// <summary>
    /// Place an absolute <c>ProcessMapFx</c> target. Authoritative only for a
    /// <b>calibrated</b> area (#454 "calibrated-area authority"): uncalibrated
    /// areas keep the legacy relative chat path (the cold-start fallback until
    /// pin calibration exists, Phase 4). The relative chat auto-place is
    /// suppressed for calibrated areas in <see cref="LogIngestionService"/> so
    /// a survey use — which emits both a chat <c>[Status]</c> line and this
    /// Player.log line — produces exactly one pin.
    /// </summary>
    private void HandleMapTarget(MapTargetDetected mt)
    {
        if (_session.Mode != SessionMode.Survey)
        {
            _session.LastLogEvent = $"Map target: {Describe(mt)} → ignored (mode is Motherlode)";
            return;
        }

        // Pre-L1 this method held a `mt.Timestamp < _liveSince` guard that
        // dropped session-replay backlog from a finished run. With the L1
        // LiveOnly subscription + persisted high-water filter, neither
        // resurrection path can reach here:
        //   • LiveOnly suppresses replay-phase envelopes inside one session.
        //   • SkipProcessedHighWater suppresses live envelopes whose
        //     Sequence was already processed in a prior session.
        // The timestamp comparison is now dead code; this comment is its
        // grave marker so a future audit doesn't restore it under the
        // mistaken belief the cases above leak through.

        // FSM gate: a target only places while the survey flow is actually
        // accepting one. Listening (resting/default) and Gathering (route in
        // progress — new targets welcome, #454) accept; Done (run finished,
        // awaiting reset) and SettingPosition (transient position-override
        // detour) do not.
        if (_flow.CurrentState is not (SurveyFlowState.Listening or SurveyFlowState.Gathering))
        {
            _session.LastLogEvent =
                $"Map target: {Describe(mt)} → ignored (survey flow is {_flow.CurrentState})";
            return;
        }

        if (_areaCalibration.CurrentCalibration is not { } cal)
        {
            _session.LastLogEvent =
                $"Map target: {Describe(mt)} → area not calibrated; run pin calibration";
            return;
        }

        var name = CleanName(mt.Short);
        var pixel = cal.ProjectWorld(mt.World);

        if (FindDuplicateAbsolute(mt.World, _settings.MapTargetDedupRadiusMetres) is { } dup)
        {
            // Same node re-surveyed re-emits an identical (X,Z); refresh its
            // projected pixel (a mid-session recalibration may have moved it)
            // rather than stacking a duplicate.
            dup.UpdateModel(dup.Model with { PixelPos = pixel, World = mt.World });
            _session.LastLogEvent = $"Map target: {name} → duplicate (X,Z), updated";
            return;
        }

        var index = _session.Surveys.Count;
        var pinVm = new SurveyItemViewModel(
            Survey.CreateAbsolute(name, mt.World, pixel, index));
        _session.Surveys.Add(pinVm);
        // New pin becomes the keyboard-nudge target (parity with the chat
        // path); surface the inventory grid so the next slot is visible.
        _session.SelectedSurvey = pinVm;
        _session.IsInventoryVisible = true;
        _session.LastLogEvent = $"Map target: {name} → placed (absolute)";
    }

    private SurveyItemViewModel? FindDuplicateAbsolute(WorldCoord world, double radiusMetres)
    {
        var r2 = radiusMetres * radiusMetres;
        foreach (var s in _session.Surveys)
        {
            if (s.Collected) continue;
            if (s.Model.World is not { } w) continue;
            var dx = w.X - world.X;
            var dz = w.Z - world.Z;
            if (dx * dx + dz * dz <= r2) return s;
        }
        return null;
    }

    /// <summary>
    /// The <c>short</c> field is consistently <c>"&lt;NodeName&gt; is here"</c>;
    /// strip the suffix for a clean pin label. Display-only — placement uses
    /// <see cref="MapTargetDetected.World"/> exclusively.
    /// </summary>
    private static string CleanName(string shortText)
    {
        const string suffix = " is here";
        var t = shortText.Trim();
        return t.EndsWith(suffix, StringComparison.OrdinalIgnoreCase)
            ? t[..^suffix.Length].Trim()
            : t;
    }

    private static string Describe(MapTargetDetected mt) =>
        $"{CleanName(mt.Short)} @ ({mt.World.X:0},{mt.World.Z:0})";

    /// <summary>
    /// Apply the current area's persisted calibration when (and only when) the
    /// tracker's area actually changes. A null area (character-select /
    /// disconnect) resets the latch so re-entering the same area later
    /// re-applies — defensive against any intervening projector reset.
    /// </summary>
    private void ApplyAreaIfChanged()
    {
        // First read triggers PlayerAreaTracker.EnsureSeeded (#514) — the
        // tracker self-scans the log for the most recent LOADING LEVEL and
        // resolves the current area before returning. So even though we
        // subscribe LiveOnly (no replay drain), the area we read here is
        // the *real* current area, not null.
        var area = _areaTracker.CurrentArea;
        if (area is null)
        {
            _lastAppliedArea = null;
            return;
        }
        if (area == _lastAppliedArea) return;
        _lastAppliedArea = area;
        _areaCalibration.SelectArea(area);
    }

    /// <summary>
    /// Resolve the high-water Sequence to feed into the L1 subscription at
    /// gate-open. Loads the active character's persisted
    /// <see cref="LegolasIngestionState"/>, captures the resulting Character +
    /// Server identity so subsequent flushes target the same file even if
    /// the active character changes mid-session.
    ///
    /// <para>Returns <c>0</c> (which the driver treats as "no high-water"
    /// when paired with the null nullable below) if no active character or
    /// no persistence store is available — the in-memory write side still
    /// tracks the highest Sequence we've seen so a switch-then-back later
    /// in the session resumes coherently.</para>
    /// </summary>
    private long ResolveInitialHighWater()
    {
        if (_ingestionStore is null) return 0;
        if (_activeChar is null) return 0;
        var name = _activeChar.ActiveCharacterName;
        var server = _activeChar.ActiveServer;
        if (string.IsNullOrEmpty(name) || string.IsNullOrEmpty(server)) return 0;

        try
        {
            var state = _ingestionStore.Load(name, server);
            _highWaterCharacter = name;
            _highWaterServer = server;
            _highWaterSequence = state.PlayerLogHighWaterSequence;
            _persistedHighWaterSequence = state.PlayerLogHighWaterSequence;
            return state.PlayerLogHighWaterSequence;
        }
        catch (Exception ex)
        {
            _diag?.Warn("Legolas.PlayerLog",
                $"Failed to load persisted high-water for {name}/{server}: {ex.Message}");
            return 0;
        }
    }

    /// <summary>
    /// Advance the in-memory high-water. Monotonic per the driver's filter
    /// contract (a Sequence we've seen is one the driver let through, so
    /// it's strictly greater than every prior); the Math.Max guard handles
    /// any pathological reordering at the test-fake boundary.
    /// </summary>
    private void AdvanceHighWater(long sequence)
    {
        if (sequence <= 0) return;
        if (sequence > _highWaterSequence)
        {
            _highWaterSequence = sequence;
            // Debounce the disk write — a hot stream produces thousands of
            // envelopes a session, but only the latest value matters.
            _highWaterFlush.Stop();
            _highWaterFlush.Start();
        }
    }

    /// <summary>
    /// Persist the in-memory high-water if it advanced since the last flush.
    /// Best-effort: a failed write logs a Warn but doesn't propagate (a missed
    /// flush only means the next session's restart-resume covers a slightly
    /// wider Sequence range than strictly necessary; LiveOnly still prevents
    /// the resurrection bug class within-session).
    /// </summary>
    private void FlushHighWater()
    {
        if (_ingestionStore is null) return;
        if (string.IsNullOrEmpty(_highWaterCharacter) || string.IsNullOrEmpty(_highWaterServer)) return;
        var current = _highWaterSequence;
        if (current <= _persistedHighWaterSequence) return;
        try
        {
            var state = new LegolasIngestionState { PlayerLogHighWaterSequence = current };
            _ingestionStore.Save(_highWaterCharacter, _highWaterServer, state);
            _persistedHighWaterSequence = current;
        }
        catch (Exception ex)
        {
            _diag?.Warn("Legolas.PlayerLog",
                $"Failed to persist high-water {current} for {_highWaterCharacter}/{_highWaterServer}: {ex.Message}");
        }
    }
}
