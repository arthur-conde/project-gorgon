using System.Windows;
using Mithril.GameState.Inventory;
using Mithril.Shared.Correlation;
using Mithril.Shared.Diagnostics;
using Mithril.Shared.Logging;
using Mithril.Shared.Modules;
using Mithril.Shared.Reference;
using Mithril.WorldSim;
using Legolas.Domain;
using Legolas.ViewModels;
using Microsoft.Extensions.Hosting;

namespace Legolas.Services;

/// <summary>
/// Replacement for the retired <c>LogIngestionService</c> chat tail (#606).
/// Owns the ADD↔COLLECT correlation that credits survey yields to
/// <see cref="SessionState.CollectedItems"/>:
/// <list type="bullet">
///   <item><b>Add channel</b> — <see cref="IInventoryView.Bus"/>
///     <see cref="InventoryItemAdded"/> with <c>SizeConfirmed = true</c>. The
///     view layer composes Player.log <c>ProcessAddItem</c> with the chat-side
///     <c>ChatInventoryObserved</c> stack-size signal (#602), so this event is
///     the post-migration equivalent of the retired chat
///     <c>[Status] X xN added to inventory.</c> line — same display name (resolved
///     via <see cref="IReferenceDataService.ItemsByInternalName"/>) and the same
///     authoritative count. Unconfirmed Adds (no chat correlation yet) are
///     skipped; the matching <see cref="InventoryStackChanged"/> back-fill that
///     follows within the view's TTL carries the confirmed size.</item>
///   <item><b>Collect channel</b> — Player.log
///     <c>ProcessScreenText(ImportantInfo, "&lt;Mineral&gt; collected!")</c>
///     (parsed by <see cref="PlayerLogParser"/>, post-#606). Drains every
///     pending ADD bucket for the same display name (FIFO) and credits the
///     summed count. The optional <c>Also found &lt;Bonus&gt; (speed bonus!)</c>
///     tail credits the bonus item under the same name-keyed correlator.</item>
/// </list>
///
/// <para><b>Correlator policy (preserved from the retired chat path).</b>
/// Keyed by display name with <see cref="StringComparer.OrdinalIgnoreCase"/>;
/// 5 s TTL (matching <c>InventoryView.PendingChatTtl</c> deliberately —
/// PG's emission ordering is ADD-then-COLLECT within ~1 s, so a tight window
/// captures the real pair and naturally expires unrelated inventory noise
/// like skinning/vendor/crafting adds for the same display name).
/// Unmatched takes apply the explicit credit-0 + <c>diag.Warn</c> policy
/// (no silent fallback); TTL-evicted noise is surfaced via the eviction
/// <c>Trace</c> callback. See <c>docs/cross-source-correlation.md</c>
/// §Tier 1 for the operational invariants.</para>
///
/// <para><b>Survey-row flip is independent of count.</b> The survey row's
/// <see cref="SurveyItemViewModel.Collected"/> flag is set via the name-match
/// loop in <see cref="HandleItemCollected"/> regardless of whether a pending
/// ADD was found — so "did I collect it" stays correct even when the count
/// credit short-circuits to credit-0.</para>
///
/// <para><b>Why a separate service.</b> Sibling to
/// <see cref="PlayerLogIngestionService"/> (which owns the
/// area→calibration bridge + absolute <c>ProcessMapFx</c> placement +
/// Motherlode coordinator wiring) to keep the correlator + collect-credit
/// concern isolated. Both subscribe through the L1 driver and gate on the
/// <c>"legolas"</c> module gate.</para>
/// </summary>
public sealed class ItemCollectionTracker : BackgroundService
{
    private static readonly TimeSpan PendingAddTtl = TimeSpan.FromSeconds(5);

    private readonly IInventoryView _inventoryView;
    private readonly ILogStreamDriver _driver;
    private readonly PlayerLogParser _parser;
    private readonly ModuleGates _gates;
    private readonly SessionState _session;
    private readonly IReferenceDataService? _refData;
    private readonly IDiagnosticsSink? _diag;
    private readonly ThrottledWarn _warn;
    private readonly PendingCorrelator<string, int> _pendingAdds;

    private IDisposable? _addedSub;
    private IDisposable? _stackChangedSub;
    private ILogSubscription? _logSub;

