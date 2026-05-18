using System.Windows;
using Legolas.Domain;
using Legolas.Flow;
using Legolas.ViewModels;
using Mithril.GameState.Areas;
using Mithril.Shared.Diagnostics;
using Mithril.Shared.Game;
using Mithril.Shared.Logging;
using Mithril.Shared.Modules;
using Microsoft.Extensions.Hosting;

namespace Legolas.Services;

/// <summary>
/// Legolas-owned <see cref="IPlayerLogStream"/> consumer. Distinct from
/// <see cref="LogIngestionService"/> (which tails the <em>chat</em> log for
/// <c>[Status]</c> survey/collect lines): this service watches
/// <c>Player.log</c>, which is where #454's absolute-coordinate signals live.
///
/// <para><b>This phase (Phase 2 of #454) — area context only.</b> The single
/// responsibility wired here is the area→calibration bridge: feed the shared
/// <see cref="PlayerAreaTracker"/> and, whenever the player's area changes,
/// apply that area's persisted <see cref="Domain.AreaCalibration"/> via
/// <see cref="IAreaCalibrationService.SelectArea"/>, plus <c>ProcessMapFx</c>
/// (absolute survey/treasure targets) placement. Map-pin lifecycle parsing was
/// promoted out to the GameState-tier <c>PlayerPinTracker</c> (#468) — one
/// Player.log subscription here, consistent with the other consumers.</para>
///
/// <para>The shared tracker is reverse-scan-seeded at startup
/// (<see cref="PlayerAreaTracker.SeedFromLog"/>) to close the gap where the
/// live replay window starts after the <c>LOADING LEVEL</c> line for the
/// current area — so a session that opens Legolas while already standing in a
/// calibrated area still applies that calibration immediately. The tracker is
/// a thread-safe singleton; double-observing it (Gandalf also feeds it) is
/// idempotent.</para>
///
/// <para>Gated on the <c>"legolas"</c> module gate, mirroring
/// <see cref="LogIngestionService"/> — Legolas is a lazy module, so log work
/// starts on first tab activation. The ChatLog <c>Entering Area:</c> path in
/// <see cref="LogIngestionService"/> is retained as a complementary fallback;
/// both converge on the same area key (last-writer-wins, idempotent).</para>
/// </summary>
public sealed class PlayerLogIngestionService : BackgroundService
{
    private readonly IPlayerLogStream _stream;
    private readonly PlayerLogParser _parser;
    private readonly PlayerAreaTracker _areaTracker;
    private readonly IAreaCalibrationService _areaCalibration;
    private readonly SurveyFlowController _flow;
    private readonly SessionState _session;
    private readonly LegolasSettings _settings;
    private readonly ModuleGates _gates;
    private readonly GameConfig _config;
    private readonly TimeProvider _time;
    private readonly IDiagnosticsSink? _diag;

    private string? _lastAppliedArea;

    /// <summary>
    /// UTC instant this consumer went live. <c>ProcessMapFx</c> lines stamped
    /// before it are the session-replay backlog (PG/the stream replays from
    /// session start so the area→calibration bridge and the #468 pin tracker
    /// can catch up) — they must NOT auto-populate stale survey pins from a run
    /// the user already finished before opening Legolas. Set once when
    /// ingestion starts.
    /// </summary>
    private DateTime _liveSince = DateTime.MinValue;

    public PlayerLogIngestionService(
        IPlayerLogStream stream,
        PlayerLogParser parser,
        PlayerAreaTracker areaTracker,
        IAreaCalibrationService areaCalibration,
        SurveyFlowController flow,
        SessionState session,
        LegolasSettings settings,
        ModuleGates gates,
        GameConfig config,
        TimeProvider? time = null,
        IDiagnosticsSink? diag = null)
    {
        _stream = stream;
        _parser = parser;
        _areaTracker = areaTracker;
        _areaCalibration = areaCalibration;
        _flow = flow;
        _session = session;
        _settings = settings;
        _gates = gates;
        _config = config;
        _time = time ?? TimeProvider.System;
        _diag = diag;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await _gates.For("legolas").WaitAsync(stoppingToken).ConfigureAwait(false);

        // Everything already in Player.log at this point is replay/backlog —
        // anchor the live cutoff before the stream yields the session-replay
        // buffer, so a finished run's targets don't repopulate as fresh pins.
        _liveSince = _time.GetUtcNow().UtcDateTime;

        // One-shot reverse-scan for the most recent LOADING LEVEL Area* line
        // before the live tail kicks in — best-effort, never blocks ingestion.
        if (!string.IsNullOrEmpty(_config.PlayerLogPath))
            _areaTracker.SeedFromLog(_config.PlayerLogPath);
        ApplyAreaIfChanged();

        await foreach (var raw in _stream.SubscribeAsync(stoppingToken).ConfigureAwait(false))
        {
            try
            {
                // Area first — a target line in the same batch must read the
                // correct current-area calibration.
                _areaTracker.Observe(raw);
                ApplyAreaIfChanged();

                // Map pins (ProcessMapPin{Add,Remove}) are owned by the
                // GameState-tier PlayerPinTracker (#468); this service only
                // handles ProcessMapFx absolute targets now.
                if (_parser.TryParse(raw.Line, raw.Timestamp) is MapTargetDetected mt)
                    PostToUi(() => HandleMapTarget(mt));
            }
            catch (Exception ex)
            {
                _diag?.Warn("Legolas.PlayerLog", $"Ingestion error: {ex.Message}");
            }
        }
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

        // Replay/backlog guard: the Player.log stream replays from session
        // start (the area/calibration bridge and #468 pin tracker need it).
        // A target stamped before this consumer went live is a pin from a run
        // the user already finished — placing it would resurrect stale surveys
        // on startup. Only genuinely-live uses (after Legolas opened) place.
        if (mt.Timestamp < _liveSince)
        {
            _session.LastLogEvent =
                $"Map target: {Describe(mt)} → ignored (log replay / pre-session backlog)";
            return;
        }

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
        var area = _areaTracker.CurrentArea;
        if (area is null)
        {
            _lastAppliedArea = null;
            return;
        }
        if (area == _lastAppliedArea) return;
        _lastAppliedArea = area;
        PostToUi(() => _areaCalibration.SelectArea(area));
    }

    /// <summary>
    /// Marshal to the WPF dispatcher — <see cref="IAreaCalibrationService.SelectArea"/>
    /// touches the projector and raises <c>Changed</c>, which calibration VMs
    /// observe. Falls back to a direct call in headless/test contexts. Mirrors
    /// <see cref="LogIngestionService"/>'s helper of the same name.
    /// </summary>
    private static void PostToUi(Action action)
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is null) action();
        else dispatcher.InvokeAsync(action);
    }
}
