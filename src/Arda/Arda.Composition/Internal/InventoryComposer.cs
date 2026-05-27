using Arda.Composition.Events;
using Arda.Dispatch;
using Arda.World.Chat.Events;
using Arda.World.Player.Events;
using Microsoft.Extensions.Logging;
using Mithril.Shared.Character;

namespace Arda.Composition.Internal;

/// <summary>
/// L4 inventory composer. Two responsibilities:
/// <list type="number">
///   <item>Temporal correlation of <see cref="InventoryItemAdded"/> (Player.log) with
///   <see cref="ChatInventoryObserved"/> (ChatLog) to emit
///   <see cref="InventoryItemResolved"/>.</item>
///   <item>Persistent accumulator that retains soft-deleted items for downstream
///   consumers needing post-removal lookups (e.g. gift correlation).</item>
/// </list>
/// </summary>
internal sealed class InventoryComposer : IInventoryAccumulatorState, IDisposable
{
    private static readonly TimeSpan CorrelationWindow = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan RetentionPeriod = TimeSpan.FromDays(30);
    private const int MaxPending = 64;

    private readonly IDomainEventBus _bus;
    private readonly PerCharacterStore<AccumulatorSnapshot>? _store;
    private readonly ILogger? _logger;

    // ── Correlation state ─────────────────────────────────────────────────
    private readonly LinkedList<InventoryItemAdded> _pendingAdds = new();
    private readonly LinkedList<ChatInventoryObserved> _pendingChats = new();

    // ── Accumulator state ─────────────────────────────────────────────────
    private readonly Dictionary<long, AccumulatedItem> _items = new();
    private string? _currentCharacter;
    private string? _currentServer;

    // ── Subscriptions ─────────────────────────────────────────────────────
    private IDisposable? _addSub;
    private IDisposable? _chatSub;
    private IDisposable? _updatedSub;
    private IDisposable? _removedSub;
    private IDisposable? _resolvedSub;
    private IDisposable? _sessionSub;

    public IReadOnlyDictionary<long, AccumulatedItem> Items => _items;
    public event Action? StateChanged;

    public InventoryComposer(
        IDomainEventBus bus,
        PerCharacterStore<AccumulatorSnapshot>? store = null,
        ILogger? logger = null)
    {
        _bus = bus;
        _store = store;
        _logger = logger;

        _addSub = bus.Subscribe<InventoryItemAdded>(OnItemAdded);
        _chatSub = bus.Subscribe<ChatInventoryObserved>(OnChatObserved);
        _updatedSub = bus.Subscribe<InventoryItemUpdated>(OnItemUpdated);
        _removedSub = bus.Subscribe<InventoryItemRemoved>(OnItemRemoved);
        _resolvedSub = bus.Subscribe<InventoryItemResolved>(OnItemResolved);
        _sessionSub = bus.Subscribe<SessionEstablished>(OnSessionEstablished);
    }

    // ── Correlation (unchanged logic) ─────────────────────────────────────

    private void OnItemAdded(InventoryItemAdded added)
    {
        AccumulateAdd(added);

        var node = _pendingChats.First;
        while (node is not null)
        {
            var chat = node.Value;
            var delta = added.Metadata.ReadOn - chat.Metadata.ReadOn;
            if (delta.Duration() <= CorrelationWindow)
            {
                _pendingChats.Remove(node);
                _bus.Publish(new InventoryItemResolved(
                    added.InstanceId,
                    added.InternalName,
                    chat.DisplayName,
                    chat.Count,
                    added.Metadata));
                return;
            }

            node = node.Next;
        }

        _pendingAdds.AddLast(added);
        TrimPending(_pendingAdds, added.Metadata.ReadOn);
    }

    private void OnChatObserved(ChatInventoryObserved chat)
    {
        var node = _pendingAdds.First;
        while (node is not null)
        {
            var add = node.Value;
            var delta = chat.Metadata.ReadOn - add.Metadata.ReadOn;
            if (delta.Duration() <= CorrelationWindow)
            {
                _pendingAdds.Remove(node);
                _bus.Publish(new InventoryItemResolved(
                    add.InstanceId,
                    add.InternalName,
                    chat.DisplayName,
                    chat.Count,
                    add.Metadata));
                return;
            }

            node = node.Next;
        }

        _pendingChats.AddLast(chat);
        TrimPending(_pendingChats, chat.Metadata.ReadOn);
    }

    private void TrimPending<T>(LinkedList<T> list, DateTimeOffset currentReadOn) where T : struct
    {
        while (list.Count > MaxPending && list.First is { } overflow)
        {
            LogFifoEviction(overflow.Value, currentReadOn);
            list.RemoveFirst();
        }

        while (list.First is { } first)
        {
            var readOn = first.Value switch
            {
                InventoryItemAdded a => a.Metadata.ReadOn,
                ChatInventoryObserved c => c.Metadata.ReadOn,
                _ => currentReadOn
            };
            if (currentReadOn - readOn > CorrelationWindow)
                list.RemoveFirst();
            else
                break;
        }
    }

    private void LogFifoEviction<T>(T evicted, DateTimeOffset currentReadOn) where T : struct
    {
        if (_logger is null) return;
        switch (evicted)
        {
            case InventoryItemAdded a:
                _logger.LogWarning(
                    "Dropping uncorrelated InventoryItemAdded {InstanceId} ({InternalName}) after {AgeMs} ms — MaxPending={MaxPending} reached",
                    a.InstanceId, a.InternalName,
                    (long)(currentReadOn - a.Metadata.ReadOn).TotalMilliseconds, MaxPending);
                break;
            case ChatInventoryObserved c:
                _logger.LogWarning(
                    "Dropping uncorrelated ChatInventoryObserved {DisplayName} x{Count} after {AgeMs} ms — MaxPending={MaxPending} reached",
                    c.DisplayName, c.Count,
                    (long)(currentReadOn - c.Metadata.ReadOn).TotalMilliseconds, MaxPending);
                break;
        }
    }

