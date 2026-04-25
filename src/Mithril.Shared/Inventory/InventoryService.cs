using System.Text.RegularExpressions;
using Mithril.Shared.Collections;
using Mithril.Shared.Diagnostics;
using Mithril.Shared.Logging;
using Mithril.Shared.Reference;
using Microsoft.Extensions.Hosting;

namespace Mithril.Shared.Inventory;

/// <summary>
/// Eagerly subscribes to <see cref="IPlayerLogStream"/> at shell startup and
/// maintains the canonical <c>instanceId → (InternalName, StackSize)</c> map.
/// The stream's session-replay buffer guarantees that the initial flush of
/// <c>ProcessAddItem</c> events is observed here regardless of subscriber
/// ordering; modules that need inventory lookups should depend on
/// <see cref="IInventoryService"/> rather than re-parsing the log.
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
///   <item>Chat <c>[Status] X [xN] added to inventory.</c> via
///   <see cref="IChatLogStream"/> carries the count for fresh additions —
///   correlated to <c>ProcessAddItem</c> by InternalName + arrival window.</item>
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

    private readonly IPlayerLogStream _stream;
    private readonly IChatLogStream? _chatStream;
    private readonly IReferenceDataService? _refData;
    private readonly IDiagnosticsSink? _diag;
    private readonly TimeProvider _time;

    // _subLock guards _map, _handlers, _pendingChat, and _pendingAdd. Both ingestion
    // loops (player log and chat) take it while mutating shared state.
    private readonly object _subLock = new();
    private readonly Dictionary<long, MapEntry> _map = new();
    private readonly List<Action<InventoryEvent>> _handlers = new();

    // Bidirectional chat/AddItem correlation. Either signal can arrive first within
    // the same second:
    //   - chat first  → enqueue to _pendingChat[InternalName]; AddItem dequeues.
    //   - AddItem first → enqueue to _pendingAdd[InternalName] with the InstanceId;
    //                     chat dequeues and back-fills _map[InstanceId].StackSize.
    // Each TtlList owns its own enqueue timestamps and lazy-evicts on access; the
    // piggyback drain in DrainPendingStale closes the leak where an entry that
    // never matched its counterpart would sit forever (under the prior queue
    // design, only the matching path consulted the TTL).
    private readonly Dictionary<string, TtlList<int>> _pendingChat = new(StringComparer.Ordinal);
    private readonly Dictionary<string, TtlList<long>> _pendingAdd = new(StringComparer.Ordinal);

    private readonly record struct MapEntry(string InternalName, DateTime Timestamp, bool Deleted, int StackSize);

    public InventoryService(
        IPlayerLogStream stream,
        IDiagnosticsSink? diag = null,
        IChatLogStream? chatStream = null,
        IReferenceDataService? refData = null,
        TimeProvider? time = null)
    {
        _stream = stream;
        _diag = diag;
        _chatStream = chatStream;
        _refData = refData;
        _time = time ?? TimeProvider.System;
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
            if (_map.TryGetValue(instanceId, out var entry) && entry.StackSize > 0)
            {
                stackSize = entry.StackSize;
                return true;
            }
        }
        stackSize = 0;
        return false;
    }

    public IDisposable Subscribe(Action<InventoryEvent> handler)
    {
        ArgumentNullException.ThrowIfNull(handler);
        lock (_subLock)
        {
            // Replay current live map state to this handler only. Skip
            // entries marked Deleted — they're retained for TryResolve but
            // shouldn't surface as Added events to a fresh subscriber.
            foreach (var (id, entry) in _map)
            {
                if (entry.Deleted) continue;
                Invoke(handler, new InventoryEvent(
                    InventoryEventKind.Added, id, entry.InternalName, entry.Timestamp, entry.StackSize));
            }
            _handlers.Add(handler);
            return new Subscription(this, handler);
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _diag?.Info("Inventory", "Subscribing to Player.log for inventory events");
        var playerTask = ConsumePlayerLogAsync(stoppingToken);

        if (_chatStream is not null)
        {
            _diag?.Info("Inventory", "Subscribing to ChatLogs for [Status] correlation");
            var chatTask = ConsumeChatLogAsync(stoppingToken);
            await Task.WhenAll(playerTask, chatTask).ConfigureAwait(false);
        }
        else
        {
            // No chat stream registered (test path); player log alone still tracks
            // sizes via UpdateItemCode and RemoveFromStorageVault — just no
            // chat-correlated AddItem sizing.
            await playerTask.ConfigureAwait(false);
        }
    }

    private async Task ConsumePlayerLogAsync(CancellationToken ct)
    {
        await foreach (var raw in _stream.SubscribeAsync(ct).ConfigureAwait(false))
        {
            var add = AddItemRx().Match(raw.Line);
            if (add.Success && long.TryParse(add.Groups[2].ValueSpan, out var addId))
            {
                HandleAddItem(addId, add.Groups[1].Value, raw.Timestamp);
                continue;
            }

            var del = DeleteItemRx().Match(raw.Line);
            if (del.Success && long.TryParse(del.Groups[1].ValueSpan, out var delId))
            {
                HandleDeleteItem(delId, raw.Timestamp);
                continue;
            }

            var upd = UpdateItemCodeRx().Match(raw.Line);
            if (upd.Success
                && long.TryParse(upd.Groups[1].ValueSpan, out var updId)
                && long.TryParse(upd.Groups[2].ValueSpan, out var code))
            {
                HandleUpdateItemCode(updId, code, raw.Timestamp);
                continue;
            }

            var vault = RemoveFromStorageVaultRx().Match(raw.Line);
            if (vault.Success
                && long.TryParse(vault.Groups[1].ValueSpan, out var vaultId)
                && int.TryParse(vault.Groups[2].ValueSpan, out var vaultSize))
            {
                HandleRemoveFromStorageVault(vaultId, vaultSize, raw.Timestamp);
            }
        }
    }

    private async Task ConsumeChatLogAsync(CancellationToken ct)
    {
        if (_chatStream is null) return;
        await foreach (var raw in _chatStream.SubscribeAsync(ct).ConfigureAwait(false))
        {
            var parsed = InventoryStatusChatParser.TryParse(raw.Line);
            if (parsed is null) continue;
            HandleChatStatusAdd(parsed.Value.DisplayName, parsed.Value.Count, raw.Timestamp);
        }
    }

    private void HandleAddItem(long instanceId, string internalName, DateTime timestamp)
    {
        lock (_subLock)
        {
            DrainPendingStale();

            // Try the pending-chat queue first — chat may have arrived ahead of us.
            int size = 1;
            if (TryDequeuePendingChat(internalName, out var pendingCount))
            {
                size = pendingCount;
            }
            else
            {
                // No chat yet; remember this InstanceId so a later chat status can
                // back-fill the size.
                EnqueuePendingAdd(internalName, instanceId);
            }

            _map[instanceId] = new MapEntry(internalName, timestamp, Deleted: false, StackSize: size);
            _diag?.Trace("Inventory", $"Add    id={instanceId} name={internalName} size={size} (total={_map.Count})");
            Fire(new InventoryEvent(InventoryEventKind.Added, instanceId, internalName, timestamp, size));
        }
    }

    private void HandleDeleteItem(long instanceId, DateTime timestamp)
    {
        lock (_subLock)
        {
            DrainPendingStale();
            if (!_map.TryGetValue(instanceId, out var entry))
            {
                _diag?.Trace("Inventory", $"Delete id={instanceId} — not in map, ignored");
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
            _diag?.Trace("Inventory", $"Delete id={instanceId} name={entry.InternalName} size={entry.StackSize} (retained)");
            Fire(new InventoryEvent(InventoryEventKind.Deleted, instanceId, entry.InternalName, timestamp, entry.StackSize));
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
                _diag?.Trace("Inventory", $"UpdateCode id={instanceId} size={newSize} — not in map, ignored");
                return;
            }
            if (entry.StackSize == newSize) return;
            _map[instanceId] = entry with { StackSize = newSize, Timestamp = timestamp };
            _diag?.Trace("Inventory", $"UpdateCode id={instanceId} name={entry.InternalName} size={newSize}");
            Fire(new InventoryEvent(InventoryEventKind.StackChanged, instanceId, entry.InternalName, timestamp, newSize));
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
            if (entry.StackSize == stackSize) return;

            _map[instanceId] = entry with { StackSize = stackSize, Timestamp = timestamp };
            _diag?.Trace("Inventory", $"RemoveFromVault id={instanceId} name={entry.InternalName} size={stackSize}");
            Fire(new InventoryEvent(InventoryEventKind.StackChanged, instanceId, entry.InternalName, timestamp, stackSize));
        }
    }

    private void HandleChatStatusAdd(string displayName, int count, DateTime timestamp)
    {
        if (count <= 0) return;
        if (_refData is null) return; // can't resolve display→internal without reference data
        if (!TryResolveDisplayNameToInternalName(displayName, out var internalName)) return;

        lock (_subLock)
        {
            DrainPendingStale();
            // Try to back-fill a recent AddItem that defaulted to size = 1.
            if (TryDequeuePendingAdd(internalName, out var instanceId))
            {
                if (_map.TryGetValue(instanceId, out var entry) && entry.StackSize != count)
                {
                    _map[instanceId] = entry with { StackSize = count, Timestamp = timestamp };
                    _diag?.Trace("Inventory", $"Chat → Add id={instanceId} name={internalName} size={count}");
                    Fire(new InventoryEvent(InventoryEventKind.StackChanged, instanceId, internalName, timestamp, count));
                }
                return;
            }

            // No matching pending AddItem; remember this count for the next AddItem of the
            // same InternalName.
            EnqueuePendingChat(internalName, count);
        }
    }

    /// <summary>MUST be called with <see cref="_subLock"/> held.</summary>
    private void EnqueuePendingChat(string internalName, int count)
    {
        if (!_pendingChat.TryGetValue(internalName, out var list))
        {
            list = new TtlList<int>(PendingChatTtl, _time);
            _pendingChat[internalName] = list;
        }
        list.Add(count);
    }

    /// <summary>MUST be called with <see cref="_subLock"/> held.</summary>
    private bool TryDequeuePendingChat(string internalName, out int count)
    {
        if (_pendingChat.TryGetValue(internalName, out var list) && list.TryRemoveOldest(out count))
            return true;
        count = 0;
        return false;
    }

    /// <summary>MUST be called with <see cref="_subLock"/> held.</summary>
    private void EnqueuePendingAdd(string internalName, long instanceId)
    {
        if (!_pendingAdd.TryGetValue(internalName, out var list))
        {
            list = new TtlList<long>(PendingChatTtl, _time);
            _pendingAdd[internalName] = list;
        }
        list.Add(instanceId);
    }

    /// <summary>MUST be called with <see cref="_subLock"/> held.</summary>
    private bool TryDequeuePendingAdd(string internalName, out long instanceId)
    {
        if (_pendingAdd.TryGetValue(internalName, out var list) && list.TryRemoveOldest(out instanceId))
            return true;
        instanceId = 0;
        return false;
    }

    /// <summary>
    /// Lazy piggyback drain — call from every event handler under <see cref="_subLock"/>.
    /// Walks both pending dictionaries, evicts each <see cref="TtlList{T}"/>'s stale
    /// entries, and removes empty list buckets. Cost is O(distinct InternalNames in
    /// pending), trivial in practice (rarely more than a handful at a time).
    /// </summary>
    private void DrainPendingStale()
    {
        DrainOne(_pendingChat);
        DrainOne(_pendingAdd);

        static void DrainOne<T>(Dictionary<string, TtlList<T>> bucket)
        {
            if (bucket.Count == 0) return;
            List<string>? empties = null;
            foreach (var (key, list) in bucket)
            {
                list.DropStale();
                if (list.Count == 0) (empties ??= new()).Add(key);
            }
            if (empties is not null)
                foreach (var k in empties) bucket.Remove(k);
        }
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
            if (string.Equals(item.Name, displayName, StringComparison.Ordinal))
            {
                internalName = item.InternalName;
                return true;
            }
        }
        internalName = "";
        return false;
    }

    /// <summary>
    /// MUST be called with <see cref="_subLock"/> held. Dispatches an event to
    /// every currently-attached handler. Holding the lock during dispatch is
    /// what makes the Subscribe-vs-live-event race impossible: a new
    /// subscriber either ran its replay before this Fire (and will receive
    /// the live event) or runs after (and saw the entry in its replay).
    /// </summary>
    private void Fire(InventoryEvent evt)
    {
        foreach (var h in _handlers) Invoke(h, evt);
    }

    private void Invoke(Action<InventoryEvent> handler, InventoryEvent evt)
    {
        try { handler(evt); }
        catch (Exception ex) { _diag?.Warn("Inventory", $"Subscriber threw: {ex.Message}"); }
    }

    /// <summary>
    /// Test hook — returns the live entry counts of the two pending dictionaries
    /// (post-piggyback-drain if the caller has just driven an event). Used by
    /// <c>InventoryServiceTests</c> to pin the leak fix; not for production use.
    /// </summary>
    internal (int Chat, int Add) PendingCounts()
    {
        lock (_subLock)
        {
            int chatTotal = 0;
            foreach (var list in _pendingChat.Values) chatTotal += list.Count;
            int addTotal = 0;
            foreach (var list in _pendingAdd.Values) addTotal += list.Count;
            return (chatTotal, addTotal);
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
