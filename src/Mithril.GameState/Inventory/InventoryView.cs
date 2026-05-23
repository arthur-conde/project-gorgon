using System.IO;
using Mithril.GameReports;
using Mithril.GameState.Sessions;
using Mithril.Shared.Collections;
using Mithril.Shared.Correlation;
using Mithril.Shared.Diagnostics;
using Mithril.Shared.Reference;
using Mithril.WorldSim;
using Mithril.WorldSim.Chat;
using Mithril.WorldSim.Player;

namespace Mithril.GameState.Inventory;

/// <summary>
/// Canonical inventory surface for modules (#602) — the cross-source view layer
/// that composes <see cref="IPlayerWorld"/>'s instance-id ledger with
/// <see cref="IChatWorld"/>'s stack-size observations. This is the
/// world-simulator architecture's answer to the principle 3 + principle 4
/// requirements: <b>no service spans both sources</b> and <b>cross-source
/// composition lives in a view layer above the worlds</b>
/// (<c>docs/world-simulator.md</c> §Worked example 1).
///
/// <para><b>Composition.</b> The view subscribes to typed change events on
/// both world buses:
/// <list type="bullet">
///   <item><see cref="PlayerInventoryAdded"/> / <see cref="PlayerInventoryRemoved"/>
///   / <see cref="PlayerInventoryStackUpdated"/> from <c>IPlayerWorld.Bus</c>.</item>
///   <item><see cref="ChatInventoryObserved"/> from <c>IChatWorld.Bus</c>.</item>
/// </list>
/// A view-layer <see cref="PendingCorrelator{TKey,TReq}"/> (Tier 1 per
/// <c>docs/cross-source-correlation.md</c>) pairs adds with matching chat
/// observations on key <c>(Server, Character, InternalName)</c> within a 5s
/// TTL. Scope mismatch (chat session and player session disagree) drops the
/// pair with a diagnostic.</para>
///
/// <para><b>View clock.</b> The correlator's <see cref="TimeProvider"/> is a
/// derived clock — <see cref="Now"/> returns the max of the most recently
/// observed timestamps on the two world buses. This makes the TTL gate
/// replay-deterministic: under replay the simulated clock advances by event
/// timestamps, NOT wall-clock, so a 5s window applies the same way whether
/// the replay drains in 100 ms wall-clock or 30 s. (Pre-#602 the legacy
/// service used <c>TimeProvider.System</c> for the same gate, breaking
/// determinism under replay.)</para>
///
/// <para><b>Three consumer surfaces (per the React / Query / Bind taxonomy
/// in <c>docs/module-charters.md</c>).</b>
/// <list type="bullet">
///   <item><b>React</b> — typed-frame bus. Subscribers attach via
///   <c>view.Bus.Subscribe&lt;InventoryItemAdded&gt;(…)</c> /
///   <c>InventoryItemRemoved</c> / <c>InventoryStackChanged</c>.</item>
///   <item><b>Query</b> — <see cref="TryResolve"/> /
///   <see cref="TryGetStackSize"/> on <see cref="IInventoryView"/>.</item>
///   <item><b>Bind</b> — <see cref="IInventoryView.Items"/>: a live
///   <see cref="IReadOnlyObservableCollection{InventoryItem}"/> for WPF
///   binding. Per-row mutations propagate via
///   <see cref="System.ComponentModel.INotifyPropertyChanged"/>; soft-delete
///   contract — removed rows stay in the collection with
///   <see cref="InventoryItem.IsDeleted"/> = <c>true</c>.</item>
/// </list>
/// The pre-#659 union-shaped <c>Subscribe(Action&lt;InventoryEvent&gt;)</c>
/// shim retired with that issue once all six pre-#602 consumers migrated to
/// their post-shim destinations (PlayerWorld-direct for Samwise/Legolas/Motherlode,
/// the Bind channel for Palantir, the Tier-2 <c>IGiftSignalService</c> for
/// Arwen, blueprint-only for Saruman).</para>
///
/// <para><b>Stack-size sources of truth.</b> The composed stack size is the
/// merge of:
/// <list type="bullet">
///   <item>Chat <c>[Status] X xN added</c> — paired with a matching Player.log
///   <c>ProcessAddItem</c> via the correlator. Authoritative when paired.</item>
///   <item>Reference-data <c>MaxStackSize == 1</c> — non-stackable items
///   confirm size = 1 on Add without needing chat.</item>
///   <item>Export seeds via <see cref="IGameReportsService"/> — carryover
///   instances whose original AddItem isn't in this session's replay buffer.</item>
///   <item><see cref="PlayerInventoryStackUpdated"/> — authoritative for
///   <c>ProcessUpdateItemCode</c> + <c>ProcessRemoveFromStorageVault</c>.</item>
/// </list>
/// Without any of these, an entry's <c>SizeConfirmed</c> stays false (the
/// unconfirmed default-1 — distinguishable from a real stack of 1 confirmed
/// via chat).</para>
///
/// <para><b>What #602 retired.</b> The pre-split <c>InventoryService</c>
/// L1-direct subscriptions to <c>LocalPlayerLogLine</c> and <c>RawLogLine</c>
/// retired entirely. The Query-only legacy <c>IInventoryService</c> interface
/// retired with the Arwen consumer migration — <c>CalibrationService</c> now
/// injects <see cref="IInventoryView"/> directly. FSW reconcile retired per #612 —
/// <see cref="IGameReportsService.StorageReportsChanged"/> is the sole
/// seed-refresh signal.</para>
/// </summary>
public sealed class InventoryView : IInventoryView, IDisposable
{
    private static readonly TimeSpan PendingChatTtl = TimeSpan.FromSeconds(5);

