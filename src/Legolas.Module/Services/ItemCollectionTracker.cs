using System.Windows;
using Legolas.Domain;
using Legolas.Flow;
using Legolas.ViewModels;
using Microsoft.Extensions.Hosting;
using Mithril.GameState.Inventory;
using Mithril.Shared.Diagnostics;
using Mithril.Shared.Logging;
using Mithril.Shared.Reference;
using Mithril.WorldSim;
using Mithril.WorldSim.Player;

namespace Legolas.Services;

/// <summary>
/// Replacement for the retired <c>LogIngestionService</c> chat tail (#606) plus
/// the post-#699 retreat from the cross-source <see cref="IInventoryView"/>
/// surface. Legolas's state-machine attribution is structurally a single-world
/// concern — both the Add side (Player.log <c>ProcessAddItem</c>) and the
/// Collect side (Player.log <c>ProcessScreenText(ImportantInfo, "&lt;Mineral&gt;
/// collected!")</c>) originate on Player.log — so the consumer takes the
/// principle 4 single-world-direct exit (<c>docs/world-simulator.md</c>):
///
/// <list type="bullet">
///   <item><b>Add channel</b> — <see cref="IPlayerWorld.Bus"/>
///     <see cref="PlayerInventoryAdded"/> (instance-id + InternalName, no
///     quantities). The folder fires one event per <c>ProcessAddItem</c>
///     observation. Resolved to a display-name via
///     <see cref="IReferenceDataService.ItemsByInternalName"/> and enqueued
///     under that key.</item>
///   <item><b>Collect channel</b> — Player.log
///     <c>ProcessScreenText(ImportantInfo, "&lt;Mineral&gt; collected!")</c>
///     (parsed by <see cref="PlayerLogParser"/>, post-#606). Dequeues the
///     head of the matching display-name FIFO and credits one to
///     <see cref="SessionState.CollectedItems"/>; misses go through the
///     credit-0-and-warn path. The optional <c>Also found &lt;Bonus&gt;
///     (speed bonus!)</c> tail credits the bonus item under the same
///     name-keyed FIFO.</item>
/// </list>
///
/// <para><b>FIFO policy (post-#699 retirement of the cross-source
/// <see cref="PendingCorrelator{TKey, TReq}"/>).</b> Keyed by display name
/// with <see cref="StringComparer.OrdinalIgnoreCase"/>; survey-session-bounded
/// (no TTL eviction). Both Add and Collect now originate on PlayerWorld, so
/// they share a single source-stream merger and arrive in the order PG
/// emitted them — the 5 s TTL the retired correlator used to guard
/// cross-stream race conditions has no race left to guard against (the brief
/// for #699 § "Design decisions ratified" enumerates the structural
/// guarantee). The lifecycle bound is <see cref="SurveyFlowController"/>:
/// on transition to <see cref="SurveyFlowState.Done"/> or on <c>Reset</c> the
/// queue clears, and any pending Add that never paired with a Collect surfaces
/// in the <c>Legolas.PendingAdds</c> Trace stream. New session, fresh queue.
/// Unmatched Collects (no pending Add for the name) still take the explicit
/// credit-0 + <c>diag.Warn</c> policy (no silent fallback); the warning frame
/// shifts from "TTL evicted" to "no pending Add for collected name".</para>
///
/// <para><b>Credit semantics shift.</b> Pre-#699, the correlator stored the
/// stack-size paired by the view layer's cross-source join (Player.log
/// <c>ProcessAddItem</c> ↔ chat <c>[Status] X xN added to inventory</c>), so
/// <see cref="SessionState.CollectedItems"/>[<i>name</i>] aggregated total
/// stack units. Post-#699 the Add channel carries no quantity (per
/// <see cref="PlayerInventoryAdded"/>'s contract — Player.log has no
/// stack-size for that verb), so the credit becomes "+1 per matched
/// (Add, Collect) pair." For surveys whose individual yields are
/// already 1 instance per pair (the common case — one node, one Collect
/// banner) the share-card display is unchanged. Surveys whose yields stack
/// across multiple Adds for the same name see one share-card line per
/// banner instead of summed stack units. The brief for #699 accepts this
/// shift as the cost of the principle 4 single-world exit; the
/// per-stack-size aggregation path would have required either a ChatWorld
/// subscription (rejected per the issue body's "two single-world paths"
/// design framing) or a return to the cross-source view layer (the very
/// coupling #699 retires).</para>
///
/// <para><b>Survey-row flip is independent of credit.</b> The survey row's
/// <see cref="SurveyItemViewModel.Collected"/> flag is set via the name-match
/// loop in <see cref="HandleItemCollected"/> regardless of whether a pending
/// Add was found — so "did I collect it" stays correct even when the count
/// credit short-circuits to credit-0.</para>
///
/// <para><b>Why a separate service.</b> Sibling to
/// <see cref="PlayerLogIngestionService"/> (which owns the
/// area→calibration bridge + absolute <c>ProcessMapFx</c> placement +
/// Motherlode coordinator wiring) to keep the attribution + collect-credit
/// concern isolated. Both subscribe eagerly through the L1 driver during
/// <c>StartAsync</c> (#695 Call 1); the legolas module gate no longer
/// gates state subscription.</para>
/// </summary>
public sealed class ItemCollectionTracker : BackgroundService
{
    private readonly IPlayerWorld _playerWorld;
    private readonly ILogStreamDriver _driver;
    private readonly PlayerLogParser _parser;
    private readonly SessionState _session;
    private readonly SurveyFlowController _flow;
    private readonly IReferenceDataService? _refData;
    private readonly IDiagnosticsSink? _diag;
    private readonly ThrottledWarn _warn;