    public ItemCollectionTracker(
        IInventoryView inventoryView,
        ILogStreamDriver driver,
        PlayerLogParser parser,
        ModuleGates gates,
        SessionState session,
        IReferenceDataService? refData = null,
        IDiagnosticsSink? diag = null,
        TimeProvider? time = null)
    {
        _inventoryView = inventoryView;
        _driver = driver;
        _parser = parser;
        _gates = gates;
        _session = session;
        _refData = refData;
        _diag = diag;
        var clock = time ?? TimeProvider.System;
        _warn = new ThrottledWarn(diag, "Legolas.Ingestion", time: clock);
        _pendingAdds = new PendingCorrelator<string, int>(
            PendingAddTtl,
            time: clock,
            onUnmatched: (name, count) =>
                _diag?.Trace("Legolas.PendingAdds", $"evicted {name} x{count}"),
            keyComparer: StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Eager subscription attach per Call 1 / principle eager-always (#695):
    /// both the <see cref="IInventoryView.Bus"/> subscriptions AND the L1
    /// ProcessScreenText subscription wire up during <c>StartAsync</c>,
    /// before the trailing world-merger drain starts (#702 / Call 2). The
    /// legolas module gate no longer gates state subscription — pre-Call-1
    /// the L1 subscribe sat inside <see cref="ExecuteAsync"/> behind
    /// <c>gates.For("legolas").WaitAsync</c>, which on a lazy module
    /// delayed the collect-credit path until first-tab activation. Now the
    /// L1 subscribe joins the bus subscribes here so a session-start replay
    /// drain reaches HandleItemCollected from the moment the host's chain
    /// completes (#702 invariant: trailing-merger starts after every
    /// other hosted service's <c>StartAsync</c> returns).
    ///
    /// <para><see cref="ViewEventBus.Subscribe"/> still has no
    /// replay-on-subscribe contract; the attach-before-merger contract
    /// from #702 is what guarantees no frames are missed.</para>
    /// </summary>
    public override Task StartAsync(CancellationToken cancellationToken)
    {
        // Inventory view — typed bus consumption per #606. Both Added (when
        // SizeConfirmed) and StackChanged represent the post-#602 equivalents
        // of the retired chat "[Status] X xN added to inventory." line. The
        // view's own correlator already pairs the Player.log + chat sides; we
        // consume the resolved view event downstream of that composition.
        _addedSub ??= _inventoryView.Bus.Subscribe<InventoryItemAdded>(OnInventoryAdded);
        _stackChangedSub ??= _inventoryView.Bus.Subscribe<InventoryStackChanged>(OnInventoryStackChanged);

        // Player.log collect channel — parses ProcessScreenText
        // (ImportantInfo, "<Mineral> collected!"). Mirrors the structural
        // shape PlayerLogIngestionService uses (LiveOnly + Marshaled +
        // Legolas.PlayerLog diagnostic category) so the two services share
        // the same delivery semantics. The collect handler mutates
        // SessionState (UI-bound surveys + LastLogEvent); UI VMs hydrate
        // lazily from the SessionState snapshot when the legolas tab is
        // first activated, so the eager subscribe here doesn't depend on
        // any prior UI construction.
        var dispatcher = Application.Current?.Dispatcher;
        _logSub = _driver.Subscribe<LocalPlayerLogLine>(
            envelope =>
            {
                var payload = envelope.Payload;
                var ts = payload.Timestamp.UtcDateTime;
                var line = payload.Data;
                if (!line.Contains("ProcessScreenText", StringComparison.Ordinal))
                    return ValueTask.CompletedTask;
                if (_parser.TryParse(line, ts) is ItemCollected ic)
                    HandleItemCollected(ic);
                return ValueTask.CompletedTask;
            },
            new LogSubscriptionOptions
            {
                ReplayMode = ReplayMode.LiveOnly,
                DeliveryContext = dispatcher is null
                    ? DeliveryContext.Inline
                    : DeliveryContext.Marshaled(dispatcher),
                DiagnosticCategory = "Legolas.ItemCollect",
            });

        return base.StartAsync(cancellationToken);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try { await Task.Delay(Timeout.Infinite, stoppingToken).ConfigureAwait(false); }
        catch (OperationCanceledException) { /* expected on host stop */ }
        finally
        {
            _logSub?.Dispose();
            _logSub = null;
        }
    }

    public override Task StopAsync(CancellationToken cancellationToken)
    {
        // Bus subs are owned across the StartAsync/StopAsync lifecycle (they
        // attach before the pump and survive the pump's exit), so dispose them
        // here rather than in ExecuteAsync's finally — symmetric with where
        // they were attached.
        _addedSub?.Dispose();
        _addedSub = null;
        _stackChangedSub?.Dispose();
        _stackChangedSub = null;
        return base.StopAsync(cancellationToken);
    }

    public override void Dispose()
    {
        _addedSub?.Dispose();
        _addedSub = null;
        _stackChangedSub?.Dispose();
        _stackChangedSub = null;
        _logSub?.Dispose();
        _logSub = null;
        base.Dispose();
    }

    private void OnInventoryAdded(Frame<InventoryItemAdded> frame)
    {
        // Only confirmed adds carry an authoritative count. Unconfirmed adds
        // default to 1 with SizeConfirmed=false — the matching StackChanged
        // back-fill that the view emits within its TTL window will carry the
        // real size, which OnInventoryStackChanged below handles.
        if (!frame.Payload.SizeConfirmed) return;
        EnqueueAdd(frame.Payload.InternalName, frame.Payload.StackSize);
    }

    private void OnInventoryStackChanged(Frame<InventoryStackChanged> frame)
    {
        // StackChanged fires on:
        //   • chat back-fill of a previously-defaulted InventoryItemAdded
        //     (legitimate survey-credit signal — the chat "added" line is
        //     what the legacy chat path was correlating on);
        //   • ProcessUpdateItemCode / ProcessRemoveFromStorageVault stack
        //     bumps on existing inventory.
        // Both correspond to chat "[Status] X xN added to inventory." emissions
        // pre-#606, so both legitimately credit. Drain & TTL handle the rest.
        EnqueueAdd(frame.Payload.InternalName, frame.Payload.StackSize);
    }

    /// <summary>
    /// Resolve <c>InternalName → DisplayName</c> via the reference catalog and
    /// enqueue under the display-name key (correlator key shape preserved from
    /// the retired chat path so the survey-row name-match loop in
    /// <see cref="HandleItemCollected"/> stays aligned with the
    /// <see cref="SessionState.CollectedItems"/> dictionary key).
    /// </summary>
    private void EnqueueAdd(string internalName, int stackSize)
    {
        if (stackSize <= 0) return;
        var displayName = ResolveDisplayName(internalName);
        if (string.IsNullOrEmpty(displayName)) return;
        DrainPendingStale();
        _pendingAdds.Add(displayName, stackSize);
    }

    private string? ResolveDisplayName(string internalName)
    {
        if (string.IsNullOrEmpty(internalName)) return null;
        if (_refData is null) return internalName; // best-effort fallback
        if (_refData.ItemsByInternalName.TryGetValue(internalName, out var item)
            && !string.IsNullOrEmpty(item.Name))
        {
            return item.Name;
        }
        // Unknown items (e.g. reference data mid-refresh or new patch) get the
        // InternalName as a best-effort display key. Subsequent chat-collected
        // lookups under the same InternalName-as-display will still pair.
        return internalName;
    }

    private void HandleItemCollected(ItemCollected ic)
    {
        // Primary item + (optional) speed-bonus item are the only ones we want
        // to credit. SumPendingFor pops every matching pending ADD (FIFO);
        // misses go through the explicit credit-0 path below rather than the
        // pre-#523 silent credit-1 fallback. TTL-evicted noise (unrelated
        // skinning/vendor/crafting adds for the same name) is surfaced via the
        // ctor's eviction Trace callback.
        DrainPendingStale();
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
    /// Credit a <c>collected!</c> against pending ADDs for the same display
    /// name. If at least one pending ADD is found, accumulates the summed
    /// count into <see cref="SessionState.CollectedItems"/>. If no pending
    /// ADD is found, the credit-0 policy applies: warn and <em>skip the
    /// accumulate</em> entirely — the dict stays untouched so the share card
    /// omits a "x0" line and any prior partial credit for this name isn't
    /// disturbed.
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
            $"Collect for '{name}' had no pending inventory add " +
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

    /// <summary>
    /// Piggyback eviction sweep across every pending key. Required because the
    /// only consumer of pending ADDs is per-key (<see cref="PendingCorrelator{TKey, TReq}.TryTake"/>
    /// lazy-evicts only the bucket it touches), so names that arrive on
    /// inventory adds and never see a matching <c>collected!</c> — skinning,
    /// vendor, crafting, non-survey loot — would accumulate for the process
    /// lifetime without this call. Invoked at the top of every handler that
    /// touches the correlator.
    /// </summary>
    private void DrainPendingStale() => _pendingAdds.DrainStale();
}
