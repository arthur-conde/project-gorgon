using System.Text.RegularExpressions;
using Mithril.Shared.Diagnostics;
using Mithril.Shared.Logging;
using Microsoft.Extensions.Hosting;

namespace Mithril.GameState.Gifting;

/// <summary>
/// Hosted-service implementation of <see cref="IGiftSignalService"/>. Owns a
/// single <see cref="LocalPlayerLogLine"/> subscription with
/// <see cref="ReplayMode.FromSessionStart"/> and feeds every verb the SM
/// consumes through it. Mirrors
/// <see cref="Mithril.GameState.Inventory.InventoryService"/>'s shape for
/// registration, L1 subscription, internal event log, and replay-on-Subscribe.
///
/// <para><b>State machine (lifted verbatim from Arwen's
/// <c>CalibrationService</c>'s <c>_activeNpcKey</c> / <c>_pendingDeletedItem</c>
/// / <c>_pendingDelta</c> logic):</b></para>
/// <list type="bullet">
///   <item><c>ProcessStartInteraction(entityId, _, _, _, "NPC_&lt;key&gt;")</c>
///   → arm window: capture <c>_activeNpcKey</c>, <c>_activeEntityId</c>,
///   <c>_interactionStartedAt</c>; clear both pending tuples.</item>
///   <item><c>ProcessDeleteItem(instanceId)</c> → if window is closed,
///   ignore. Otherwise resolve <c>instanceId</c> to InternalName from
///   <see cref="_instanceMap"/> (Trace-and-skip on miss). If
///   <c>_pendingDelta</c> is set, EMIT with the pending DeltaFavor stamp;
///   otherwise stash <c>_pendingDeletedItem</c>.</item>
///   <item><c>ProcessDeltaFavor("NPC_&lt;key&gt;", delta)</c> → if
///   <c>delta &lt;= 0</c> or the NpcKey doesn't match
///   <c>_activeNpcKey</c>, ignore. If <c>_pendingDeletedItem</c> is set,
///   EMIT; otherwise stash <c>_pendingDelta</c>.</item>
///   <item><c>ProcessEndInteraction(entityId)</c> → if it matches
///   <c>_activeEntityId</c>, clear all transient state. (New vs the original
///   Arwen SM, which never explicitly cleared on EndInteraction; benign
///   defensive cleanup.)</item>
///   <item><c>ProcessAddItem(InternalName(instanceId), …)</c> → upsert
///   <c>_instanceMap[instanceId] = InternalName</c>. Never evict — matches
///   <see cref="Mithril.GameState.Inventory.InventoryService"/>'s retention
///   of deleted entries so a Delete-then-resolve lookup still succeeds.</item>
/// </list>
///
/// <para><b>Frame-determinism contract (per
/// <a href="https://github.com/moumantai-gg/mithril/issues/594">#594</a>).</b>
/// One L1 subscription, one pump; emission timestamps are L1 envelope
/// timestamps (not <see cref="DateTimeOffset.UtcNow"/>); identical L1 input
/// sequences produce identical <see cref="GiftAccepted"/> sequences regardless
/// of when subscribers attach; the React-channel event log is appended under
/// the same lock <see cref="Subscribe"/> takes for replay so the
/// replay-then-live boundary is race-free.</para>
///
/// <para><b>Known SM quirk (preserved verbatim).</b> A non-gift
/// <c>ProcessDeltaFavor</c> arriving inside an active interaction (e.g. a
/// quest-turnin favor reward landing right at talk-open, as captured at
/// 02:12:18 in the May-2026 NPC_Way capture) sets <c>_pendingDelta</c>; the
/// next <c>ProcessDeleteItem</c> claims it and misattributes the first gift's
/// delta. Verified intentional per
/// <a href="https://github.com/moumantai-gg/mithril/issues/596">#596</a>
/// <i>Out of scope → SM behavioural fixes</i>; the recorded-session fixture
/// asserts on this exact behaviour.</para>
/// </summary>
public sealed partial class GiftSignalService : BackgroundService, IGiftSignalService
{
    // ProcessStartInteraction(entityId, ?, absoluteFavor, bool, "NPC_Key")
    // Borrowed shape from Arwen/Parsing/FavorLogParser.cs:24 — we tighten the
    // capture to (entityId, npcKey) since the favor + bool args aren't load-
    // bearing here.
    [GeneratedRegex(
        @"ProcessStartInteraction\((\d+),\s*[\d.\-]+,\s*[\d.\-]+,\s*\w+,\s*""(NPC_\w+)""\)",
        RegexOptions.CultureInvariant)]
    private static partial Regex StartInteractionRx();

