using System.Windows;
using Arda.Contracts;
using Arda.World.Player.Events;
using Legolas.Domain;
using Legolas.ViewModels;
using Microsoft.Extensions.Hosting;

namespace Legolas.Services;

/// <summary>
/// Survey-mode item-attribution tracker. Treats <see cref="ScreenTextObserved"/>
/// "<c>&lt;item&gt; collected!</c>" lines as the sole source of truth for both
/// primary drops and speed-bonus drops — the chat text carries the explicit
/// <c>xN</c> count and fires for both fresh-instance and stack-onto-existing
/// inventory mutations, while <c>InventoryItemAdded</c> only fires on the
/// fresh-instance path and always carries <c>StackSize = 1</c>. See #824 for
/// the corpus evidence behind this pivot (Player-2026-05-20-0400.log) and
/// #543 / #809 for the prior attribution attempts this replaces.
///
/// <para><b>Replay gating.</b> <see cref="Arda.Abstractions.Logs.LogLineMetadata.IsReplay"/>
/// events are dropped — survey state is live-session only.</para>
///
/// <para><b>Threading.</b> The Arda bus fires synchronously on the driver
/// thread. The handler marshals to the UI thread via the WPF dispatcher so
/// overlay-bound state mutations stay single-threaded.</para>
/// </summary>
public sealed class ItemCollectionTracker : BackgroundService
{
    private readonly IDomainEventSubscriber _bus;
    private readonly SessionState _session;

    private IDisposable? _screenTextSub;

    public ItemCollectionTracker(IDomainEventSubscriber bus, SessionState session)
    {
        _bus = bus;
        _session = session;
    }

    public override Task StartAsync(CancellationToken cancellationToken)
    {
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
        _screenTextSub?.Dispose();
        _screenTextSub = null;
        return base.StopAsync(cancellationToken);
    }

    public override void Dispose()
    {
        _screenTextSub?.Dispose();
        _screenTextSub = null;
        base.Dispose();
    }

    private void OnScreenTextObserved(ScreenTextObserved evt)
    {
        if (evt.Metadata.IsReplay) return;
        if (!evt.Category.Span.SequenceEqual("ImportantInfo".AsSpan())) return;

        if (PlayerLogParser.TryParseItemCollected(evt.Text.Span) is not var (name, count, bonusName, bonusCount))
            return;

        MarshalToUi(() => HandleItemCollected(name, count, bonusName, bonusCount));
    }

    private void HandleItemCollected(string name, int count, string? bonusName, int bonusCount)
    {
        AccumulateCollected(name, count);
        if (!string.IsNullOrEmpty(bonusName))
            AccumulateCollected(bonusName, bonusCount);

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

    private void AccumulateCollected(string name, int count)
    {
        _session.CollectedItems.TryGetValue(name, out var existing);
        _session.CollectedItems[name] = existing + count;
    }

    private static void MarshalToUi(Action action)
    {
        if (Application.Current?.Dispatcher is { } d && !d.CheckAccess())
            d.Invoke(action);
        else
            action();
    }
}
