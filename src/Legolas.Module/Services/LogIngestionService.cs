using System.Windows;
using Mithril.Shared.Logging;
using Mithril.Shared.Modules;
using Legolas.Domain;
using Legolas.Flow;
using Legolas.ViewModels;
using Microsoft.Extensions.Hosting;

namespace Legolas.Services;

/// <summary>
/// Background service that consumes chat-log lines from <see cref="IChatLogStream"/>,
/// parses them via <see cref="IChatLogParser"/>, and pumps resulting events into the
/// session: SurveyDetected adds slots, ItemCollected marks the matching slot
/// collected, MotherlodeDistance forwards to the ML VM.
/// </summary>
public sealed class LogIngestionService : BackgroundService
{
    private readonly IChatLogStream _stream;
    private readonly IChatLogParser _parser;
    private readonly ModuleGates _gates;
    private readonly LegolasSettings _settings;
    private readonly SessionState _session;
    private readonly ICoordinateProjector _projector;
    private readonly MotherlodeViewModel _motherlode;
    private readonly SurveyFlowController _surveyFlow;
    private readonly IAreaCalibrationService _areaCalibration;

    public LogIngestionService(
        IChatLogStream stream,
        IChatLogParser parser,
        ModuleGates gates,
        LegolasSettings settings,
        SessionState session,
        ICoordinateProjector projector,
        MotherlodeViewModel motherlode,
        SurveyFlowController surveyFlow,
        IAreaCalibrationService areaCalibration)
    {
        _stream = stream;
        _parser = parser;
        _gates = gates;
        _settings = settings;
        _session = session;
        _projector = projector;
        _motherlode = motherlode;
        _surveyFlow = surveyFlow;
        _areaCalibration = areaCalibration;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await _gates.For("legolas").WaitAsync(stoppingToken).ConfigureAwait(false);

        await foreach (var raw in _stream.SubscribeAsync(stoppingToken).ConfigureAwait(false))
        {
            if (_parser.TryParse(raw.Line, raw.Timestamp) is GameEvent evt)
                Dispatch(evt);
        }
    }

    /// <summary>
    /// Buffer of recent "[Status] X xN added to inventory." adds. PG fires these
    /// before the matching "[Status] X collected!" line and they're the only
    /// place real item counts appear. We drain matching entries on each
    /// <see cref="ItemCollected"/> (primary + speed-bonus item if present) so
    /// unrelated adds (skinning, crafting, vendor purchases) get discarded
    /// instead of leaking into the survey report. Capped to bound memory if
    /// "collected!" never arrives for some buffered entry.
    /// </summary>
    private const int PendingAddBufferCap = 32;
    private readonly List<(string Name, int Count)> _pendingAdds = new();

    private void Dispatch(GameEvent evt)
    {
        PostToUi(() =>
        {
            _session.LastLogEvent = Describe(evt);
            switch (evt)
            {
                case SurveyDetected sd:
                    HandleSurveyDetected(sd);
                    break;
                case ItemAddedToInventory ia:
                    HandleItemAddedToInventory(ia);
                    break;
                case ItemCollected ic:
                    HandleItemCollected(ic);
                    break;
                case MotherlodeDistance md when _session.Mode == SessionMode.Motherlode:
                    _motherlode.RecordDistanceCommand.Execute(md.DistanceMetres);
                    break;
                case AreaEntered ae:
                    _areaCalibration.OnAreaEntered(ae.AreaFriendlyName);
                    break;
            }
        });
    }

    private static string Describe(GameEvent evt) => evt switch
    {
        SurveyDetected sd => $"Survey: {sd.Name} ({sd.Offset.East:0}E, {sd.Offset.North:0}N)",
        ItemAddedToInventory ia => $"Added: {ia.Name} x{ia.Count}",
        ItemCollected ic when ic.SpeedBonusItem is not null => $"Collected: {ic.Name} (+ {ic.SpeedBonusItem} speed bonus)",
        ItemCollected ic => $"Collected: {ic.Name}",
        MotherlodeDistance md => $"Motherlode: {md.DistanceMetres}m",
        AreaEntered ae => $"Area: {ae.AreaFriendlyName}",
        UnknownLine ul => $"Unknown: {ul.RawLine}",
        _ => evt.GetType().Name,
    };

