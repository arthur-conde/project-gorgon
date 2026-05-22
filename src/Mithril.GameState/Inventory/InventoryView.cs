using System.IO;
using Mithril.GameReports;
using Mithril.GameState.Sessions;
using Mithril.Shared.Correlation;
using Mithril.Shared.Diagnostics;
using Mithril.Shared.Logging;
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
/// <para><b>Two consumer surfaces.</b>
/// <list type="bullet">
///   <item><b>Typed bus</b> — the canonical post-migration surface. New code
///   subscribes via <c>view.Bus.Subscribe&lt;InventoryItemAdded&gt;(...)</c>
///   etc.</item>
///   <item><b>Legacy union-shaped <c>Subscribe(Action&lt;InventoryEvent&gt;)</c></b>
///   — preserved for the six pre-#602 consumers (Arwen, Samwise, Palantir,
///   Legolas, Saruman, Motherlode). The view owns its own event log + handler
///   list (replacing what <c>InventoryService</c> previously owned), so the
///   late-subscribe atomic-replay contract (#585) survives unchanged. Migration
///   of each consumer to the typed bus is tracked in #659.</item>
/// </list>
/// </para>
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
/// <para><b>What this PR retires.</b> The legacy <c>InventoryService</c>
/// L1-direct subscriptions to <c>LocalPlayerLogLine</c> and <c>RawLogLine</c>
/// retire entirely. The class survives only as a thin wrapper that resolves
/// to this view (so the existing <see cref="IInventoryService"/> DI binding
/// stays valid for the six pre-#602 consumers). FSW reconcile retired per
/// #612 — <see cref="IGameReportsService.StorageReportsChanged"/> is the sole
/// seed-refresh signal.</para>
/// </summary>
public sealed class InventoryView : IInventoryView, IInventoryService, IDisposable
{
    private static readonly TimeSpan PendingChatTtl = TimeSpan.FromSeconds(5);

    private readonly IPlayerWorld _playerWorld;
    private readonly IChatWorld _chatWorld;
    private readonly IPlayerInventoryState _playerState;
    private readonly IReferenceDataService? _refData;
    private readonly IGameSessionService? _playerSession;
    private readonly IChatSessionService? _chatSession;
    private readonly IGameReportsService? _gameReports;
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

    // _stateLock guards _map, _shimHandlers, _eventLog, _eventLogOverflowWarned,
    // _seededStackSizes. The correlators have their own internal locks; we still
    // take _stateLock around correlator calls so the drain-then-mutate-_map
    // sequence inside each handler is atomic. Two independent world-bus
    // subscriptions dispatch on different threads, so _stateLock is also
    // load-bearing for cross-source serialization.
    private readonly object _stateLock = new();
    private readonly Dictionary<long, MapEntry> _map = new();

    // Legacy union-shaped subscriber list — same atomic-replay shape the
    // pre-#602 service offered (#585 contract). The six pre-#602 consumers
    // subscribe here through the IInventoryService shim; #659 follow-on PRs
    // migrate each to the typed bus.
    private readonly List<Action<InventoryEvent>> _shimHandlers = new();

    private const int EventLogSoftCap = 50_000;
    private const int EventLogTrimChunk = 4_096;
    private readonly List<InventoryEvent> _eventLog = new();
    private bool _eventLogOverflowWarned;

    // One-shot SinceSubscribe coercion diag — same as the pre-split service.
    private static int s_sinceSubscribeDiagFired;