    // Survey-session-bounded FIFO. Keyed by display name (resolved at enqueue
    // via the reference catalog so the survey-row name-match loop in
    // HandleItemCollected stays aligned with the CollectedItems dictionary
    // key). Value is the instance-id from PlayerInventoryAdded; the
    // state-machine attribution path consumes the *fact* of a pending Add,
    // not its identity — the instance-id is kept for future per-instance
    // attribution callers (e.g. Motherlode) and for diagnostic Trace clarity.
    private readonly Dictionary<string, Queue<long>> _pendingAdds
        = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _pendingLock = new();

    private IDisposable? _playerAddedSub;
    private ILogSubscription? _logSub;

    public ItemCollectionTracker(
        IPlayerWorld playerWorld,
        ILogStreamDriver driver,
        PlayerLogParser parser,
        SessionState session,
        SurveyFlowController flow,
        IReferenceDataService? refData = null,
        IDiagnosticsSink? diag = null,
        TimeProvider? time = null)
    {
        _playerWorld = playerWorld;
        _driver = driver;
        _parser = parser;
        _session = session;
        _flow = flow;
        _refData = refData;
        _diag = diag;
        // ThrottledWarn's TimeProvider migrates off TimeProvider.System onto a
        // PlayerWorld.Clock-backed adapter (#699). The throttle gate's
        // eviction trajectory now advances by simulated event time — same
        // determinism property the cross-source correlator retirement
        // delivers, applied to the warn-rate gate that shared its clock
        // surface. A test override stays available via the `time` ctor arg
        // (the LSP test seam pre-#699).
        var clock = time ?? new PlayerWorldClockTimeProvider(_playerWorld.Clock);
        _warn = new ThrottledWarn(diag, "Legolas.Ingestion", time: clock);

        _flow.Transitioned += OnFlowTransitioned;
    }