    private readonly IPlayerWorld _playerWorld;
    private readonly IChatWorld _chatWorld;
    private readonly IPlayerInventoryState _playerState;
    private readonly IGameReportsService _gameReports;
    private readonly IReferenceDataService? _refData;
    private readonly IGameSessionService? _playerSession;
    private readonly IChatSessionService? _chatSession;
    private readonly IDiagnosticsSink? _diag;
    private readonly ViewClock _clock;
    private readonly ViewEventBus _bus = new();

    private IDisposable? _playerAddedSub;
    private IDisposable? _playerRemovedSub;
    private IDisposable? _playerStackUpdatedSub;
    private IDisposable? _chatObservedSub;
    private bool _started;
    private bool _disposed;

    // Bidirectional correlator: chat-arrives-first stores the count keyed by
    // (Server, Character, InternalName) for a future Add to consume;
    // Add-arrives-first stores the instance id keyed by the same tuple for a
    // future chat observation to back-fill. See cross-source-correlation.md
    // §Tier 1 for the operational invariants — DrainStale discipline,
    // unmatched callback policy, monotonic-time invariant. Both correlators
    // are time-stamped via the view's derived clock (NOT TimeProvider.System);
    // this is what makes the TTL gate replay-deterministic.
    private readonly PendingCorrelator<ScopedKey, int> _pendingChat;
    private readonly PendingCorrelator<ScopedKey, long> _pendingAdd;

    // InternalName → authoritative stack size from the player's most recent
    // *_items_*.json export. Populated at startup and on Reports/ writes;
    // restricted to InternalNames that appear exactly once and have
    // MaxStackSize > 1.
    private readonly Dictionary<string, int> _seededStackSizes = new(StringComparer.Ordinal);

    // _stateLock guards _map, _seededStackSizes, _items. The correlators have
    // their own internal locks; we still take _stateLock around correlator
    // calls so the drain-then-mutate-_map sequence inside each handler is
    // atomic. Two independent world-bus subscriptions dispatch on different
    // threads, so _stateLock is also load-bearing for cross-source
    // serialization.
    //
    // _items is the WPF "Bind" surface (#729). Per-row InventoryItem state
    // (StackSize / SizeConfirmed / IsDeleted) mutates via the same code paths
    // that update _map; collection add/remove fires via _items's INotifyCollectionChanged.
    // Soft-delete: Removed entries stay in _items with IsDeleted = true (matching
    // _map's retention for Arwen's gift-attribution path). WPF consumers binding
    // from a non-dispatcher thread call BindingOperations.EnableCollectionSynchronization
    // on (_items, _stateLock).
    private readonly object _stateLock = new();
    private readonly Dictionary<long, MapEntry> _map = new();
    private readonly ObservableInventoryItems _items = new();

    // Item is the per-instance row exposed via the bindable _items surface.
    // The view drives its mutable bits (StackSize / SizeConfirmed / IsDeleted)
    // in lockstep with MapEntry's scalar fields so the two surfaces never
    // drift; the duplicate state is the price of giving WPF a row identity
    // that survives PropertyChanged notifications. MapEntry stays a struct so
    // dictionary mutations don't allocate.
    private readonly record struct MapEntry(string InternalName, DateTime Timestamp, bool Deleted, int StackSize, bool SizeConfirmed, InventoryItem Item);

