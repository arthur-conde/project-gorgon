using Mithril.GameState.Inventory;
using Mithril.Reference.Models.Items;
using Mithril.Reference.Models.Recipes;
using Mithril.Shared.Reference;

namespace Legolas.Tests;

/// <summary>
/// Test double for <see cref="IInventoryService"/> (#488). <see cref="Subscribe"/>
/// atomically replays the current live (non-deleted) items as
/// <see cref="InventoryEventKind.Added"/> then delivers live add/delete, like
/// the real service. <see cref="Add"/>/<see cref="Delete"/> drive the stream.
/// </summary>
public sealed class FakeInventoryService : IInventoryService
{
    private readonly record struct Entry(string InternalName, bool Deleted);

    private readonly List<long> _order = new();
    private readonly Dictionary<long, Entry> _map = new();
    private readonly List<Action<InventoryEvent>> _handlers = new();

    public IDisposable Subscribe(Action<InventoryEvent> handler)
    {
        foreach (var id in _order)
        {
            var e = _map[id];
            if (e.Deleted) continue;
            handler(new InventoryEvent(InventoryEventKind.Added, id, e.InternalName,
                DateTime.UtcNow, 1, true));
        }
        _handlers.Add(handler);
        return new Sub(this, handler);
    }

    public bool TryResolve(long instanceId, out string internalName)
    {
        if (_map.TryGetValue(instanceId, out var e)) { internalName = e.InternalName; return true; }
        internalName = string.Empty;
        return false;
    }

    public bool TryGetStackSize(long instanceId, out int stackSize)
    {
        stackSize = _map.ContainsKey(instanceId) ? 1 : 0;
        return _map.ContainsKey(instanceId);
    }

    /// <summary>Seed an item already in inventory before anyone subscribes
    /// (mimics login replay) — raises no live event.</summary>
    public void Seed(long instanceId, string internalName)
    {
        if (_map.ContainsKey(instanceId)) return;
        _order.Add(instanceId);
        _map[instanceId] = new Entry(internalName, false);
    }

    public void Add(long instanceId, string internalName, DateTime? at = null)
    {
        if (!_map.ContainsKey(instanceId)) _order.Add(instanceId);
        _map[instanceId] = new Entry(internalName, false);
        Raise(new InventoryEvent(InventoryEventKind.Added, instanceId, internalName,
            at ?? DateTime.UtcNow, 1, true));
    }

    public void Delete(long instanceId, DateTime at)
    {
        if (!_map.TryGetValue(instanceId, out var e)) return;
        _map[instanceId] = e with { Deleted = true };
        Raise(new InventoryEvent(InventoryEventKind.Deleted, instanceId, e.InternalName,
            at, e.Deleted ? 0 : 1, true));
    }

    private void Raise(InventoryEvent e)
    {
        foreach (var h in _handlers.ToArray()) h(e);
    }

    private sealed class Sub(FakeInventoryService owner, Action<InventoryEvent> handler) : IDisposable
    {
        public void Dispose() => owner._handlers.Remove(handler);
    }
}

/// <summary>
/// Minimal <see cref="IReferenceDataService"/> for the motherlode-map
/// predicate: only <see cref="ItemsByInternalName"/> is populated (the
/// coordinator resolves an InternalName to an item whose display Name ends
/// with "Motherlode Map"). Other members are empty/no-op.
/// </summary>
public sealed class FakeMotherlodeRefData : IReferenceDataService
{
    public FakeMotherlodeRefData(params (string InternalName, string Name)[] items)
    {
        var byName = new Dictionary<string, Item>(StringComparer.Ordinal);
        var byId = new Dictionary<long, Item>();
        long id = 1;
        foreach (var (intern, name) in items)
        {
            var it = new Item { Id = id++, InternalName = intern, Name = name };
            byName[intern] = it;
            byId[it.Id] = it;
        }
        ItemsByInternalName = byName;
        Items = byId;
    }

    public IReadOnlyList<string> Keys { get; } = [];
    public IReadOnlyDictionary<long, Item> Items { get; }
    public IReadOnlyDictionary<string, Item> ItemsByInternalName { get; }
    public ItemKeywordIndex KeywordIndex => ItemKeywordIndex.Empty;
    public IReadOnlyDictionary<string, Recipe> Recipes { get; } = new Dictionary<string, Recipe>();
    public IReadOnlyDictionary<string, Recipe> RecipesByInternalName { get; } = new Dictionary<string, Recipe>();
    public IReadOnlyDictionary<string, SkillEntry> Skills { get; } = new Dictionary<string, SkillEntry>();
    public IReadOnlyDictionary<string, XpTableEntry> XpTables { get; } = new Dictionary<string, XpTableEntry>();
    public IReadOnlyDictionary<string, NpcEntry> Npcs { get; } = new Dictionary<string, NpcEntry>();
    public IReadOnlyDictionary<string, AreaEntry> Areas { get; } = new Dictionary<string, AreaEntry>(StringComparer.Ordinal);
    public IReadOnlyDictionary<string, IReadOnlyList<ItemSource>> ItemSources { get; } = new Dictionary<string, IReadOnlyList<ItemSource>>();
    public IReadOnlyDictionary<string, AttributeEntry> Attributes { get; } = new Dictionary<string, AttributeEntry>();
    public IReadOnlyDictionary<string, PowerEntry> Powers { get; } = new Dictionary<string, PowerEntry>();
    public IReadOnlyDictionary<string, IReadOnlyList<string>> Profiles { get; } = new Dictionary<string, IReadOnlyList<string>>();
    public IReadOnlyDictionary<string, Mithril.Reference.Models.Quests.Quest> Quests { get; } = new Dictionary<string, Mithril.Reference.Models.Quests.Quest>();
    public IReadOnlyDictionary<string, Mithril.Reference.Models.Quests.Quest> QuestsByInternalName { get; } = new Dictionary<string, Mithril.Reference.Models.Quests.Quest>();
    public IReadOnlyDictionary<string, string> Strings { get; } = new Dictionary<string, string>(StringComparer.Ordinal);
    public event EventHandler<string>? FileUpdated;
    public ReferenceFileSnapshot GetSnapshot(string key) => new(key, ReferenceFileSource.Bundled, "", null, 0);
    public Task RefreshAsync(string key, CancellationToken ct = default) => Task.CompletedTask;
    public Task RefreshAllAsync(CancellationToken ct = default) => Task.CompletedTask;
    public void BeginBackgroundRefresh() { }
    private void Suppress() => FileUpdated?.Invoke(this, "");
}
