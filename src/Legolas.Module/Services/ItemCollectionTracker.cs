using System.Windows;
using Arda.Contracts;
using Arda.World.Player.Events;
using Legolas.Domain;
using Legolas.Flow;
using Legolas.ViewModels;
using Microsoft.Extensions.Hosting;
using Mithril.Shared.Diagnostics;
using Mithril.Shared.Reference;

namespace Legolas.Services;

/// <summary>
/// Arda-driven inventory-add ↔ Player.log-collect attribution. Subscribes to
/// <see cref="InventoryItemAdded"/> (replaces former <c>IPlayerWorld.Bus</c>
/// <c>PlayerInventoryAdded</c> subscription) and <see cref="ScreenTextObserved"/>
/// (replaces the L1 driver <c>ProcessScreenText</c> parsing) via
/// <see cref="IDomainEventSubscriber"/>.
///
/// <list type="bullet">
///   <item><b>Add channel</b> — <see cref="InventoryItemAdded"/> (instance-id +
///   InternalName). Resolved to a display-name via
///   <see cref="IReferenceDataService.ItemsByInternalName"/> and enqueued under
///   that key.</item>
///   <item><b>Collect channel</b> — <see cref="ScreenTextObserved"/> with
///   category <c>ImportantInfo</c> matching the "<c>&lt;Mineral&gt; collected!</c>"
///   pattern (parsed by <see cref="PlayerLogParser.TryParseItemCollected"/>).
///   Dequeues the head of the matching display-name FIFO and credits one to
///   <see cref="SessionState.CollectedItems"/>.</item>
/// </list>
///
/// <para><b>Replay gating.</b> Both channels check
/// <see cref="Arda.Abstractions.Logs.LogLineMetadata.IsReplay"/> — replay events
/// are dropped. Replaces the former <c>LiveOnly</c> replay mode.</para>
///
/// <para><b>Threading.</b> The Arda bus fires synchronously on the driver
/// thread. Handlers marshal to the UI thread via the WPF dispatcher so
/// overlay-bound state mutations stay single-threaded.</para>
/// </summary>
public sealed class ItemCollectionTracker : BackgroundService
{
    private readonly IDomainEventSubscriber _bus;
    private readonly SessionState _session;
    private readonly SurveyFlowController _flow;
    private readonly IReferenceDataService? _refData;
    private readonly IDiagnosticsSink? _diag;
    private readonly ThrottledWarn _warn;

    private readonly Dictionary<string, Queue<long>> _pendingAdds
        = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _pendingLock = new();

    private IDisposable? _invAddedSub;
    private IDisposable? _screenTextSub;

    public ItemCollectionTracker(
        IDomainEventSubscriber bus,
        SessionState session,
        SurveyFlowController flow,
        IReferenceDataService? refData = null,
        IDiagnosticsSink? diag = null,
        TimeProvider? time = null)
    {
        _bus = bus;
        _session = session;
        _flow = flow;
        _refData = refData;
        _diag = diag;
        _warn = new ThrottledWarn(diag, "Legolas.Ingestion", time: time ?? TimeProvider.System);

        _flow.Transitioned += OnFlowTransitioned;
    }

    public override Task StartAsync(CancellationToken cancellationToken)
    {
        _invAddedSub ??= _bus.Subscribe<InventoryItemAdded>(OnInventoryItemAdded);
        _screenTextSub ??= _bus.Subscribe<ScreenTextObserved>(OnScreenTextObserved);

        return base.StartAsync(cancellationToken);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try { await Task.Delay(Timeout.Infinite, stoppingToken).ConfigureAwait(false); }
        catch (OperationCanceledException) { /* expected on host stop */ }
    }

    public override Task StopAsync(CancellationToken cancellationToken)
    {
        _invAddedSub?.Dispose();
        _invAddedSub = null;
        _screenTextSub?.Dispose();
        _screenTextSub = null;
        _flow.Transitioned -= OnFlowTransitioned;
        return base.StopAsync(cancellationToken);
    }

    public override void Dispose()
    {
        _invAddedSub?.Dispose();
        _invAddedSub = null;
        _screenTextSub?.Dispose();
        _screenTextSub = null;
        _flow.Transitioned -= OnFlowTransitioned;
        base.Dispose();
    }