    /// <summary>
    /// Correlator scope key: <c>(Server, Character, InternalName)</c>. The
    /// Server + Character pair is taken from <see cref="IGameSessionService"/>
    /// for Player.log observations and from <see cref="IChatSessionService"/>
    /// for chat observations. When the two disagree on the same frame, the
    /// pair candidate drops (no cross-character correlation hazard). When
    /// either session is null at cold start, the scope falls back to a single
    /// empty-scoped bucket so a single-session boot still pairs — sessions
    /// converge on the first banner within seconds.
    /// </summary>
    private readonly record struct ScopedKey(string Server, string Character, string InternalName);

    public InventoryView(
        IPlayerWorld playerWorld,
        IChatWorld chatWorld,
        IPlayerInventoryState playerState,
        IGameReportsService gameReports,
        IReferenceDataService? refData = null,
        IGameSessionService? playerSession = null,
        IChatSessionService? chatSession = null,
        IDiagnosticsSink? diag = null)
    {
        _playerWorld = playerWorld ?? throw new ArgumentNullException(nameof(playerWorld));
        _chatWorld = chatWorld ?? throw new ArgumentNullException(nameof(chatWorld));
        _playerState = playerState ?? throw new ArgumentNullException(nameof(playerState));
        _gameReports = gameReports ?? throw new ArgumentNullException(nameof(gameReports));
        _refData = refData;
        _playerSession = playerSession;
        _chatSession = chatSession;
        _diag = diag;
        _clock = new ViewClock();
        _pendingChat = new PendingCorrelator<ScopedKey, int>(PendingChatTtl, _clock);
        _pendingAdd = new PendingCorrelator<ScopedKey, long>(PendingChatTtl, _clock);
    }

    public IWorldEventBus Bus => _bus;

    public IReadOnlyObservableCollection<InventoryItem> Items => _items;

    public object ItemsSyncRoot => _stateLock;

    /// <summary>
    /// The view's derived clock — <c>Now</c> = <c>max(lastPlayerFrameTs,
    /// lastChatFrameTs)</c> over the most recently observed bus frames.
    /// Public for tests; production consumers use the bus.
    /// </summary>
    public IViewClock Clock => _clock;

    /// <summary>
    /// Attach to the two world buses + seed export reconcile. Idempotent —
    /// safe to call from multiple registration hosted services. The DI
    /// extension calls this once per process during host start.
    /// </summary>
    public void Start()
    {
        if (_started) return;
        _started = true;

        LoadExportSeeds();
        _gameReports.StorageReportsChanged += OnGameReportsStorageChanged;

        _playerAddedSub = _playerWorld.Bus.Subscribe<PlayerInventoryAdded>(OnPlayerAdded);
        _playerRemovedSub = _playerWorld.Bus.Subscribe<PlayerInventoryRemoved>(OnPlayerRemoved);
        _playerStackUpdatedSub = _playerWorld.Bus.Subscribe<PlayerInventoryStackUpdated>(OnPlayerStackUpdated);
        _chatObservedSub = _chatWorld.Bus.Subscribe<ChatInventoryObserved>(OnChatObserved);

        _diag?.Info("GameState.Inventory.View",
            "InventoryView subscribed to PlayerWorld + ChatWorld typed bus channels");
    }

    public bool TryResolve(long instanceId, out string internalName)
    {
        lock (_stateLock)
        {
            if (_map.TryGetValue(instanceId, out var entry))
            {
                internalName = entry.InternalName;
                return true;
            }
        }
        // Fall through to the PlayerWorld folder's ledger — covers ids
        // observed by the folder before the view attached (e.g. early
        // replay before our subscription handler ran).
        return _playerState.TryResolve(instanceId, out internalName);
    }