    // ── Accumulator ───────────────────────────────────────────────────────

    private void AccumulateAdd(InventoryItemAdded added)
    {
        var now = added.Metadata.Timestamp ?? added.Metadata.ReadOn;
        _items[added.InstanceId] = new AccumulatedItem(
            added.InternalName,
            DisplayName: null,
            StackSize: 1,
            TypeId: null,
            IsRemoved: false,
            RemovedAt: null,
            FirstSeenAt: _items.TryGetValue(added.InstanceId, out var existing)
                ? existing.FirstSeenAt
                : now,
            LastUpdatedAt: now);
        StateChanged?.Invoke();
    }

    private void OnItemUpdated(InventoryItemUpdated updated)
    {
        if (!_items.TryGetValue(updated.InstanceId, out var existing))
            return;

        var now = updated.Metadata.Timestamp ?? updated.Metadata.ReadOn;
        _items[updated.InstanceId] = existing with
        {
            StackSize = updated.NewStackSize,
            LastUpdatedAt = now
        };
        StateChanged?.Invoke();
    }

    private void OnItemRemoved(InventoryItemRemoved removed)
    {
        if (!_items.TryGetValue(removed.InstanceId, out var existing))
        {
            var now = removed.Metadata.Timestamp ?? removed.Metadata.ReadOn;
            _items[removed.InstanceId] = new AccumulatedItem(
                removed.InternalName,
                DisplayName: null,
                StackSize: 0,
                TypeId: null,
                IsRemoved: true,
                RemovedAt: now,
                FirstSeenAt: now,
                LastUpdatedAt: now);
        }
        else
        {
            var now = removed.Metadata.Timestamp ?? removed.Metadata.ReadOn;
            _items[removed.InstanceId] = existing with
            {
                IsRemoved = true,
                RemovedAt = now,
                LastUpdatedAt = now
            };
        }
        StateChanged?.Invoke();
    }

    private void OnItemResolved(InventoryItemResolved resolved)
    {
        if (!_items.TryGetValue(resolved.InstanceId, out var existing))
            return;

        var now = resolved.Metadata.Timestamp ?? resolved.Metadata.ReadOn;
        _items[resolved.InstanceId] = existing with
        {
            DisplayName = resolved.DisplayName,
            StackSize = resolved.Count > 0 ? resolved.Count : existing.StackSize,
            LastUpdatedAt = now
        };
        StateChanged?.Invoke();
    }

    // ── Session / persistence ─────────────────────────────────────────────

    private void OnSessionEstablished(SessionEstablished evt)
    {
        var session = evt.Session;
        if (session.CharacterName == _currentCharacter && session.Server == _currentServer)
            return;

        FlushToDisk();
        _currentCharacter = session.CharacterName;
        _currentServer = session.Server;
        LoadFromDisk();
    }

    private void FlushToDisk()
    {
        if (_store is null || _currentCharacter is null || _currentServer is null)
            return;

        var snapshot = new AccumulatorSnapshot();
        foreach (var (id, item) in _items)
        {
            snapshot.Entries[id] = new AccumulatorSnapshot.PersistedEntry
            {
                InternalName = item.InternalName,
                DisplayName = item.DisplayName,
                StackSize = item.StackSize,
                TypeId = item.TypeId,
                IsRemoved = item.IsRemoved,
                RemovedAt = item.RemovedAt,
                FirstSeenAt = item.FirstSeenAt,
                LastUpdatedAt = item.LastUpdatedAt
            };
        }

        try
        {
            _store.Save(_currentCharacter, _currentServer, snapshot);
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Failed to save accumulator snapshot for {Character}/{Server}",
                _currentCharacter, _currentServer);
        }
    }

    private void LoadFromDisk()
    {
        _items.Clear();
        _pendingAdds.Clear();
        _pendingChats.Clear();

        if (_store is null || _currentCharacter is null || _currentServer is null)
        {
            StateChanged?.Invoke();
            return;
        }

        try
        {
            var snapshot = _store.Load(_currentCharacter, _currentServer);
            var cutoff = DateTimeOffset.UtcNow - RetentionPeriod;
            foreach (var (id, entry) in snapshot.Entries)
            {
                if (entry.IsRemoved && entry.RemovedAt.HasValue && entry.RemovedAt.Value < cutoff)
                    continue;

                _items[id] = new AccumulatedItem(
                    entry.InternalName,
                    entry.DisplayName,
                    entry.StackSize,
                    entry.TypeId,
                    entry.IsRemoved,
                    entry.RemovedAt,
                    entry.FirstSeenAt,
                    entry.LastUpdatedAt);
            }
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Failed to load accumulator snapshot for {Character}/{Server}",
                _currentCharacter, _currentServer);
        }

        StateChanged?.Invoke();
    }

    // ── Lifecycle ─────────────────────────────────────────────────────────

    public void Dispose()
    {
        FlushToDisk();
        _addSub?.Dispose();
        _chatSub?.Dispose();
        _updatedSub?.Dispose();
        _removedSub?.Dispose();
        _resolvedSub?.Dispose();
        _sessionSub?.Dispose();
        _addSub = null;
        _chatSub = null;
        _updatedSub = null;
        _removedSub = null;
        _resolvedSub = null;
        _sessionSub = null;
    }
}
