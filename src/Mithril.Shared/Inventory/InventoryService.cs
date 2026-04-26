using System.IO;
using System.Text.RegularExpressions;
using Mithril.Shared.Collections;
using Mithril.Shared.Diagnostics;
using Mithril.Shared.Game;
using Mithril.Shared.Logging;
using Mithril.Shared.Reference;
using Mithril.Shared.Storage;
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
    private readonly GameConfig? _gameConfig;
    private readonly TimeProvider _time;
    private FileSystemWatcher? _seedWatcher;
    private Timer? _seedDebounce;

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
        IPlayerLogStream stream,
        IDiagnosticsSink? diag = null,
        IChatLogStream? chatStream = null,
        IReferenceDataService? refData = null,
        GameConfig? gameConfig = null,
        TimeProvider? time = null)
    {
        _stream = stream;
        _diag = diag;
        _chatStream = chatStream;
        _refData = refData;
        _gameConfig = gameConfig;
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
                    InventoryEventKind.Added, id, entry.InternalName, entry.Timestamp, entry.StackSize, entry.SizeConfirmed));
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
        finally
        {
            _seedDebounce?.Dispose();
            _seedWatcher?.Dispose();
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
            bool seeded = false;
            bool confirmed = false;
            if (TryDequeuePendingChat(internalName, out var pendingCount))
            {
                size = pendingCount;
                confirmed = true;
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
                EnqueuePendingAdd(internalName, instanceId);
            }
            else
            {
                // No chat yet, no export seed; remember this InstanceId so a later
                // chat status can back-fill the size. Size remains the unconfirmed
                // default — TryGetStackSize will report unknown until a confirming
                // event lands (chat back-fill, UpdateItemCode, vault, reconcile).
                EnqueuePendingAdd(internalName, instanceId);
            }

            _map[instanceId] = new MapEntry(internalName, timestamp, Deleted: false, StackSize: size, SizeConfirmed: confirmed);
            _diag?.Trace("Inventory", $"Add    id={instanceId} name={internalName} size={size}{(seeded ? " (export-seeded)" : confirmed ? " (chat)" : " (unconfirmed)")} (total={_map.Count})");
            Fire(new InventoryEvent(InventoryEventKind.Added, instanceId, internalName, timestamp, size, confirmed));
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
                _diag?.Trace("Inventory", $"UpdateCode id={instanceId} size={newSize} — not in map, ignored");
                return;
            }
            // UpdateCode is authoritative for the post-event size, so confirmation
            // flips on regardless of whether the size value changed (a "1 → 1"
            // UpdateCode for a previously-defaulted entry still moves it from
            // unconfirmed to confirmed). Only suppress the StackChanged fire when
            // both size and confirmation status match what's already there.
            if (entry.StackSize == newSize && entry.SizeConfirmed) return;
            _map[instanceId] = entry with { StackSize = newSize, Timestamp = timestamp, SizeConfirmed = true };
            _diag?.Trace("Inventory", $"UpdateCode id={instanceId} name={entry.InternalName} size={newSize}");
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
            _diag?.Trace("Inventory", $"RemoveFromVault id={instanceId} name={entry.InternalName} size={stackSize}");
            Fire(new InventoryEvent(InventoryEventKind.StackChanged, instanceId, entry.InternalName, timestamp, stackSize, SizeConfirmed: true));
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
                if (_map.TryGetValue(instanceId, out var entry)
                    && (entry.StackSize != count || !entry.SizeConfirmed))
                {
                    _map[instanceId] = entry with { StackSize = count, Timestamp = timestamp, SizeConfirmed = true };
                    _diag?.Trace("Inventory", $"Chat → Add id={instanceId} name={internalName} size={count}");
                    Fire(new InventoryEvent(InventoryEventKind.StackChanged, instanceId, internalName, timestamp, count, SizeConfirmed: true));
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
        if (_gameConfig is null || _refData is null) return;
        var dir = _gameConfig.ReportsDirectory;
        if (string.IsNullOrEmpty(dir)) return;

        ReportFileInfo? newest;
        StorageReport report;
        try
        {
            newest = StorageReportLoader.ScanForReports(dir).FirstOrDefault();
            if (newest is null)
            {
                _diag?.Trace("Inventory", $"Seed: no exports found in {dir}");
                return;
            }
            report = StorageReportLoader.Load(newest.FilePath);
        }
        catch (Exception ex)
        {
            _diag?.Warn("Inventory", $"Seed: failed to load export: {ex.Message}");
            return;
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

            counts[entry.InternalName] = counts.TryGetValue(entry.InternalName, out var c) ? c + 1 : 1;
            sizes[entry.InternalName] = item.StackSize;
        }

        lock (_subLock)
        {
            _seededStackSizes.Clear();
            foreach (var (name, count) in counts)
            {
                if (count == 1) _seededStackSizes[name] = sizes[name];
            }
            _diag?.Trace("Inventory", $"Seeded {_seededStackSizes.Count} stack sizes from {Path.GetFileName(newest.FilePath)}");

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
                _diag?.Trace("Inventory",
                    $"Export reconcile id={live.Id} name={name} size {live.Size} → {exportSize}{(live.Confirmed ? "" : " (confirming)")}");
                Fire(new InventoryEvent(InventoryEventKind.StackChanged, live.Id, name, now, exportSize, SizeConfirmed: true));
                reconciled++;
            }
            if (reconciled > 0)
                _diag?.Info("Inventory", $"Export reconciled {reconciled} live entries against {Path.GetFileName(newest.FilePath)}");
        }
    }

    private void SetupSeedWatcher()
    {
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
            _diag?.Warn("Inventory", $"Seed: watcher setup failed: {ex.Message}");
        }
    }

    private void OnSeedReportsChanged(object sender, FileSystemEventArgs e)
    {
        // Debounce chunked writes — PG can flush an export across multiple ticks.
        // The 500 ms window matches ActiveCharacterService's own watcher.
        var prev = Interlocked.Exchange(ref _seedDebounce,
            new Timer(_ =>
            {
                try { LoadExportSeeds(); }
                catch (Exception ex) { _diag?.Warn("Inventory", $"Seed: refresh failed: {ex.Message}"); }
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