    /// <summary>
    /// Eager subscription attach per Call 1 / principle eager-always (#695):
    /// the <see cref="IPlayerWorld.Bus"/> subscription AND the L1
    /// ProcessScreenText subscription wire up during <c>StartAsync</c>,
    /// before the trailing world-merger drain starts (#702 / Call 2). The
    /// legolas module gate no longer gates state subscription; #699 then
    /// further retired the cross-source <see cref="IInventoryView"/>
    /// dependency the Add side previously read through.
    /// </summary>
    public override Task StartAsync(CancellationToken cancellationToken)
    {
        // PlayerWorld bus — typed change-event consumption per #699. Same
        // event the view layer was composing for us (then publishing as
        // InventoryItemAdded after fusing with chat); we now consume the
        // raw PlayerWorld-emitted change event directly, skipping the
        // view's cross-source join because the state machine has no
        // cross-world concern (the Collect side below is also Player.log).
        _playerAddedSub ??= _playerWorld.Bus.Subscribe<PlayerInventoryAdded>(OnPlayerInventoryAdded);

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
        // Bus sub is owned across the StartAsync/StopAsync lifecycle (attaches
        // before the pump and survives the pump's exit), so dispose it here
        // rather than in ExecuteAsync's finally — symmetric with where it was
        // attached.
        _playerAddedSub?.Dispose();
        _playerAddedSub = null;
        _flow.Transitioned -= OnFlowTransitioned;
        return base.StopAsync(cancellationToken);
    }

    public override void Dispose()
    {
        _playerAddedSub?.Dispose();
        _playerAddedSub = null;
        _logSub?.Dispose();
        _logSub = null;
        _flow.Transitioned -= OnFlowTransitioned;
        base.Dispose();
    }

    private void OnPlayerInventoryAdded(Frame<PlayerInventoryAdded> frame)
    {
        var displayName = ResolveDisplayName(frame.Payload.InternalName);
        if (string.IsNullOrEmpty(displayName)) return;
        lock (_pendingLock)
        {
            if (!_pendingAdds.TryGetValue(displayName, out var q))
                _pendingAdds[displayName] = q = new Queue<long>();
            q.Enqueue(frame.Payload.InstanceId);
        }
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
        // to credit. The post-#699 FIFO pops one matching pending Add per
        // Collect (vs the pre-#699 SumPendingFor that drained all); misses go
        // through the explicit credit-0 path below rather than the pre-#523
        // silent credit-1 fallback. Unmatched pending Adds that survive past
        // survey-session-end are surfaced via OnFlowTransitioned's Trace.
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
    /// Credit a <c>collected!</c> against a pending Add for the same display
    /// name. If one is found, accumulates a single instance into
    /// <see cref="SessionState.CollectedItems"/> (see the class-level remark
    /// on the post-#699 +1-per-pair semantic). If none is found, the credit-0
    /// policy applies: warn and <em>skip the accumulate</em> entirely — the
    /// dict stays untouched so the share card omits a "x0" line and any prior
    /// partial credit for this name isn't disturbed.
    /// </summary>
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
        // Survey-session lifecycle bound (#699). The pre-#699 cross-source
        // correlator used a 5 s TTL keyed on view-clock to guard against
        // unrelated noise accumulating between an Add and a Collect; under
        // the post-#699 PlayerWorld-direct subscription that TTL is gone, so
        // the survey FSM's transitions own the lifecycle:
        //
        //   • Listening / Gathering / SettingPosition → Done: the survey
        //     completed; any pending Add that never paired with a Collect
        //     wasn't survey loot and must not carry into the next run.
        //   • Reset trigger (from Listening/Gathering back to Listening, or
        //     Done → Listening via auto-reset): an explicit "start over"
        //     signal; same disposal semantic.
        //
        // Items still pending at clear-time surface in the
        // Legolas.PendingAdds Trace stream (mirrors the pre-#699
        // "evicted {name} x{count}" diagnostic, narrowed to the
        // survey-session frame instead of a wall-clock TTL).
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

    /// <summary>
    /// Thin <see cref="TimeProvider"/> shim around an <see cref="IWorldClock"/>
    /// so primitives that accept a <c>TimeProvider</c> (the canonical
    /// abstraction in the .NET BCL — and the contract <see cref="ThrottledWarn"/>
    /// already accepts) read their "now" from PlayerWorld's simulated
    /// frame-driven clock. The blast radius is private-nested: only the
    /// throttle gate in this file consumes it; the broader codebase keeps the
    /// existing <see cref="TimeProvider"/> seam without introducing an
    /// <c>IWorldClock</c>-flavoured callback variant. <see cref="ViewClock"/>
    /// uses the same shape over the view layer's derived clock.
    /// </summary>
    private sealed class PlayerWorldClockTimeProvider(IWorldClock clock) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => clock.Now;
    }
}