    // ProcessEndInteraction(entityId)
    [GeneratedRegex(@"ProcessEndInteraction\((\d+)\)", RegexOptions.CultureInvariant)]
    private static partial Regex EndInteractionRx();

    // ProcessDeltaFavor(entityId, "NPC_Key", delta, bool)
    // Shape lifted from Arwen/Parsing/FavorLogParser.cs:28.
    [GeneratedRegex(
        @"ProcessDeltaFavor\(\d+,\s*""(NPC_\w+)"",\s*([\d.\-]+),\s*\w+\)",
        RegexOptions.CultureInvariant)]
    private static partial Regex DeltaFavorRx();

    // ProcessDeleteItem(instanceId). Shape lifted from
    // src/Mithril.GameState/Inventory/InventoryService.cs:64.
    [GeneratedRegex(@"ProcessDeleteItem\((\d+)\)", RegexOptions.CultureInvariant)]
    private static partial Regex DeleteItemRx();

    // ProcessAddItem(InternalName(instanceId), slot, bool). Shape lifted from
    // src/Mithril.GameState/Inventory/InventoryService.cs:60.
    [GeneratedRegex(@"ProcessAddItem\((\w+)\((\d+)\),", RegexOptions.CultureInvariant)]
    private static partial Regex AddItemRx();

    /// <summary>
    /// Soft cap on the internal event-log size. Gifts are rare in absolute
    /// terms (PG sessions emit a handful per active hour at most), so 10,000
    /// is generous (a couple orders of magnitude above expected). One-time
    /// <see cref="DiagnosticLevel.Warn"/> if exceeded — the cap protects the
    /// log from unbounded growth on a degenerate stream, not from normal use.
    /// Matches the shape <c>PlayerEffectsStateService</c> adopts in #590.
    /// </summary>
    internal const int EventLogSoftCap = 10_000;

    private readonly ILogStreamDriver _driver;
    private readonly IDiagnosticsSink? _diag;

    // _lock guards _instanceMap, _eventLog, _handlers, _activeNpcKey,
    // _activeEntityId, _interactionStartedAt, _pendingDeletedItem,
    // _pendingDelta. Subscribe takes it for replay-then-attach; the L1 pump
    // takes it around every verb-handler so the SM transitions, the
    // _eventLog append, and the live-handler dispatch are atomic with
    // respect to subscriber attach.
    private readonly object _lock = new();

    // Service-owned instanceId → InternalName map. Populated from
    // ProcessAddItem on the SAME L1 subscription as the rest of the SM —
    // explicitly NOT consulted from IInventoryView.TryResolve. This is
    // the load-bearing Tier-2 commitment per #596's "Own ProcessAddItem too"
    // section: routing the DeleteItem half through IInventoryView would
    // re-introduce the cross-pump race documented in #582. Map is
    // append-only (never evict) so a DeleteItem whose AddItem fired earlier
    // in the same session resolves cleanly, mirroring the view's
    // retention-on-delete pattern.
    private readonly Dictionary<long, string> _instanceMap = new();

    // React-channel event log — every emitted GiftAccepted is appended,
    // bounded by EventLogSoftCap.
    private readonly List<GiftAccepted> _eventLog = new();

    // Handlers carry their replay mode so live dispatch can include or skip
    // by mode if we ever extend that surface. Today every handler receives
    // every live event (replay was decided at attach time); the per-handler
    // mode is retained for future extension. Mirrors
    // PlayerEffectsStateService's handler shape.
    private readonly List<(Action<GiftAccepted> Handler, ReplayMode Replay)> _handlers = new();

