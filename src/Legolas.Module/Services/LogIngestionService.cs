using System.Windows;
using Gorgon.Shared.Logging;
using Gorgon.Shared.Modules;
using Legolas.Domain;
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

    public LogIngestionService(
        IChatLogStream stream,
        IChatLogParser parser,
        ModuleGates gates,
        LegolasSettings settings,
        SessionState session,
        ICoordinateProjector projector,
        MotherlodeViewModel motherlode)
    {
        _stream = stream;
        _parser = parser;
        _gates = gates;
        _settings = settings;
        _session = session;
        _projector = projector;
        _motherlode = motherlode;
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
                case ItemCollected ic:
                    HandleItemCollected(ic);
                    break;
                case MotherlodeDistance md when _session.Mode == SessionMode.Motherlode:
                    _motherlode.RecordDistanceCommand.Execute(md.DistanceMetres);
                    break;
            }
        });
    }

    private static string Describe(GameEvent evt) => evt switch
    {
        SurveyDetected sd => $"Survey: {sd.Name} ({sd.Offset.East:0}E, {sd.Offset.North:0}N)",
        ItemCollected ic => $"Collected: {ic.Name} x{ic.Count}",
        MotherlodeDistance md => $"Motherlode: {md.DistanceMetres}m",
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
        if (_session.Mode != SessionMode.Survey)
        {
            _session.LastLogEvent = $"Survey: {sd.Name} → ignored (mode is Motherlode)";
            return;
        }
        if (_session.SurveyPhase == SurveyPhase.Idle)
        {
            _session.LastLogEvent = $"Survey: {sd.Name} → ignored (press Start first)";
            return;
        }

        var duplicate = FindDuplicate(sd.Name, sd.Offset, _settings.SurveyDedupRadiusMetres);
        if (duplicate is not null)
        {
            duplicate.UpdateModel(duplicate.Model with { Offset = sd.Offset });
            return;
        }
        var newPixel = _projector.Project(sd.Offset);

        var correctionCount = 0;
        foreach (var s in _session.Surveys)
        {
            if (s.Model.ManualOverride.HasValue) correctionCount++;
        }
        if (correctionCount >= 2)
        {
            var index = _session.Surveys.Count;
            var survey = Survey.Create(sd.Name, sd.Offset, gridIndex: index)
                with { PixelPos = newPixel };
            _session.Surveys.Add(new SurveyItemViewModel(survey));
            return;
        }

        _session.PendingSurvey = sd;
        _session.SurveyPhase = SurveyPhase.AwaitingPin;
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

    private void HandleItemCollected(ItemCollected ic)
    {
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
            _session.LastLogEvent = $"Collected: {ic.Name} x{ic.Count} → marked";
            return;
        }

        _session.LastLogEvent = _session.Surveys.Count == 0
            ? $"Collected: {ic.Name} x{ic.Count} → no surveys tracked"
            : $"Collected: {ic.Name} x{ic.Count} → no name match (have {string.Join(", ", _session.Surveys.Where(s => !s.Collected).Select(s => s.Name).Take(3))})";
    }
}