    private static void PostToUi(Action action)
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is null) action();
        else dispatcher.InvokeAsync(action);
    }

    private void HandleSurveyDetected(SurveyDetected sd)
    {
        // Feed calibration test mode unconditionally — it must see the raw
        // reading even when survey mode is off or the FSM isn't accepting.
        _areaCalibration.NoteSurvey(sd.Name, sd.Offset);

        if (_session.Mode != SessionMode.Survey)
        {
            _session.LastLogEvent = $"Survey: {sd.Name} → ignored (mode is Motherlode)";
            return;
        }

        // #454 calibrated-area authority: when the current area has a
        // calibration, the absolute ProcessMapFx path (PlayerLogIngestionService)
        // owns placement. A survey use emits BOTH this chat [Status] line and a
        // Player.log ProcessMapFx line — suppress the relative auto-place here
        // so it doesn't double-place. NoteSurvey above still feeds the
        // calibration window's test mode. Uncalibrated areas fall through to
        // the legacy relative path (cold-start fallback until pin calibration).
        if (_areaCalibration.IsCurrentAreaCalibrated)
        {
            _session.LastLogEvent =
                $"Survey: {sd.Name} → absolute path owns placement (area calibrated)";
            return;
        }

        if (!_surveyFlow.CanAcceptSurvey)
        {
            // Controller writes its own diagnostic to LastLogEvent.
            _surveyFlow.NoteSurveyDetected(sd);
            return;
        }

        var duplicate = FindDuplicate(sd.Name, sd.Offset, _settings.SurveyDedupRadiusMetres);
        if (duplicate is not null)
        {
            duplicate.UpdateModel(duplicate.Model with { Offset = sd.Offset });
            return;
        }

        // Auto-place every survey at the projected position. The user only ever
        // interacts to *correct* a pin (drag/nudge), which sets ManualOverride and
        // drives Refit. A click during AwaitingPin used to set ManualOverride from
        // the click, but that conflated "place" with "correct" — every forced click
        // (often a guess) became calibration data. After the rework, placement
        // never sets ManualOverride; only explicit drags do.
        var index = _session.Surveys.Count;
        var newPixel = _projector.Project(sd.Offset);
        var survey = Survey.Create(sd.Name, sd.Offset, gridIndex: index)
            with { PixelPos = newPixel };
        var newPinVm = new SurveyItemViewModel(survey);
        _session.Surveys.Add(newPinVm);
        // Make the new pin the keyboard-nudge target so arrow-key adjustments
        // act on the just-arrived pin rather than the previously-selected one.
        _session.SelectedSurvey = newPinVm;
        _surveyFlow.NoteSurveyDetected(sd);
    }

    private SurveyItemViewModel? FindDuplicate(string name, MetreOffset newOffset, double radiusMetres)
    {
        var r2 = radiusMetres * radiusMetres;
        foreach (var s in _session.Surveys)
        {
            if (s.Collected) continue;
            if (!string.Equals(s.Name, name, StringComparison.OrdinalIgnoreCase)) continue;
            var dE = s.Offset.East - newOffset.East;
            var dN = s.Offset.North - newOffset.North;
            if (dE * dE + dN * dN <= r2) return s;
        }
        return null;
    }

    private void HandleItemAddedToInventory(ItemAddedToInventory ia)
    {
        _pendingAdds.Add((ia.Name, ia.Count));
        if (_pendingAdds.Count > PendingAddBufferCap)
            _pendingAdds.RemoveRange(0, _pendingAdds.Count - PendingAddBufferCap);
    }

    private void HandleItemCollected(ItemCollected ic)
    {
        // Drain pending "added to inventory" entries that match this collection.
        // Primary item + (optional) speed-bonus item are the only ones that count
        // toward this survey; everything else in the buffer is unrelated noise
        // (skinning drops, vendor purchases, etc.) and gets discarded after.
        var primaryCount = DrainPendingForName(ic.Name);
        AccumulateCollected(ic.Name, primaryCount);
        if (!string.IsNullOrEmpty(ic.SpeedBonusItem))
        {
            var bonusCount = DrainPendingForName(ic.SpeedBonusItem!);
            AccumulateCollected(ic.SpeedBonusItem!, bonusCount);
        }
        _pendingAdds.Clear();

        SurveyItemViewModel? best = null;
        var bestOrder = int.MaxValue;
        foreach (var s in _session.Surveys)
        {
            if (s.Collected) continue;
            if (!string.Equals(s.Name, ic.Name, StringComparison.OrdinalIgnoreCase)) continue;
            var order = s.RouteOrder ?? int.MaxValue;
            if (best is null || order < bestOrder)
            {
                best = s;
                bestOrder = order;
            }
        }

        if (best is not null)
        {
            best.UpdateModel(best.Model with { Collected = true });
            _session.LastLogEvent = $"Collected: {ic.Name} → marked";
            return;
        }

        _session.LastLogEvent = _session.Surveys.Count == 0
            ? $"Collected: {ic.Name} → no surveys tracked"
            : $"Collected: {ic.Name} → no name match (have {string.Join(", ", _session.Surveys.Where(s => !s.Collected).Select(s => s.Name).Take(3))})";
    }

    private int DrainPendingForName(string name)
    {
        // Walk back-to-front so we consume the most recent matching adds first
        // (and so RemoveAt indices stay valid as we shrink the list).
        var total = 0;
        for (var i = _pendingAdds.Count - 1; i >= 0; i--)
        {
            if (string.Equals(_pendingAdds[i].Name, name, StringComparison.OrdinalIgnoreCase))
            {
                total += _pendingAdds[i].Count;
                _pendingAdds.RemoveAt(i);
            }
        }
        // Defensive default: if the chat-log "added to inventory" line never arrived
        // (unusual, but timing skew can happen), credit at least one item so the
        // report doesn't drop the collection entirely.
        return total > 0 ? total : 1;
    }

    private void AccumulateCollected(string name, int count)
    {
        _session.CollectedItems.TryGetValue(name, out var existing);
        _session.CollectedItems[name] = existing + count;
    }
}
