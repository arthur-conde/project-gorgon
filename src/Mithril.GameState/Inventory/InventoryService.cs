using System.IO;
using System.Text.RegularExpressions;
using Mithril.Shared.Correlation;
using Mithril.Shared.Diagnostics;
using Mithril.Shared.Game;
using Mithril.Shared.Logging;
using Mithril.Shared.Reference;
using Mithril.GameReports;
using Microsoft.Extensions.Hosting;

namespace Mithril.GameState.Inventory;

/// <summary>
/// Eagerly subscribes to the L1 driver's LocalPlayer + chat pipes at shell
/// startup and maintains the canonical <c>instanceId → (InternalName, StackSize)</c>
/// map. The driver's per-pipe session-replay buffer guarantees that the
/// initial flush of <c>ProcessAddItem</c> events is observed here regardless
/// of subscriber ordering; modules that need inventory lookups should depend
/// on <see cref="IInventoryService"/> rather than re-parsing the log.
///
/// <para><b>L1 migration (#565).</b> Player.log consumption moved from
/// <see cref="IPlayerLogStream"/> to
/// <see cref="ILogStreamDriver.Subscribe{T}"/> over
/// <see cref="LocalPlayerLogLine"/>; chat moved from
/// <see cref="IChatLogStream"/> to <see cref="RawLogLine"/>. The Tier-1
/// <see cref="PendingCorrelator{TKey,TReq}"/> stays — chat + Player.log are
/// two physically separate sources with no shared total order, so the
/// 5-second TTL correlator is the right ordering primitive (not the L1
/// multi-pipe shape used by Position / Pin / Weather in #556). What L1
/// adds: per-message handler containment, drop accounting, and the fault
/// state machine that surfaces a degraded subscription on
/// <see cref="Mithril.Shared.Modules.IAttentionAggregator"/>.</para>
///
/// Subscribers attach via <see cref="Subscribe"/>, which atomically replays
/// the current live-map contents before going live. This closes the late-join
/// race that a plain event would otherwise leave open: if InventoryService has
/// already processed session-replay lines before the subscriber attaches, the
/// subscriber would otherwise miss those <c>Added</c> events permanently.
///
/// Stack-size tracking sources, layered on the canonical map:
/// <list type="bullet">
///   <item><c>ProcessAddItem</c> seeds size = 1 (provisional). Chat correlation
///   may correct this for stacked additions (loot drops, vault withdrawals).</item>
///   <item><c>ProcessUpdateItemCode(Id, code, _)</c> decodes
///   <c>(code &gt;&gt; 16) + 1</c> as the post-event size. Authoritative for
///   splits, merges, plant-consumption, and similar in-place mutations.</item>
///   <item><c>ProcessRemoveFromStorageVault(_, _, Id, N)</c> carries the literal
///   stack size for vault-to-bag transfers. Authoritative when present.</item>
///   <item>Chat <c>[Status] X [xN] added to inventory.</c> via the L1
///   <c>Subscribe&lt;RawLogLine&gt;</c> chat pipe carries the count for fresh
///   additions — correlated to <c>ProcessAddItem</c> by InternalName +
///   arrival window.</item>
/// </list>
/// Stack size for entries marked <c>Deleted</c> is retained so late lookups
/// (Arwen's gift-attribution path) can see the pre-removal size.
/// </summary>
public sealed partial class InventoryService : BackgroundService, IInventoryService
{
    // ProcessAddItem(InternalName(instanceId), slot, bool)
    [GeneratedRegex(@"ProcessAddItem\((\w+)\((\d+)\),", RegexOptions.CultureInvariant)]
    private static partial Regex AddItemRx();

    // ProcessDeleteItem(instanceId)
    [GeneratedRegex(@"ProcessDeleteItem\((\d+)\)", RegexOptions.CultureInvariant)]
    private static partial Regex DeleteItemRx();

    // ProcessUpdateItemCode(instanceId, code, bool)
    // `code` packs `(stackSize - 1) << 16 | TypeID`; we decode the high 16 bits.
    [GeneratedRegex(@"ProcessUpdateItemCode\((\d+),\s*(\d+),\s*\w+\)", RegexOptions.CultureInvariant)]
    private static partial Regex UpdateItemCodeRx();

    // ProcessRemoveFromStorageVault(arg1, arg2, instanceId, stackSize) — args 1/2 are vault metadata.
    [GeneratedRegex(@"ProcessRemoveFromStorageVault\([^,]+,\s*[^,]+,\s*(\d+),\s*(\d+)\)", RegexOptions.CultureInvariant)]
    private static partial Regex RemoveFromStorageVaultRx();

    // Chat-side correlation has a small staleness window; entries older than this are dropped.
    private static readonly TimeSpan PendingChatTtl = TimeSpan.FromSeconds(5);

