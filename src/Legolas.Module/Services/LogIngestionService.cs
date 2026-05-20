using System.Windows;
using Mithril.Shared.Correlation;
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
    private readonly IDiagnosticsSink? _diag;
    private readonly ThrottledWarn _warn;

    public LogIngestionService(
        IChatLogStream stream,
        IChatLogParser parser,
        ModuleGates gates,
        SessionState session,
        MotherlodeMeasurementCoordinator motherlode,
        IAreaCalibrationService areaCalibration,
        IDiagnosticsSink? diag = null,
        TimeProvider? time = null)
    {
        _stream = stream;
        _parser = parser;
        _gates = gates;
        _session = session;
        _motherlode = motherlode;
        _areaCalibration = areaCalibration;
        _diag = diag;
        _warn = new ThrottledWarn(diag, "Legolas.Ingestion");
        _pendingAdds = new PendingCorrelator<string, int>(
            PendingAddTtl,
            time: time ?? TimeProvider.System,
            onUnmatched: (name, count) =>
                _diag?.Trace("Legolas.PendingAdds", $"evicted {name} x{count}"),
            keyComparer: StringComparer.OrdinalIgnoreCase);
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
    /// Pending "[Status] X xN added to inventory." adds awaiting a matching
    /// "[Status] X collected!" line. PG's observed emission is ADD-then-COLLECT,
    /// emitted within ~1s, so a tight TTL window captures the real pair and
    /// naturally expires unrelated inventory noise (skinning, vendor, crafting).
    /// Keyed by display name; case-insensitive to match the consumer's lookup
    /// in <see cref="HandleItemCollected"/>. Migrated from a hand-rolled buffer
    /// + cap + "credit at least 1" fallback to the shared
    /// <see cref="PendingCorrelator{TKey, TReq}"/> primitive (#523 deliverable 2);
    /// the credit-1 guess is replaced with an explicit credit-0 + diag.Warn
    /// policy on the take side, and TTL-evicted noise is surfaced via the
    /// eviction Trace callback wired in the ctor.
    /// </summary>
    private static readonly TimeSpan PendingAddTtl = TimeSpan.FromSeconds(5);
    private readonly PendingCorrelator<string, int> _pendingAdds;

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
        // Assumes ia.Count > 0. The chat parser's regex (x\d+) tolerates "x0"
        // but PG has never been observed emitting it; a zero-count enqueue
        // would correlate but contribute nothing on take, which is harmless.
        _pendingAdds.Add(ia.Name, ia.Count);
    }

    private void HandleItemCollected(ItemCollected ic)
    {
        // Primary item + (optional) speed-bonus item are the only ones we want
        // to credit. SumPendingFor pops every matching pending ADD (FIFO);
        // misses go through the explicit credit-0 path below rather than the
        // pre-#523 silent credit-1 fallback. TTL-evicted noise (unrelated
        // skinning/vendor adds for the same name) is surfaced via the ctor's
        // eviction Trace callback.
        CreditCollect(ic.Name);
        if (!string.IsNullOrEmpty(ic.SpeedBonusItem))
            CreditCollect(ic.SpeedBonusItem!);

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

    /// <summary>
    /// Credit a <c>[Status] X collected!</c> against pending ADDs for the same
    /// name. If at least one pending ADD is found, accumulates the summed count
    /// into <see cref="SessionState.CollectedItems"/>. If no pending ADD is
    /// found, the credit-0 policy applies: warn and <em>skip the accumulate</em>
    /// entirely — the dict stays untouched so the share card omits a "x0" line
    /// and any prior partial credit for this name isn't disturbed. The survey
    /// row's <see cref="SurveyItemViewModel.Collected"/> flag is independent of
    /// this path (set by the name-match loop in <see cref="HandleItemCollected"/>),
    /// so "did I collect it" stays correct regardless of count.
    /// </summary>
    private void CreditCollect(string name)
    {
        var (total, hadAny) = SumPendingFor(name);
        if (hadAny)
        {
            AccumulateCollected(name, total);
            return;
        }
        _warn.Warn(
            $"Collect for '{name}' had no pending '[Status] added to inventory' " +
            $"within {PendingAddTtl.TotalSeconds:0}s; crediting 0.");
    }

    private (int Total, bool HadAny) SumPendingFor(string name)
    {
        var total = 0;
        var hadAny = false;
        while (_pendingAdds.TryTake(name, out var count))
        {
            total += count;
            hadAny = true;
        }
        return (total, hadAny);
    }

    private void AccumulateCollected(string name, int count)
    {
        _session.CollectedItems.TryGetValue(name, out var existing);
        _session.CollectedItems[name] = existing + count;
    }
}
