using System.Windows;
using Mithril.Shared.Diagnostics;
using Mithril.Shared.Logging;
using Mithril.Shared.Modules;
using Legolas.Domain;
using Legolas.ViewModels;
using Microsoft.Extensions.Hosting;

namespace Legolas.Services;

/// <summary>
/// Background service that consumes chat-log lines from <see cref="IChatLogStream"/>,
/// parses them via <see cref="IChatLogParser"/>, and pumps resulting events into the
/// session: SurveyDetected adds slots, ItemCollected marks the matching slot
/// collected, MotherlodeDistance + Metal-Slab collection forward to the
/// <see cref="MotherlodeMeasurementCoordinator"/> (#488).
/// </summary>
public sealed class LogIngestionService : BackgroundService
{
    private readonly IChatLogStream _stream;
    private readonly IChatLogParser _parser;
    private readonly ModuleGates _gates;
    private readonly SessionState _session;
    private readonly MotherlodeMeasurementCoordinator _motherlode;
    private readonly IAreaCalibrationService _areaCalibration;
    private readonly ThrottledWarn _warn;

    public LogIngestionService(
        IChatLogStream stream,
        IChatLogParser parser,
        ModuleGates gates,
        SessionState session,
        MotherlodeMeasurementCoordinator motherlode,
        IAreaCalibrationService areaCalibration,
        IDiagnosticsSink? diag = null)
    {
        _stream = stream;
        _parser = parser;
        _gates = gates;
        _session = session;
        _motherlode = motherlode;
        _areaCalibration = areaCalibration;
        _warn = new ThrottledWarn(diag, "Legolas.Ingestion");
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await _gates.For("legolas").WaitAsync(stoppingToken).ConfigureAwait(false);

        await foreach (var raw in _stream.SubscribeAsync(stoppingToken).ConfigureAwait(false))
        {
            try
            {
                if (_parser.TryParse(raw.Line, raw.Timestamp.UtcDateTime) is GameEvent evt)
                    Dispatch(evt);
            }
            catch (Exception ex) { _warn.Warn($"Ingestion error: {ex.Message}"); }
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
                    // Motherlode no longer infers completion from a "Metal Slab"
                    // add: the dig is the map's IInventoryService Deleted event
                    // (authoritative, exact), and loot is decoupled (the dig
                    // spawns a mining node — no log link). Survey still uses it.
                    if (_session.Mode != SessionMode.Motherlode)
                        HandleItemAddedToInventory(ia);
                    break;
                case ItemCollected ic:
                    HandleItemCollected(ic);
                    break;
                case MotherlodeDistance md when _session.Mode == SessionMode.Motherlode:
                    // md.Timestamp was carried through as raw.Timestamp.UtcDateTime
                    // at the parser boundary above, so it is Kind=Utc; lift to a
                    // DateTimeOffset with offset 0 for the coordinator's API.
                    _motherlode.OnDistance(md.DistanceMetres, new DateTimeOffset(md.Timestamp, TimeSpan.Zero));
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
        // Feed the calibration window's verify mode unconditionally — it must
        // see the raw relative reading regardless of mode/state.
        _areaCalibration.NoteSurvey(sd.Name, sd.Offset);

        // #454: the relative-offset placement model is retired. Survey/
        // treasure placement is exclusively the absolute ProcessMapFx path
        // (PlayerLogIngestionService); the chat [Status] line no longer places
        // a pin. NoteSurvey above still drives the calibration verify view.
        // An uncalibrated area places nothing until calibration runs — pin
        // calibration is cold-start-capable, so that's always available.
        _session.LastLogEvent = _session.Mode == SessionMode.Survey
            ? $"Survey: {sd.Name} → placed via absolute ProcessMapFx (calibrate the area if no pin appears)"
            : $"Survey: {sd.Name} → ignored (mode is Motherlode)";
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