    private readonly ILogStreamDriver _driver;
    private readonly IReferenceDataService? _refData;
    private readonly IDiagnosticsSink? _diag;
    private readonly GameConfig? _gameConfig;
    // Optional foundation service (#612). When present, InventoryService
    // subscribes to its StorageReportsChanged event for reconcile triggers
    // and reads storage snapshots via GetStorageReport/GetStorageContents —
    // rather than running its own FileSystemWatcher on the Reports/ directory.
    // Null in test contexts that don't wire the service.
    private readonly IGameReportsService? _gameReports;
    private readonly TimeProvider _time;
    private FileSystemWatcher? _seedWatcher;
    private Timer? _seedDebounce;
    private ILogSubscription? _playerSubscription;
    private ILogSubscription? _chatSubscription;

    // One-shot guard for the SinceSubscribe coercion diag. Subscribe treats
    // SinceSubscribe as LiveOnly today (no "since timestamp T" window
    // implemented); we emit a single Trace per process so the spurious knob
    // is observable without flooding diagnostics on repeated subscribes.
    // Mirrors LogStreamDriver's chat-pipe ReplayMode-mismatch diagnostic
    // (LogStreamDriver.cs ReplayMode != LiveOnly branch).
    private static int s_sinceSubscribeDiagFired;

    // _subLock guards _map, _handlers, _eventLog, and _eventLogOverflowWarned.
    // _pendingChat / _pendingAdd are PendingCorrelator instances that are
    // independently thread-safe via their own internal _gate; they don't require
    // _subLock for correctness, but both L1 subscription handlers (OnLocalPlayer,
    // OnChat) take _subLock around correlator calls anyway so the
    // drain-then-mutate-_map sequence stays atomic within a handler. The two
    // subscriptions have independent pump threads, so _subLock is load-bearing for
    // cross-pipe serialization too.
    private readonly object _subLock = new();
    private readonly Dictionary<long, MapEntry> _map = new();
    private readonly List<Action<InventoryEvent>> _handlers = new();

    // React-channel event log (#585). Every InventoryEvent the service emits
    // is appended here so a late subscriber asking for the default
    // ReplayMode.FromSessionStart replay receives the full session, including
    // Deleted events for items that were added-and-deleted before it attached.
    // Soft-capped at EventLogSoftCap: when exceeded the oldest entries are
    // dropped to bound memory (~50 bytes/entry x 50k ~= 2.5 MB worst case) and
    // a single Warn fires so the situation is observable rather than silent.
    // The cap exists for pathological sessions; typical PG sessions produce
    // well under it.
    private const int EventLogSoftCap = 50_000;
    // When trimming, drop this many entries at once so we amortize the
    // shift cost across many appends rather than a one-at-a-time slide.
    private const int EventLogTrimChunk = 4_096;
    private readonly List<InventoryEvent> _eventLog = new();
    private bool _eventLogOverflowWarned;

    // Bidirectional chat/AddItem correlation. Either signal can arrive first within
    // the same second:
    //   - chat first  → enqueue to _pendingChat[InternalName]; AddItem dequeues.
    //   - AddItem first → enqueue to _pendingAdd[InternalName] with the InstanceId;
    //                     chat dequeues and back-fills _map[InstanceId].StackSize.
    // PendingCorrelator owns the per-key FIFO + arrival-window TTL + lazy eviction;
    // the piggyback drain at the top of every handler closes the leak where an
    // entry that never matched its counterpart would otherwise sit forever (the
    // pre-extraction TtlList design only consulted the TTL on the matching path).
    // Both correlators pass onUnmatched: null — InventoryService's policy for an
    // un-correlated half-event is the same "silent drop" the original code had;
    // promoting that to an explicit policy is a separate change. Lock-ordering
    // hazard for that future change: handlers hold _subLock when invoking the
    // correlator, so a non-null onUnmatched callback would inherit
    // _subLock → _gate → callback ordering. A callback that synchronously took a
    // different module's lock would create a cross-module ordering constraint;
    // design the callback to be dispatch-only (queue + drain off-handler) rather
    // than synchronously taking foreign locks.
    private readonly PendingCorrelator<string, int> _pendingChat;
    private readonly PendingCorrelator<string, long> _pendingAdd;

    // InternalName → authoritative stack size, sourced from the player's most
    // recent *_items_*.json export. Populated at startup and on Reports/ writes;
    // restricted to InternalNames that appear exactly once with IsInInventory ==
    // true and have MaxStackSize > 1. Consumed (removed) on the next HandleAddItem
    // for that name when chat correlation isn't available, so a stale entry
    // pollutes at most one event per InternalName.
    private readonly Dictionary<string, int> _seededStackSizes = new(StringComparer.Ordinal);

    // SizeConfirmed distinguishes "we know the stack size" from "we defaulted
    // to 1 because the AddItem had no chat correlation, no export seed, and
    // no later UpdateItemCode/Vault/chat-back-fill landed yet." Without this,
    // a session-replayed AddItem for a carryover stack looks identical to a
    // freshly-picked-up single fish — and Arwen would then record gifts of
    // those carryover stacks at quantity=1 instead of routing them to the
    // pending-observation queue. Confirmation flips on at:
    //   - HandleAddItem chat-correlation hit
    //   - HandleAddItem non-stackable (MaxStackSize == 1) short-circuit
    //   - HandleAddItem export-seed hit
    //   - HandleUpdateItemCode
    //   - HandleRemoveFromStorageVault
    //   - HandleChatStatusAdd back-fill
    //   - LoadExportSeeds reconcile pass
    private readonly record struct MapEntry(string InternalName, DateTime Timestamp, bool Deleted, int StackSize, bool SizeConfirmed);