    private void OnInventoryItemAdded(InventoryItemAdded evt)
    {
        if (evt.Metadata.IsReplay) return;

        var displayName = ResolveDisplayName(evt.InternalName);
        if (string.IsNullOrEmpty(displayName)) return;
        lock (_pendingLock)
        {
            if (!_pendingAdds.TryGetValue(displayName, out var q))
                _pendingAdds[displayName] = q = new Queue<long>();
            q.Enqueue(evt.InstanceId);
        }
    }

    private void OnScreenTextObserved(ScreenTextObserved evt)
    {
        if (evt.Metadata.IsReplay) return;
        if (!evt.Category.Span.SequenceEqual("ImportantInfo".AsSpan())) return;

        var text = evt.Text.ToString();
        if (PlayerLogParser.TryParseItemCollected(text) is not var (name, bonus)) return;

        MarshalToUi(() => HandleItemCollected(name, bonus));
    }

    private string? ResolveDisplayName(string internalName)
    {
        if (string.IsNullOrEmpty(internalName)) return null;
        if (_refData is null) return internalName;
        if (_refData.ItemsByInternalName.TryGetValue(internalName, out var item)
            && !string.IsNullOrEmpty(item.Name))
        {
            return item.Name;
        }
        return internalName;
    }

    private void HandleItemCollected(string name, string? speedBonusItem)
    {
        CreditCollect(name);
        if (!string.IsNullOrEmpty(speedBonusItem))
            CreditCollect(speedBonusItem!);

        SurveyItemViewModel? best = null;
        var bestOrder = int.MaxValue;
        foreach (var s in _session.Surveys)
        {
            if (s.Collected) continue;
            if (!string.Equals(s.Name, name, StringComparison.OrdinalIgnoreCase)) continue;
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
            _session.LastLogEvent = $"Collected: {name} → marked";
            return;
        }

        _session.LastLogEvent = _session.Surveys.Count == 0
            ? $"Collected: {name} → no surveys tracked"
            : $"Collected: {name} → no name match (have {string.Join(", ", _session.Surveys.Where(s => !s.Collected).Select(s => s.Name).Take(3))})";
    }

    private void CreditCollect(string name)
    {
        bool hadAny;
        lock (_pendingLock)
        {
            hadAny = _pendingAdds.TryGetValue(name, out var q) && q.Count > 0;
            if (hadAny)
            {
                _pendingAdds[name].Dequeue();
                if (_pendingAdds[name].Count == 0)
                    _pendingAdds.Remove(name);
            }
        }

        if (hadAny)
        {
            AccumulateCollected(name, 1);
            return;
        }
        _warn.Warn(
            $"Collect for '{name}' had no pending inventory add; crediting 0.");
    }

    private void AccumulateCollected(string name, int count)
    {
        _session.CollectedItems.TryGetValue(name, out var existing);
        _session.CollectedItems[name] = existing + count;
    }

    private void OnFlowTransitioned(SurveyTransition t)
    {
        var shouldClear = t.To == SurveyFlowState.Done
            || string.Equals(t.Trigger, nameof(SurveyFlowController.Reset), StringComparison.Ordinal);
        if (!shouldClear) return;

        Dictionary<string, int>? unmatched = null;
        lock (_pendingLock)
        {
            if (_pendingAdds.Count > 0)
            {
                unmatched = new Dictionary<string, int>(_pendingAdds.Count, StringComparer.OrdinalIgnoreCase);
                foreach (var (name, q) in _pendingAdds) unmatched[name] = q.Count;
                _pendingAdds.Clear();
            }
        }
        if (unmatched is { Count: > 0 })
        {
            var summary = string.Join(", ",
                unmatched.OrderBy(kv => kv.Key, StringComparer.OrdinalIgnoreCase)
                    .Select(kv => $"{kv.Key} x{kv.Value}"));
            _diag?.Trace("Legolas.PendingAdds",
                $"survey ended ({t.Trigger}); unmatched pending Adds dropped: {summary}");
        }
    }

    private static void MarshalToUi(Action action)
    {
        if (Application.Current?.Dispatcher is { } d && !d.CheckAccess())
            d.Invoke(action);
        else
            action();
    }
}