    // Transient SM state. Both pending tuples carry the L1 envelope
    // timestamp captured at the half-event for symmetry, even though emit
    // always uses the DeltaFavor side's timestamp (matches the in-Arwen
    // behaviour at CalibrationService.cs:195).
    private string? _activeNpcKey;
    private long? _activeEntityId;
    private DateTimeOffset _interactionStartedAt;
    private (long InstanceId, string InternalName, DateTimeOffset Timestamp)? _pendingDeletedItem;
    private (string NpcKey, double Delta, DateTimeOffset Timestamp)? _pendingDelta;

    private ILogSubscription? _subscription;
    private bool _eventLogCapWarned;

    public GiftSignalService(ILogStreamDriver driver, IDiagnosticsSink? diag = null)
    {
        _driver = driver;
        _diag = diag;
    }

    public IDisposable Subscribe(
        Action<GiftAccepted> handler,
        ReplayMode replay = ReplayMode.FromSessionStart)
    {
        ArgumentNullException.ThrowIfNull(handler);
        lock (_lock)
        {
            // React-channel contract (post-#585 shape): default replay is the
            // full in-session event log, atomically under _lock so the
            // replay-then-live boundary is race-free with concurrent Fire()s.
            // SinceSubscribe is treated like LiveOnly — the service has no
            // notion of an arbitrary "since timestamp T" today, same shape
            // InventoryService.Subscribe uses.
            if (replay == ReplayMode.FromSessionStart)
            {
                foreach (var evt in _eventLog)
                    Invoke(handler, evt);
            }
            _handlers.Add((handler, replay));
            return new Subscription(this, handler);
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _diag?.Info("GameState.Gifting",
            "Subscribing to L1 driver (LocalPlayer pipe) for "
            + "ProcessStartInteraction / ProcessDeleteItem / ProcessDeltaFavor / "
            + "ProcessEndInteraction / ProcessAddItem");
        try
        {
            _subscription = _driver.Subscribe<LocalPlayerLogLine>(
                OnLocalPlayer,
                new LogSubscriptionOptions
                {
                    ReplayMode = ReplayMode.FromSessionStart,
                    DeliveryContext = DeliveryContext.Inline,
                    DiagnosticCategory = "GameState.Gifting",
                });

            try { await Task.Delay(Timeout.Infinite, stoppingToken).ConfigureAwait(false); }
            catch (OperationCanceledException) { /* expected on host stop */ }
        }
        finally
        {
            _subscription?.Dispose();
            _subscription = null;
        }
    }

    public override void Dispose()
    {
        _subscription?.Dispose();
        _subscription = null;
        base.Dispose();
    }

    private ValueTask OnLocalPlayer(LogEnvelope<LocalPlayerLogLine> envelope)
    {
        var data = envelope.Payload.Data;
        var ts = ToOffset(envelope.Payload.Timestamp);

        // Order checks cheap-first: AddItem dominates Player.log by volume
        // (every loot pickup, every crafting product, every NPC drop). Keep
        // it ahead of the rarer verbs to short-circuit the per-line cost.
        var add = AddItemRx().Match(data);
        if (add.Success && long.TryParse(add.Groups[2].ValueSpan, out var addId))
        {
            HandleAddItem(addId, add.Groups[1].Value);
            return ValueTask.CompletedTask;
        }

        var del = DeleteItemRx().Match(data);
        if (del.Success && long.TryParse(del.Groups[1].ValueSpan, out var delId))
        {
            HandleDeleteItem(delId, ts);
            return ValueTask.CompletedTask;
        }

        var delta = DeltaFavorRx().Match(data);
        if (delta.Success
            && double.TryParse(delta.Groups[2].ValueSpan, out var deltaValue))
        {
            HandleDeltaFavor(delta.Groups[1].Value, deltaValue, ts);
            return ValueTask.CompletedTask;
        }

        var start = StartInteractionRx().Match(data);
        if (start.Success && long.TryParse(start.Groups[1].ValueSpan, out var startEntity))
        {
            HandleStartInteraction(startEntity, start.Groups[2].Value, ts);
            return ValueTask.CompletedTask;
        }

        var end = EndInteractionRx().Match(data);
        if (end.Success && long.TryParse(end.Groups[1].ValueSpan, out var endEntity))
        {
            HandleEndInteraction(endEntity);
        }
        return ValueTask.CompletedTask;
    }

    private void HandleAddItem(long instanceId, string internalName)
    {
        lock (_lock)
        {
            // Never evict. Matches InventoryService.HandleDeleteItem's
            // retain-on-delete semantics — a downstream DeleteItem for an id
            // we've already seen still resolves to its InternalName even if
            // PG never re-emits the AddItem.
            _instanceMap[instanceId] = internalName;
        }
    }

    private void HandleStartInteraction(long entityId, string npcKey, DateTimeOffset timestamp)
    {
        lock (_lock)
        {
            _activeNpcKey = npcKey;
            _activeEntityId = entityId;
            _interactionStartedAt = timestamp;
            _pendingDeletedItem = null;
            _pendingDelta = null;
            _diag?.Trace("GameState.Gifting",
                $"StartInteraction npc={npcKey} entity={entityId} @ {timestamp:O}");
        }
    }

    private void HandleEndInteraction(long entityId)
    {
        lock (_lock)
        {
            // Only clear when EndInteraction matches the entity we armed on.
            // PG can emit EndInteraction for windows the SM never armed
            // (e.g. interactions with "" NpcKey targets — graves, doors —
            // which never set _activeNpcKey); those must not stomp an
            // active gift-eligible window.
            if (_activeEntityId is null || entityId != _activeEntityId.Value) return;

            _diag?.Trace("GameState.Gifting",
                $"EndInteraction entity={entityId} (npc={_activeNpcKey})");
            _activeNpcKey = null;
            _activeEntityId = null;
            _pendingDeletedItem = null;
            _pendingDelta = null;
        }
    }

    private void HandleDeleteItem(long instanceId, DateTimeOffset timestamp)
    {
        lock (_lock)
        {
            if (_activeNpcKey is null) return; // no armed window

            if (!_instanceMap.TryGetValue(instanceId, out var internalName))
            {
                // No AddItem ingested for this id — could be a carryover from a
                // prior PG session whose AddItem isn't in this log's replay
                // buffer. Trace and skip; don't half-arm pendingDeletedItem
                // with an empty name.
                _diag?.Trace("GameState.Gifting",
                    $"DeleteItem id={instanceId} unresolved while talking to {_activeNpcKey} — skipping");
                return;
            }

            if (_pendingDelta is { } pendingDelta)
            {
                // DeltaFavor-first resolved: emit, clear, and (for the
                // DeleteItem stash) skip — same as Arwen
                // CalibrationService.OnItemDeleted at line 192.
                _pendingDelta = null;
                Emit(new GiftAccepted(
                    NpcKey: pendingDelta.NpcKey,
                    ItemInstanceId: instanceId,
                    ItemInternalName: internalName,
                    DeltaFavor: pendingDelta.Delta,
                    Timestamp: pendingDelta.Timestamp,
                    InteractionStartedAt: _interactionStartedAt));
                return;
            }

            _pendingDeletedItem = (instanceId, internalName, timestamp);
            _diag?.Trace("GameState.Gifting",
                $"DeleteItem id={instanceId} name={internalName} pending under npc={_activeNpcKey}");
        }
    }

    private void HandleDeltaFavor(string npcKey, double delta, DateTimeOffset timestamp)
    {
        lock (_lock)
        {
            if (delta <= 0) return;
            if (_activeNpcKey is null) return;          // no armed window
            if (_activeNpcKey != npcKey) return;        // talking to A, delta for B → not this gift

            if (_pendingDeletedItem is { } pending)
            {
                // DeleteItem-first resolved: emit, clear both pending slots.
                _pendingDeletedItem = null;
                _pendingDelta = null;
                Emit(new GiftAccepted(
                    NpcKey: npcKey,
                    ItemInstanceId: pending.InstanceId,
                    ItemInternalName: pending.InternalName,
                    DeltaFavor: delta,
                    Timestamp: timestamp,
                    InteractionStartedAt: _interactionStartedAt));
                return;
            }

            _pendingDelta = (npcKey, delta, timestamp);
            _diag?.Trace("GameState.Gifting",
                $"DeltaFavor npc={npcKey} delta={delta} pending");
        }
    }

    /// <summary>
    /// MUST be called with <see cref="_lock"/> held. Appends to the
    /// React-channel event log and dispatches to every currently-attached
    /// handler. Holding the lock around both is what makes the
    /// Subscribe-vs-live-event race impossible — same shape
    /// <see cref="Mithril.GameState.Inventory.InventoryService"/> uses (and
    /// the post-#590 <c>PlayerEffectsStateService</c> mirrors).
    /// </summary>
    private void Emit(GiftAccepted evt)
    {
        AppendEventLog(evt);
        _diag?.Info("GameState.Gifting",
            $"GiftAccepted npc={evt.NpcKey} item={evt.ItemInternalName} (id={evt.ItemInstanceId}) "
            + $"delta={evt.DeltaFavor} @ {evt.Timestamp:O} (interaction started @ {evt.InteractionStartedAt:O})");
        foreach (var (handler, _) in _handlers) Invoke(handler, evt);
    }

    /// <summary>
    /// MUST be called with <see cref="_lock"/> held. Appends an event to the
    /// internal log used by <see cref="Subscribe"/>'s replay path. Soft cap
    /// (<see cref="EventLogSoftCap"/>) protects against unbounded growth on
    /// a degenerate stream; a one-time <see cref="DiagnosticLevel.Warn"/>
    /// fires the first time the cap is exceeded. Past the cap the log keeps
    /// growing — dropping events would silently break the replay contract.
    /// Same shape the post-#590 <c>PlayerEffectsStateService</c> uses;
    /// InventoryService's chunk-trim variant is a perf optimization for a
    /// higher-volume stream we don't need here (gifts are rare).
    /// </summary>
    private void AppendEventLog(GiftAccepted evt)
    {
        _eventLog.Add(evt);
        if (_eventLog.Count > EventLogSoftCap && !_eventLogCapWarned)
        {
            _eventLogCapWarned = true;
            _diag?.Warn("GameState.Gifting",
                $"Event-log size exceeded soft cap ({EventLogSoftCap}). "
                + "Replay-on-Subscribe will continue to deliver every event in order; "
                + "log keeps growing (no drop). Investigate if not expected.");
        }
    }

    private void Invoke(Action<GiftAccepted> handler, GiftAccepted evt)
    {
        try { handler(evt); }
        catch (Exception ex) { _diag?.Warn("GameState.Gifting", $"Subscriber threw: {ex.Message}"); }
    }

    /// <summary>
    /// Defensive offset normalization. L1 envelopes carry
    /// <see cref="DateTimeOffset"/> already; we stamp the kind explicitly so
    /// downstream consumers see UTC offset +00:00 regardless of how the
    /// upstream synthesised it.
    /// </summary>
    private static DateTimeOffset ToOffset(DateTimeOffset ts) =>
        ts.Offset == TimeSpan.Zero ? ts : ts.ToUniversalTime();

    private sealed class Subscription : IDisposable
    {
        private GiftSignalService? _owner;
        private readonly Action<GiftAccepted> _handler;

        public Subscription(GiftSignalService owner, Action<GiftAccepted> handler)
        {
            _owner = owner;
            _handler = handler;
        }

        public void Dispose()
        {
            var owner = Interlocked.Exchange(ref _owner, null);
            if (owner is null) return;
            lock (owner._lock)
            {
                for (int i = owner._handlers.Count - 1; i >= 0; i--)
                {
                    if (owner._handlers[i].Handler == _handler)
                    {
                        owner._handlers.RemoveAt(i);
                        break;
                    }
                }
            }
        }
    }
}