    /// <summary>
    /// Snapshot of a single live (un-deleted) <see cref="_map"/> entry,
    /// captured during <see cref="LoadExportSeeds"/>'s reconcile pass.
    /// Replaces a (long, int, bool) tuple so the call sites below read
    /// like prose instead of <c>live.Item3</c>.
    /// </summary>
    private readonly record struct LiveEntrySnapshot(long Id, int Size, bool Confirmed);

    public InventoryService(
        ILogStreamDriver driver,
        IDiagnosticsSink? diag = null,
        IReferenceDataService? refData = null,
        GameConfig? gameConfig = null,
        TimeProvider? time = null,
        IGameReportsService? gameReports = null)
    {
        _driver = driver;
        _diag = diag;
        _refData = refData;
        _gameConfig = gameConfig;
        _gameReports = gameReports;
        _time = time ?? TimeProvider.System;
        _pendingChat = new PendingCorrelator<string, int>(PendingChatTtl, _time, keyComparer: StringComparer.Ordinal);
        _pendingAdd = new PendingCorrelator<string, long>(PendingChatTtl, _time, keyComparer: StringComparer.Ordinal);
    }

    public bool TryResolve(long instanceId, out string internalName)
    {
        lock (_subLock)
        {
            if (_map.TryGetValue(instanceId, out var entry))
            {
                internalName = entry.InternalName;
                return true;
            }
        }
        internalName = "";
        return false;
    }

    public bool TryGetStackSize(long instanceId, out int stackSize)
    {
        lock (_subLock)
        {
            if (_map.TryGetValue(instanceId, out var entry)
                && entry.StackSize > 0
                && entry.SizeConfirmed)
            {
                stackSize = entry.StackSize;
                return true;
            }
        }
        stackSize = 0;
        return false;
    }