    private readonly record struct MapEntry(string InternalName, DateTime Timestamp, bool Deleted, int StackSize, bool SizeConfirmed);

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
        IReferenceDataService? refData = null,
        IGameSessionService? playerSession = null,
        IChatSessionService? chatSession = null,
        IGameReportsService? gameReports = null,
        IDiagnosticsSink? diag = null)
    {
        _playerWorld = playerWorld ?? throw new ArgumentNullException(nameof(playerWorld));
        _chatWorld = chatWorld ?? throw new ArgumentNullException(nameof(chatWorld));
        _playerState = playerState ?? throw new ArgumentNullException(nameof(playerState));
        _refData = refData;
        _playerSession = playerSession;
        _chatSession = chatSession;
        _gameReports = gameReports;
        _diag = diag;
        _clock = new ViewClock();
        _pendingChat = new PendingCorrelator<ScopedKey, int>(PendingChatTtl, _clock);
        _pendingAdd = new PendingCorrelator<ScopedKey, long>(PendingChatTtl, _clock);
    }

    public IWorldEventBus Bus => _bus;

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
        if (_gameReports is not null)
        {
            _gameReports.StorageReportsChanged += OnGameReportsStorageChanged;
        }

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

    public IDisposable Subscribe(
        Action<InventoryEvent> handler,
        ReplayMode replay = ReplayMode.FromSessionStart)
    {
        ArgumentNullException.ThrowIfNull(handler);
        if (replay == ReplayMode.SinceSubscribe
            && Interlocked.CompareExchange(ref s_sinceSubscribeDiagFired, 1, 0) == 0)
        {
            _diag?.Trace("GameState.Inventory.View",
                "ReplayMode.SinceSubscribe is not yet implemented; treating as LiveOnly. " +
                "This diagnostic fires once per process.");
        }
        lock (_stateLock)
        {
            if (replay == ReplayMode.FromSessionStart)
            {
                foreach (var evt in _eventLog) InvokeShim(handler, evt);
            }
            _shimHandlers.Add(handler);
            return new ShimSubscription(this, handler);
        }
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

            _map[evt.InstanceId] = new MapEntry(
                evt.InternalName, evt.Timestamp, Deleted: false, StackSize: size, SizeConfirmed: confirmed);
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
        if (_gameReports is null) return;

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
        FireShim(new InventoryEvent(InventoryEventKind.Added, instanceId, internalName, timestamp, size, confirmed));
    }

    private void FireRemoved(long instanceId, string internalName, DateTime timestamp, int size, bool confirmed)
    {
        var typed = new InventoryItemRemoved(instanceId, internalName, size, confirmed, timestamp);
        _bus.Publish(new Frame<InventoryItemRemoved>(new DateTimeOffset(timestamp, TimeSpan.Zero), typed));
        FireShim(new InventoryEvent(InventoryEventKind.Deleted, instanceId, internalName, timestamp, size, confirmed));
    }

    private void FireStackChanged(long instanceId, string internalName, DateTime timestamp, int size, bool sizeConfirmed)
    {
        var typed = new InventoryStackChanged(instanceId, internalName, size, sizeConfirmed, timestamp);
        _bus.Publish(new Frame<InventoryStackChanged>(new DateTimeOffset(timestamp, TimeSpan.Zero), typed));
        FireShim(new InventoryEvent(InventoryEventKind.StackChanged, instanceId, internalName, timestamp, size, sizeConfirmed));
    }

    /// <summary>MUST hold <see cref="_stateLock"/>.</summary>
    private void FireShim(InventoryEvent evt)
    {
        AppendToEventLog(evt);
        foreach (var h in _shimHandlers) InvokeShim(h, evt);
    }

    private void AppendToEventLog(InventoryEvent evt)
    {
        if (_eventLog.Count >= EventLogSoftCap)
        {
            var trim = Math.Min(EventLogTrimChunk, _eventLog.Count);
            _eventLog.RemoveRange(0, trim);
            if (!_eventLogOverflowWarned)
            {
                _eventLogOverflowWarned = true;
                _diag?.Warn("GameState.Inventory.View",
                    $"React-channel event log exceeded soft cap ({EventLogSoftCap}); dropping oldest entries.");
            }
        }
        _eventLog.Add(evt);
    }

    private void InvokeShim(Action<InventoryEvent> handler, InventoryEvent evt)
    {
        try { handler(evt); }
        catch (Exception ex) { _diag?.Warn("GameState.Inventory.View", $"Subscriber threw: {ex.Message}"); }
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
        if (_gameReports is not null)
        {
            _gameReports.StorageReportsChanged -= OnGameReportsStorageChanged;
        }
    }

    private sealed class ShimSubscription : IDisposable
    {
        private InventoryView? _owner;
        private readonly Action<InventoryEvent> _handler;

        public ShimSubscription(InventoryView owner, Action<InventoryEvent> handler)
        {
            _owner = owner;
            _handler = handler;
        }

        public void Dispose()
        {
            var owner = Interlocked.Exchange(ref _owner, null);
            if (owner is null) return;
            lock (owner._stateLock) { owner._shimHandlers.Remove(_handler); }
        }
    }
}