    public bool TryGetStackSize(long instanceId, out int stackSize)
    {
        lock (_stateLock)
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

    // ── PlayerWorld bus handlers ─────────────────────────────────────────

    private void OnPlayerAdded(Frame<PlayerInventoryAdded> frame)
    {
        _clock.AdvancePlayer(frame.Timestamp);
        var evt = frame.Payload;
        lock (_stateLock)
        {
            DrainPendingStale();

            // Re-emission pulse for an already-tracked alive entry — update
            // timestamp only; don't re-fire view events or burn a correlator
            // slot. The folder filters most of these, but the view must
            // defend independently because its own ledger could observe a
            // re-emit the folder elided.
            if (_map.TryGetValue(evt.InstanceId, out var existing) && !existing.Deleted)
            {
                _map[evt.InstanceId] = existing with { Timestamp = evt.Timestamp };
                return;
            }

            var key = MakePlayerKey(evt.InternalName);
            int size = 1;
            bool seeded = false;
            bool confirmed = false;
            bool nonStackable = false;

            if (_pendingChat.TryTake(key, out var pendingCount))
            {
                size = pendingCount;
                confirmed = true;
            }
            else if (TryGetMaxStackSize(evt.InternalName, out var maxStack) && maxStack == 1)
            {
                confirmed = true;
                nonStackable = true;
            }
            else if (_seededStackSizes.TryGetValue(evt.InternalName, out var seededSize))
            {
                size = seededSize;
                seeded = true;
                confirmed = true;
                _seededStackSizes.Remove(evt.InternalName);
                _pendingAdd.Add(key, evt.InstanceId);
            }
            else
            {
                _pendingAdd.Add(key, evt.InstanceId);
            }

            // Re-add of a previously-deleted instance id is rare (PG would
            // normally allocate a fresh id) but the legacy LiveInventoryViewModel
            // already handles it as an in-place reset. Reuse the existing row so
            // the bindable surface tracks the same identity instead of leaving
            // an orphan row + adding a duplicate.
            InventoryItem newItem;
            if (_map.TryGetValue(evt.InstanceId, out var reAddExisting))
            {
                newItem = reAddExisting.Item;
                newItem.StackSize = size;
                newItem.SizeConfirmed = confirmed;
                newItem.IsDeleted = false;
            }
            else
            {
                newItem = new InventoryItem(evt.InstanceId, evt.InternalName, size, confirmed);
                _items.AddItem(newItem);
            }
            _map[evt.InstanceId] = new MapEntry(
                evt.InternalName, evt.Timestamp, Deleted: false, StackSize: size, SizeConfirmed: confirmed, Item: newItem);
            var sourceTag = nonStackable ? " (non-stackable)"
                : seeded ? " (export-seeded)"
                : confirmed ? " (chat)"
                : " (unconfirmed)";
            _diag?.Trace("GameState.Inventory.View",
                $"Add    id={evt.InstanceId} name={evt.InternalName} size={size}{sourceTag} (total={_map.Count})");
            FireAdded(evt.InstanceId, evt.InternalName, evt.Timestamp, size, confirmed);
        }
    }

    private void OnPlayerRemoved(Frame<PlayerInventoryRemoved> frame)
    {
        _clock.AdvancePlayer(frame.Timestamp);
        var evt = frame.Payload;
        lock (_stateLock)
        {
            DrainPendingStale();
            if (!_map.TryGetValue(evt.InstanceId, out var entry))
            {
                _diag?.Trace("GameState.Inventory.View",
                    $"Remove id={evt.InstanceId} — not in view map, ignored");
                return;
            }
            if (entry.Deleted) return;
            _map[evt.InstanceId] = entry with { Deleted = true, Timestamp = evt.Timestamp };
            entry.Item.IsDeleted = true;
            _diag?.Trace("GameState.Inventory.View",
                $"Remove id={evt.InstanceId} name={entry.InternalName} (retained)");
            FireRemoved(evt.InstanceId, entry.InternalName, evt.Timestamp, entry.StackSize, entry.SizeConfirmed);
        }
    }

    private void OnPlayerStackUpdated(Frame<PlayerInventoryStackUpdated> frame)
    {
        _clock.AdvancePlayer(frame.Timestamp);
        var evt = frame.Payload;
        if (evt.StackSize <= 0) return;
        lock (_stateLock)
        {
            DrainPendingStale();
            if (!_map.TryGetValue(evt.InstanceId, out var entry)) return;
            if (entry.StackSize == evt.StackSize && entry.SizeConfirmed) return;
            _map[evt.InstanceId] = entry with
            {
                StackSize = evt.StackSize,
                Timestamp = evt.Timestamp,
                SizeConfirmed = true,
            };
            entry.Item.StackSize = evt.StackSize;
            entry.Item.SizeConfirmed = true;
            _diag?.Trace("GameState.Inventory.View",
                $"StackUpdate id={evt.InstanceId} name={entry.InternalName} size={evt.StackSize}");
            FireStackChanged(evt.InstanceId, entry.InternalName, evt.Timestamp, evt.StackSize, sizeConfirmed: true);
        }
    }

    // ── ChatWorld bus handlers ──────────────────────────────────────────

    private void OnChatObserved(Frame<ChatInventoryObserved> frame)
    {
        _clock.AdvanceChat(frame.Timestamp);
        var evt = frame.Payload;
        if (evt.Count <= 0) return;
        if (_refData is null) return;
        if (!TryResolveDisplayNameToInternalName(evt.DisplayName, out var internalName))
        {
            _diag?.Trace("GameState.Inventory.View",
                $"Chat status: '{evt.DisplayName}' (x{evt.Count}) — no InternalName match in reference data");
            return;
        }

        // Scope check: chat observation's (Server, Character) must agree with
        // the active Player.log session. Reading both sessions' Current at
        // the same frame; when either is null (cold start) fall back to the
        // empty-scope bucket so single-session boots still pair.
        var chatScope = _chatSession?.Current;
        var playerScope = _playerSession?.Current;
        if (chatScope is not null && playerScope is not null)
        {
            var playerServer = playerScope.Server?.Name ?? "";
            if (!string.Equals(chatScope.Character, playerScope.CharacterName, StringComparison.Ordinal)
                || !string.Equals(chatScope.Server, playerServer, StringComparison.Ordinal))
            {
                _diag?.Trace("GameState.Inventory.View",
                    $"Chat observation '{evt.DisplayName}' (x{evt.Count}) " +
                    $"scope=(server={chatScope.Server}, char={chatScope.Character}) " +
                    $"differs from player session (server={playerServer}, char={playerScope.CharacterName}); dropping pair candidate.");
                return;
            }
        }

        var key = MakeChatKey(internalName);

        lock (_stateLock)
        {
            DrainPendingStale();
            if (_pendingAdd.TryTake(key, out var instanceId))
            {
                if (_map.TryGetValue(instanceId, out var entry)
                    && (entry.StackSize != evt.Count || !entry.SizeConfirmed))
                {
                    _map[instanceId] = entry with
                    {
                        StackSize = evt.Count,
                        Timestamp = evt.Timestamp,
                        SizeConfirmed = true,
                    };
                    entry.Item.StackSize = evt.Count;
                    entry.Item.SizeConfirmed = true;
                    _diag?.Trace("GameState.Inventory.View",
                        $"Chat → Add id={instanceId} name={internalName} size={evt.Count}");
                    FireStackChanged(instanceId, internalName, evt.Timestamp, evt.Count, sizeConfirmed: true);
                }
                return;
            }
            _pendingChat.Add(key, evt.Count);
        }
    }

    // ── Helpers ─────────────────────────────────────────────────────────

    private ScopedKey MakePlayerKey(string internalName)
    {
        var s = _playerSession?.Current;
        return new ScopedKey(
            Server: s?.Server?.Name ?? "",
            Character: s?.CharacterName ?? "",
            InternalName: internalName);
    }

    private ScopedKey MakeChatKey(string internalName)
    {
        // The chat side keys on the PLAYER session for the cross-source join
        // (we've already scope-checked the chat session matches the player
        // session above). Using the player session uniformly avoids a
        // separate per-side key, which would race when the chat banner lands
        // microseconds before/after the player banner during simultaneous
        // login.
        return MakePlayerKey(internalName);
    }

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

    private void DrainPendingStale()
    {
        _pendingChat.DrainStale();
        _pendingAdd.DrainStale();
    }

    private void LoadExportSeeds()
    {
        if (_refData is null) return;

        var newest = _gameReports.StorageReports.FirstOrDefault();
        if (newest is null)
        {
            _diag?.Trace("GameState.Inventory.View", "Seed: no exports found via IGameReportsService");
            return;
        }
        var report = _gameReports.GetStorageContents(newest.Character, newest.Server);
        if (report is null)
        {
            _diag?.Warn("GameState.Inventory.View", $"Seed: IGameReportsService failed to parse {newest.FilePath}");
            return;
        }

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

        lock (_stateLock)
        {
            _seededStackSizes.Clear();
            foreach (var (name, count) in counts)
            {
                if (count == 1) _seededStackSizes[name] = sizes[name];
            }
            _diag?.Trace("GameState.Inventory.View",
                $"Seeded {_seededStackSizes.Count} stack sizes from {Path.GetFileName(newest.FilePath)}");

            // Non-stackable confirm pass — same shape as pre-split InventoryService.
            var nowNs = _clock.GetUtcNow().UtcDateTime;
            int nonStackConfirmed = 0;
            foreach (var (id, entry) in _map.ToArray())
            {
                if (entry.Deleted || entry.SizeConfirmed) continue;
                if (!_refData.ItemsByInternalName.TryGetValue(entry.InternalName, out var itemEntry)) continue;
                if (itemEntry.MaxStackSize != 1) continue;

                _map[id] = entry with { StackSize = 1, Timestamp = nowNs, SizeConfirmed = true };
                entry.Item.StackSize = 1;
                entry.Item.SizeConfirmed = true;
                FireStackChanged(id, entry.InternalName, nowNs, 1, sizeConfirmed: true);
                nonStackConfirmed++;
            }
            if (nonStackConfirmed > 0)
                _diag?.Info("GameState.Inventory.View",
                    $"Confirmed {nonStackConfirmed} non-stackable live entries from reference data");

            // Stackable single-instance reconcile pass.
            var liveSingle = new Dictionary<string, (long Id, int Size, bool Confirmed)>(StringComparer.Ordinal);
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
                    liveSingle[entry.InternalName] = (id, entry.StackSize, entry.SizeConfirmed);
                }
            }

            int reconciled = 0;
            foreach (var (name, count) in counts)
            {
                if (count != 1) continue;
                if (!liveSingle.TryGetValue(name, out var live)) continue;
                var exportSize = sizes[name];
                if (live.Size == exportSize && live.Confirmed) continue;

                var entry = _map[live.Id];
                _map[live.Id] = entry with { StackSize = exportSize, Timestamp = nowNs, SizeConfirmed = true };
                entry.Item.StackSize = exportSize;
                entry.Item.SizeConfirmed = true;
                FireStackChanged(live.Id, name, nowNs, exportSize, sizeConfirmed: true);
                reconciled++;
            }
            if (reconciled > 0)
                _diag?.Info("GameState.Inventory.View",
                    $"Export reconciled {reconciled} live entries against {Path.GetFileName(newest.FilePath)}");
        }
    }

    private void OnGameReportsStorageChanged(object? sender, EventArgs e)
    {
        try { LoadExportSeeds(); }
        catch (Exception ex) { _diag?.Warn("GameState.Inventory.View", $"Seed: refresh failed: {ex.Message}"); }
    }

    // ── Fire / publish helpers (MUST hold _stateLock) ──────────────────

    private void FireAdded(long instanceId, string internalName, DateTime timestamp, int size, bool confirmed)
    {
        var typed = new InventoryItemAdded(instanceId, internalName, size, confirmed, timestamp);
        _bus.Publish(new Frame<InventoryItemAdded>(new DateTimeOffset(timestamp, TimeSpan.Zero), typed));
    }

    private void FireRemoved(long instanceId, string internalName, DateTime timestamp, int size, bool confirmed)
    {
        var typed = new InventoryItemRemoved(instanceId, internalName, size, confirmed, timestamp);
        _bus.Publish(new Frame<InventoryItemRemoved>(new DateTimeOffset(timestamp, TimeSpan.Zero), typed));
    }

    private void FireStackChanged(long instanceId, string internalName, DateTime timestamp, int size, bool sizeConfirmed)
    {
        var typed = new InventoryStackChanged(instanceId, internalName, size, sizeConfirmed, timestamp);
        _bus.Publish(new Frame<InventoryStackChanged>(new DateTimeOffset(timestamp, TimeSpan.Zero), typed));
    }

    /// <summary>
    /// Test hook — pending-correlator entry counts (post-piggyback-drain if
    /// the caller has just driven an event).
    /// </summary>
    internal (int Chat, int Add) PendingCounts()
    {
        lock (_stateLock) { return (_pendingChat.Count, _pendingAdd.Count); }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _playerAddedSub?.Dispose();
        _playerRemovedSub?.Dispose();
        _playerStackUpdatedSub?.Dispose();
        _chatObservedSub?.Dispose();
        _gameReports.StorageReportsChanged -= OnGameReportsStorageChanged;
    }
}