    public IDisposable Subscribe(
        Action<InventoryEvent> handler,
        ReplayMode replay = ReplayMode.FromSessionStart)
    {
        ArgumentNullException.ThrowIfNull(handler);
        // SinceSubscribe is treated like LiveOnly here — InventoryService
        // has no notion of an arbitrary "since timestamp T" window today
        // (mirrors LogStreamDriver's current behaviour for that mode).
        // Emit a one-shot Trace per process for symmetry with the
        // LogStreamDriver chat-pipe ReplayMode-mismatch diagnostic, so the
        // spurious knob doesn't go unnoticed. Fired outside _subLock — the
        // diagnostics sink is independently thread-safe and this avoids
        // re-entering the sink while holding the subscription lock.
        if (replay == ReplayMode.SinceSubscribe
            && Interlocked.CompareExchange(ref s_sinceSubscribeDiagFired, 1, 0) == 0)
        {
            _diag?.Trace(
                "GameState.Inventory",
                "ReplayMode.SinceSubscribe is not yet implemented for IInventoryService; treating as LiveOnly. " +
                "This diagnostic fires once per process.");
        }
        lock (_subLock)
        {
            // React-channel contract (#585): default replay is the full
            // in-session event log, atomically under _subLock so the
            // replay-then-live boundary is race-free with concurrent Fire()s.
            if (replay == ReplayMode.FromSessionStart)
            {
                foreach (var evt in _eventLog)
                    Invoke(handler, evt);
            }
            _handlers.Add(handler);
            return new Subscription(this, handler);
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Seed before subscribing — the seed map must be in place by the time
        // session-replay AddItem events flow, otherwise replayed adds default
        // to size = 1 and the seed is never consulted for those instances.
        LoadExportSeeds();
        SetupSeedWatcher();

        try
        {
            _diag?.Info("GameState.Inventory", "Subscribing to L1 driver (LocalPlayer + chat pipes) for inventory events");
            _playerSubscription = _driver.Subscribe<LocalPlayerLogLine>(
                OnLocalPlayer,
                new LogSubscriptionOptions
                {
                    ReplayMode = ReplayMode.FromSessionStart,
                    DeliveryContext = DeliveryContext.Inline,
                    DiagnosticCategory = "GameState.Inventory",
                });
            // Chat carries no backlog by construction — L1 coerces any
            // non-LiveOnly request to LiveOnly. Asking for it explicitly so
            // the intent reads at the call site.
            _chatSubscription = _driver.Subscribe<RawLogLine>(
                OnChat,
                new LogSubscriptionOptions
                {
                    ReplayMode = ReplayMode.LiveOnly,
                    DeliveryContext = DeliveryContext.Inline,
                    DiagnosticCategory = "GameState.Inventory",
                });

            try { await Task.Delay(Timeout.Infinite, stoppingToken).ConfigureAwait(false); }
            catch (OperationCanceledException) { /* expected on host stop */ }
        }
        finally
        {
            _playerSubscription?.Dispose();
            _playerSubscription = null;
            _chatSubscription?.Dispose();
            _chatSubscription = null;
            _seedDebounce?.Dispose();
            _seedWatcher?.Dispose();
            if (_gameReports is not null)
                _gameReports.StorageReportsChanged -= OnGameReportsStorageChanged;
        }
    }

    private ValueTask OnLocalPlayer(LogEnvelope<LocalPlayerLogLine> envelope)
    {
        var data = envelope.Payload.Data;
        var ts = envelope.Payload.Timestamp.UtcDateTime;

        var add = AddItemRx().Match(data);
        if (add.Success && long.TryParse(add.Groups[2].ValueSpan, out var addId))
        {
            HandleAddItem(addId, add.Groups[1].Value, ts);
            return ValueTask.CompletedTask;
        }

        var del = DeleteItemRx().Match(data);
        if (del.Success && long.TryParse(del.Groups[1].ValueSpan, out var delId))
        {
            HandleDeleteItem(delId, ts);
            return ValueTask.CompletedTask;
        }

        var upd = UpdateItemCodeRx().Match(data);
        if (upd.Success
            && long.TryParse(upd.Groups[1].ValueSpan, out var updId)
            && long.TryParse(upd.Groups[2].ValueSpan, out var code))
        {
            HandleUpdateItemCode(updId, code, ts);
            return ValueTask.CompletedTask;
        }

        var vault = RemoveFromStorageVaultRx().Match(data);
        if (vault.Success
            && long.TryParse(vault.Groups[1].ValueSpan, out var vaultId)
            && int.TryParse(vault.Groups[2].ValueSpan, out var vaultSize))
        {
            HandleRemoveFromStorageVault(vaultId, vaultSize, ts);
        }
        return ValueTask.CompletedTask;
    }

    private ValueTask OnChat(LogEnvelope<RawLogLine> envelope)
    {
        var parsed = InventoryStatusChatParser.TryParse(envelope.Payload.Line);
        if (parsed is not null)
            HandleChatStatusAdd(parsed.Value.DisplayName, parsed.Value.Count, envelope.Payload.Timestamp.UtcDateTime);
        return ValueTask.CompletedTask;
    }

    private void HandleAddItem(long instanceId, string internalName, DateTime timestamp)
    {
        lock (_subLock)
        {
            DrainPendingStale();

            // Re-emission pulse (zone change / server resync) for an already-tracked
            // InstanceId. Carries no size information, so we must not touch StackSize,
            // SizeConfirmed, _pendingChat, or _pendingAdd — otherwise a confirmed N
            // gets silently clobbered to (1, unconfirmed) and a queued chat slot is
            // burned on the wrong id. See issue #10.
            if (_map.TryGetValue(instanceId, out var existing) && !existing.Deleted)
            {
                _map[instanceId] = existing with { Timestamp = timestamp };
                _diag?.Trace("GameState.Inventory",
                    $"Add-reemit id={instanceId} name={existing.InternalName} size={existing.StackSize} confirmed={existing.SizeConfirmed}");
                return;
            }

            // Try the pending-chat queue first — chat may have arrived ahead of us.
            int size = 1;
            bool seeded = false;
            bool confirmed = false;
            bool nonStackable = false;
            if (_pendingChat.TryTake(internalName, out var pendingCount))
            {
                size = pendingCount;
                confirmed = true;
            }
            else if (TryGetMaxStackSize(internalName, out var maxStack) && maxStack == 1)
            {
                // Reference data says this item never stacks (equipment, unique consumables,
                // etc.) — the AddItem alone is authoritative for size = 1. No chat needed.
                confirmed = true;
                nonStackable = true;
            }
            else if (_seededStackSizes.TryGetValue(internalName, out var seededSize))
            {
                // Export-seeded fallback: trust the most recent items export when no
                // chat correlation is available. Consume on first hit so a second
                // AddItem of the same InternalName falls through to the default.
                // We still enqueue pending-add so a chat replay (which IS contemporaneous)
                // can override the seeded size via the standard StackChanged path.
                size = seededSize;
                seeded = true;
                confirmed = true;
                _seededStackSizes.Remove(internalName);
                _pendingAdd.Add(internalName, instanceId);
            }
            else
            {
                // No chat yet, no export seed; remember this InstanceId so a later
                // chat status can back-fill the size. Size remains the unconfirmed
                // default — TryGetStackSize will report unknown until a confirming
                // event lands (chat back-fill, UpdateItemCode, vault, reconcile).
                _pendingAdd.Add(internalName, instanceId);
            }

            _map[instanceId] = new MapEntry(internalName, timestamp, Deleted: false, StackSize: size, SizeConfirmed: confirmed);
            var sourceTag = nonStackable ? " (non-stackable)"
                : seeded ? " (export-seeded)"
                : confirmed ? " (chat)"
                : " (unconfirmed)";
            _diag?.Trace("GameState.Inventory", $"Add    id={instanceId} name={internalName} size={size}{sourceTag} (total={_map.Count})");
            Fire(new InventoryEvent(InventoryEventKind.Added, instanceId, internalName, timestamp, size, confirmed));
        }
    }

    /// <summary>
    /// Resolve an InternalName to its <see cref="Item.MaxStackSize"/> via reference data.
    /// Used by <see cref="HandleAddItem"/> to short-circuit chat correlation for items that
    /// can never stack (equipment, unique consumables) — for those, the AddItem alone is
    /// authoritative for size = 1.
    /// </summary>
    private bool TryGetMaxStackSize(string internalName, out int maxStackSize)
    {
        if (_refData is not null
            && _refData.ItemsByInternalName.TryGetValue(internalName, out var entry))
        {
            maxStackSize = entry.MaxStackSize;
            return true;
        }
        maxStackSize = 0;
        return false;
    }

    private void HandleDeleteItem(long instanceId, DateTime timestamp)
    {
        lock (_subLock)
        {
            DrainPendingStale();
            if (!_map.TryGetValue(instanceId, out var entry))
            {
                _diag?.Trace("GameState.Inventory", $"Delete id={instanceId} — not in map, ignored");
                return;
            }
            if (entry.Deleted)
            {
                // Already marked deleted; suppress the duplicate event.
                return;
            }
            // Mark as deleted but retain the entry (and the last-known StackSize)
            // so concurrent TryResolve / TryGetStackSize callers (e.g. Arwen's
            // gift-attribution path) can still resolve an id whose delete line
            // they've already read past.
            _map[instanceId] = entry with { Deleted = true, Timestamp = timestamp };
            _diag?.Trace("GameState.Inventory", $"Delete id={instanceId} name={entry.InternalName} size={entry.StackSize} (retained)");
            Fire(new InventoryEvent(InventoryEventKind.Deleted, instanceId, entry.InternalName, timestamp, entry.StackSize, entry.SizeConfirmed));
        }
    }

    private void HandleUpdateItemCode(long instanceId, long code, DateTime timestamp)
    {
        // Decode: high 16 bits + 1 = post-event stack size; low 16 bits = TypeID (unused here).
        var newSize = (int)(code >> 16) + 1;
        if (newSize <= 0) return;

        lock (_subLock)
        {
            DrainPendingStale();
            if (!_map.TryGetValue(instanceId, out var entry))
            {
                // Update for an InstanceId we've never seen — game can emit these for
                // entries that pre-date the session log. Skip; we'd have no InternalName.
                _diag?.Trace("GameState.Inventory", $"UpdateCode id={instanceId} size={newSize} — not in map, ignored");
                return;
            }
            // UpdateCode is authoritative for the post-event size, so confirmation
            // flips on regardless of whether the size value changed (a "1 → 1"
            // UpdateCode for a previously-defaulted entry still moves it from
            // unconfirmed to confirmed). Only suppress the StackChanged fire when
            // both size and confirmation status match what's already there.
            if (entry.StackSize == newSize && entry.SizeConfirmed) return;
            _map[instanceId] = entry with { StackSize = newSize, Timestamp = timestamp, SizeConfirmed = true };
            _diag?.Trace("GameState.Inventory", $"UpdateCode id={instanceId} name={entry.InternalName} size={newSize}");
            Fire(new InventoryEvent(InventoryEventKind.StackChanged, instanceId, entry.InternalName, timestamp, newSize, SizeConfirmed: true));
        }
    }

    private void HandleRemoveFromStorageVault(long instanceId, int stackSize, DateTime timestamp)
    {
        if (stackSize <= 0) return;
        lock (_subLock)
        {
            DrainPendingStale();
            // RemoveFromStorageVault pairs with the AddItem of a vault withdrawal landing
            // in an empty bag (the bag-side InstanceId), AND with merge-into-existing-stack
            // cases (the vault-side InstanceId, which we don't track). Only update if we
            // know this id — that filters cleanly.
            if (!_map.TryGetValue(instanceId, out var entry)) return;
            if (entry.StackSize == stackSize && entry.SizeConfirmed) return;

            _map[instanceId] = entry with { StackSize = stackSize, Timestamp = timestamp, SizeConfirmed = true };
            _diag?.Trace("GameState.Inventory", $"RemoveFromVault id={instanceId} name={entry.InternalName} size={stackSize}");
            Fire(new InventoryEvent(InventoryEventKind.StackChanged, instanceId, entry.InternalName, timestamp, stackSize, SizeConfirmed: true));
        }
    }

    private void HandleChatStatusAdd(string displayName, int count, DateTime timestamp)
    {
        if (count <= 0) return;
        if (_refData is null) return; // can't resolve display→internal without reference data
        if (!TryResolveDisplayNameToInternalName(displayName, out var internalName))
        {
            // The chat line parsed cleanly but no item with that display name exists in
            // reference data — likely a name-mapping miss (renamed item, localization
            // variant, or new item not in the bundled fallback). Logging here is the
            // bread-crumb that lets us spot the gap when an AddItem stays unconfirmed.
            _diag?.Trace("GameState.Inventory", $"Chat status: '{displayName}' (x{count}) — no InternalName match in reference data");
            return;
        }

        lock (_subLock)
        {
            DrainPendingStale();
            // Try to back-fill a recent AddItem that defaulted to size = 1.
            if (_pendingAdd.TryTake(internalName, out var instanceId))
            {
                if (_map.TryGetValue(instanceId, out var entry)
                    && (entry.StackSize != count || !entry.SizeConfirmed))
                {
                    _map[instanceId] = entry with { StackSize = count, Timestamp = timestamp, SizeConfirmed = true };
                    _diag?.Trace("GameState.Inventory", $"Chat → Add id={instanceId} name={internalName} size={count}");
                    Fire(new InventoryEvent(InventoryEventKind.StackChanged, instanceId, internalName, timestamp, count, SizeConfirmed: true));
                }
                return;
            }

            // No matching pending AddItem; remember this count for the next AddItem of the
            // same InternalName.
            _pendingChat.Add(internalName, count);
        }
    }

    /// <summary>
    /// Lazy piggyback drain — called from every event handler. The
    /// <see cref="PendingCorrelator{TKey,TReq}"/> primitive is self-synchronizing,
    /// so this method no longer requires <c>_subLock</c> to be held for
    /// correctness; callers take it anyway so the drain-then-mutate-<see cref="_map"/>
    /// sequence stays atomic within a handler. Walks both pending correlators,
    /// evicting stale entries. Cost is O(distinct InternalNames in pending),
    /// trivial in practice (rarely more than a handful at a time).
    /// </summary>
    private void DrainPendingStale()
    {
        _pendingChat.DrainStale();
        _pendingAdd.DrainStale();
    }

    /// <summary>
    /// Read the player's most recent <c>*_items_*.json</c> export and rebuild
    /// <see cref="_seededStackSizes"/>. Skipped silently if the dependencies
    /// needed to interpret the export aren't present (no game root, no reference
    /// data). The rebuild is atomic under <see cref="_subLock"/>.
    ///
    /// After rebuilding the seed map, makes a second pass that reconciles
    /// already-tracked instances against the export: for each InternalName that
    /// appears exactly once in the export AND exactly once (un-deleted) in the
    /// live map, the export's <c>StackSize</c> is treated as authoritative —
    /// the live entry is updated and a <see cref="InventoryEventKind.StackChanged"/>
    /// event fires. Closes the carryover gap when a player runs an export
    /// mid-session for an instance whose original AddItem is in a prior session
    /// log (so chat / UpdateItemCode correlation never reaches it).
    /// </summary>
    internal void LoadExportSeeds()
    {
        if (_refData is null) return;

        // #612: prefer the IGameReportsService (foundation) when present;
        // it owns the report scan + parse + cache. The GameConfig-direct
        // path stays as a fallback for older test contexts that didn't wire
        // the service through DI.
        ReportFileInfo? newest;
        StorageReport report;

        if (_gameReports is not null)
        {
            newest = _gameReports.StorageReports.FirstOrDefault();
            if (newest is null)
            {
                _diag?.Trace("GameState.Inventory", "Seed: no exports found via IGameReportsService");
                return;
            }
            var loaded = _gameReports.GetStorageContents(newest.Character, newest.Server);
            if (loaded is null)
            {
                _diag?.Warn("GameState.Inventory", $"Seed: IGameReportsService failed to parse {newest.FilePath}");
                return;
            }
            report = loaded;
        }
        else if (_gameConfig is not null)
        {
            var dir = _gameConfig.ReportsDirectory;
            if (string.IsNullOrEmpty(dir)) return;

            try
            {
                newest = StorageReportLoader.ScanForReports(dir).FirstOrDefault();
                if (newest is null)
                {
                    _diag?.Trace("GameState.Inventory", $"Seed: no exports found in {dir}");
                    return;
                }
                report = StorageReportLoader.Load(newest.FilePath);
            }
            catch (Exception ex)
            {
                _diag?.Warn("GameState.Inventory", $"Seed: failed to load export: {ex.Message}");
                return;
            }
        }
        else
        {
            return; // No data source available.
        }

        // Group in-inventory entries by InternalName via TypeID lookup. Only
        // stackables (MaxStackSize > 1) are useful; non-stackables always have
        // size 1 anyway. A name that appears more than once is ambiguous —
        // we have no way to pick which bag-stack a given AddItem belongs to —
        // so we drop it entirely.
        var counts = new Dictionary<string, int>(StringComparer.Ordinal);
        var sizes = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var item in report.Items)
        {
            if (!item.IsInInventory) continue;
            if (!_refData.Items.TryGetValue(item.TypeID, out var entry)) continue;
            if (entry.MaxStackSize <= 1) continue;
            if (string.IsNullOrEmpty(entry.InternalName)) continue;

            counts[entry.InternalName!] = counts.TryGetValue(entry.InternalName!, out var c) ? c + 1 : 1;
            sizes[entry.InternalName!] = item.StackSize;
        }

        lock (_subLock)
        {
            _seededStackSizes.Clear();
            foreach (var (name, count) in counts)
            {
                if (count == 1) _seededStackSizes[name] = sizes[name];
            }
            _diag?.Trace("GameState.Inventory", $"Seeded {_seededStackSizes.Count} stack sizes from {Path.GetFileName(newest.FilePath)}");

            // Non-stackable confirm pass: any un-confirmed live entry whose item has
            // MaxStackSize == 1 is trivially size = 1. The export trigger is just a
            // convenient pump; the truth is reference-data-derived, not export-derived.
            // Done independently of the stackable reconcile below because non-stackables
            // are excluded from `counts`/`sizes` (filtered out at line ~528 above).
            var nowNs = _time.GetUtcNow().UtcDateTime;
            int nonStackConfirmed = 0;
            foreach (var (id, entry) in _map.ToArray())
            {
                if (entry.Deleted || entry.SizeConfirmed) continue;
                if (!_refData.ItemsByInternalName.TryGetValue(entry.InternalName, out var itemEntry)) continue;
                if (itemEntry.MaxStackSize != 1) continue;

                _map[id] = entry with { StackSize = 1, Timestamp = nowNs, SizeConfirmed = true };
                _diag?.Trace("GameState.Inventory", $"Non-stack confirm id={id} name={entry.InternalName} size=1");
                Fire(new InventoryEvent(InventoryEventKind.StackChanged, id, entry.InternalName, nowNs, 1, SizeConfirmed: true));
                nonStackConfirmed++;
            }
            if (nonStackConfirmed > 0)
                _diag?.Info("GameState.Inventory", $"Confirmed {nonStackConfirmed} non-stackable live entries from reference data");

            // Reconcile already-tracked instances against the fresh export. Tally
            // un-deleted live entries by InternalName; mark any name that appears
            // more than once as ambiguous (no way to know which bag-stack the
            // export's count maps to). Then for unambiguous matches whose live
            // size differs from the export, update + fire StackChanged.
            var liveSingle = new Dictionary<string, LiveEntrySnapshot>(StringComparer.Ordinal);
            var liveAmbiguous = new HashSet<string>(StringComparer.Ordinal);
            foreach (var (id, entry) in _map)
            {
                if (entry.Deleted) continue;
                if (liveAmbiguous.Contains(entry.InternalName)) continue;
                if (liveSingle.ContainsKey(entry.InternalName))
                {
                    liveSingle.Remove(entry.InternalName);
                    liveAmbiguous.Add(entry.InternalName);
                }
                else
                {
                    liveSingle[entry.InternalName] = new LiveEntrySnapshot(id, entry.StackSize, entry.SizeConfirmed);
                }
            }

            var now = _time.GetUtcNow().UtcDateTime;
            int reconciled = 0;
            foreach (var (name, count) in counts)
            {
                if (count != 1) continue;
                if (!liveSingle.TryGetValue(name, out var live)) continue;
                var exportSize = sizes[name];
                // Reconcile when size differs OR when the export confirms a
                // previously-unconfirmed default. Carryover stacks land here:
                // _map says size 1 (default), export says size N — fire to
                // promote unconfirmed → confirmed even if N happens to be 1.
                if (live.Size == exportSize && live.Confirmed) continue;

                var entry = _map[live.Id];
                _map[live.Id] = entry with { StackSize = exportSize, Timestamp = now, SizeConfirmed = true };
                _diag?.Trace("GameState.Inventory",
                    $"Export reconcile id={live.Id} name={name} size {live.Size} → {exportSize}{(live.Confirmed ? "" : " (confirming)")}");
                Fire(new InventoryEvent(InventoryEventKind.StackChanged, live.Id, name, now, exportSize, SizeConfirmed: true));
                reconciled++;
            }
            if (reconciled > 0)
                _diag?.Info("GameState.Inventory", $"Export reconciled {reconciled} live entries against {Path.GetFileName(newest.FilePath)}");
        }
    }

    private void SetupSeedWatcher()
    {
        // #612: when IGameReportsService is wired, subscribe to its already-
        // debounced StorageReportsChanged event — no second FileSystemWatcher.
        // The shell wires this; only legacy tests that pass GameConfig without
        // the service fall back to the local watcher.
        if (_gameReports is not null)
        {
            _gameReports.StorageReportsChanged += OnGameReportsStorageChanged;
            return;
        }

        if (_gameConfig is null) return;
        var dir = _gameConfig.ReportsDirectory;
        if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir)) return;

        try
        {
            _seedWatcher = new FileSystemWatcher(dir)
            {
                Filter = "*.json",
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.CreationTime | NotifyFilters.Size,
                EnableRaisingEvents = true,
            };
            _seedWatcher.Created += OnSeedReportsChanged;
            _seedWatcher.Changed += OnSeedReportsChanged;
            _seedWatcher.Renamed += OnSeedReportsChanged;
        }
        catch (Exception ex)
        {
            _diag?.Warn("GameState.Inventory", $"Seed: watcher setup failed: {ex.Message}");
        }
    }

    private void OnGameReportsStorageChanged(object? sender, EventArgs e)
    {
        // IGameReportsService's own watcher already debounces, so we reconcile
        // immediately on its notification — no second debounce timer here.
        try { LoadExportSeeds(); }
        catch (Exception ex) { _diag?.Warn("GameState.Inventory", $"Seed: refresh failed: {ex.Message}"); }
    }

    private void OnSeedReportsChanged(object sender, FileSystemEventArgs e)
    {
        // Debounce chunked writes — PG can flush an export across multiple ticks.
        // The 500 ms window matches ActiveCharacterService's own watcher.
        var prev = Interlocked.Exchange(ref _seedDebounce,
            new Timer(_ =>
            {
                try { LoadExportSeeds(); }
                catch (Exception ex) { _diag?.Warn("GameState.Inventory", $"Seed: refresh failed: {ex.Message}"); }
            }, null, TimeSpan.FromMilliseconds(500), Timeout.InfiniteTimeSpan));
        prev?.Dispose();
    }

    /// <summary>
    /// Resolve a chat display name (e.g. <c>"Egg"</c>) to an InternalName
    /// (e.g. <c>"BirdEgg"</c>) via the reference data items table. Linear scan
    /// over ~5k items per chat line; acceptable given chat status frequency.
    /// </summary>
    private bool TryResolveDisplayNameToInternalName(string displayName, out string internalName)
    {
        if (_refData is null) { internalName = ""; return false; }
        foreach (var item in _refData.ItemsByInternalName.Values)
        {
            if (string.Equals(item.Name, displayName, StringComparison.Ordinal)
                && !string.IsNullOrEmpty(item.InternalName))
            {
                internalName = item.InternalName!;
                return true;
            }
        }
        internalName = "";
        return false;
    }

    /// <summary>
    /// MUST be called with <see cref="_subLock"/> held. Appends to the
    /// React-channel event log (#585) and dispatches to every currently
    /// attached handler. Holding the lock around both is what makes the
    /// Subscribe-vs-live-event race impossible: a new subscriber either ran
    /// its replay before this Fire (and will receive the live event because
    /// the handler is in <see cref="_handlers"/>) or runs after (and saw
    /// the event in its replay of <see cref="_eventLog"/>).
    /// </summary>
    private void Fire(InventoryEvent evt)
    {
        AppendToEventLog(evt);
        foreach (var h in _handlers) Invoke(h, evt);
    }

    /// <summary>
    /// MUST be called with <see cref="_subLock"/> held. Appends an event to
    /// the React-channel log, enforcing the soft cap by dropping oldest
    /// entries in <see cref="EventLogTrimChunk"/>-sized chunks. The first
    /// time the cap is exceeded a single <c>Warn</c> fires so the situation
    /// surfaces in diagnostics rather than silently truncating session
    /// history. Subsequent overflows are bounded but not logged again to
    /// avoid noise in pathological sessions.
    /// </summary>
    private void AppendToEventLog(InventoryEvent evt)
    {
        if (_eventLog.Count >= EventLogSoftCap)
        {
            // Trim a chunk from the head. RemoveRange is O(n) on List<T>, but
            // amortizes to O(1) per Append once Count rounds the cap because
            // we trim a chunk at a time, not one element at a time.
            var trim = Math.Min(EventLogTrimChunk, _eventLog.Count);
            _eventLog.RemoveRange(0, trim);
            if (!_eventLogOverflowWarned)
            {
                _eventLogOverflowWarned = true;
                _diag?.Warn("GameState.Inventory",
                    $"React-channel event log exceeded soft cap ({EventLogSoftCap}); " +
                    $"dropping oldest entries. Late subscribers will see a bounded session history. " +
                    $"This is the first overflow this session; further overflows are silent.");
            }
        }
        _eventLog.Add(evt);
    }

    private void Invoke(Action<InventoryEvent> handler, InventoryEvent evt)
    {
        try { handler(evt); }
        catch (Exception ex) { _diag?.Warn("GameState.Inventory", $"Subscriber threw: {ex.Message}"); }
    }

    /// <summary>
    /// Test hook — returns the live entry counts of the two pending correlators
    /// (post-piggyback-drain if the caller has just driven an event). Used by
    /// <c>InventoryServiceTests</c> to pin the leak fix; not for production use.
    /// </summary>
    internal (int Chat, int Add) PendingCounts()
    {
        lock (_subLock)
        {
            return (_pendingChat.Count, _pendingAdd.Count);
        }
    }

    private sealed class Subscription : IDisposable
    {
        private InventoryService? _owner;
        private readonly Action<InventoryEvent> _handler;

        public Subscription(InventoryService owner, Action<InventoryEvent> handler)
        {
            _owner = owner;
            _handler = handler;
        }

        public void Dispose()
        {
            var owner = Interlocked.Exchange(ref _owner, null);
            if (owner is null) return;
            lock (owner._subLock) { owner._handlers.Remove(_handler); }
        }
    }
}
