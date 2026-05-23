using Mithril.GameState.Inventory;
using Mithril.Reference.Models.Items;
using Mithril.Reference.Models.Recipes;
using Mithril.Shared.Reference;
using Mithril.WorldSim;
using Mithril.WorldSim.Player;

namespace Legolas.Tests;

/// <summary>
/// Minimal <see cref="IPlayerWorld"/> stub for <c>MotherlodeMeasurementCoordinator</c>
/// tests. Replaces the pre-#727 <c>FakeInventoryService</c> wrapper: post-migration
/// the coordinator subscribes to <see cref="PlayerInventoryRemoved"/> on the
/// PlayerWorld bus directly (principle 4 single-world-direct exit). The
/// <see cref="Add"/> bookkeeping is preserved so <see cref="Delete"/> can fill
/// in the <c>InternalName</c> the folder-emitted event carries — the
/// coordinator no longer reacts to Adds, but the tests still need an
/// id→InternalName mapping to drive a meaningful Removed event.
/// </summary>
public sealed class FakeMotherlodePlayerWorld : IPlayerWorld
{
    private readonly Dictionary<long, string> _ledger = new();
    private readonly TestBus _bus = new();
    private readonly TestClock _clock = new();

    public IWorldClock Clock => _clock;
    public IWorldEventBus Bus => _bus;

    /// <summary>Register an instance-id↔InternalName mapping (mimics login
    /// replay / a prior <c>ProcessAddItem</c>). The coordinator does not react
    /// to Adds in the post-#727 model — this only populates the ledger so a
    /// later <see cref="Delete"/> can publish the right InternalName.</summary>
    public void Add(long instanceId, string internalName) => _ledger[instanceId] = internalName;

    /// <summary>Publish a <see cref="PlayerInventoryRemoved"/> for the given
    /// instance — the dig signal the post-#727 coordinator subscribes to.
    /// The InternalName is pulled from the prior <see cref="Add"/>; an
    /// unregistered id publishes an empty InternalName (mirrors the
    /// folder contract for carryover-instance deletes).</summary>
    public void Delete(long instanceId, DateTime at)
    {
        var name = _ledger.TryGetValue(instanceId, out var n) ? n : string.Empty;
        var ts = DateTime.SpecifyKind(at, DateTimeKind.Utc);
        _bus.Publish(new DateTimeOffset(ts, TimeSpan.Zero),
            new PlayerInventoryRemoved(instanceId, name, ts));
    }

    public void RegisterProducer<T>(IFrameProducer<T> producer) =>
        throw new NotSupportedException("FakeMotherlodePlayerWorld: register on a real world.");
    public void RegisterFolder<T>(IFolder<T> folder) =>
        throw new NotSupportedException("FakeMotherlodePlayerWorld: register on a real world.");
    public void RegisterComposer(IComposer composer) =>
        throw new NotSupportedException("FakeMotherlodePlayerWorld: register on a real world.");
    public Task StartMerger(CancellationToken ct) => Task.CompletedTask;

    private sealed class TestClock : IWorldClock
    {
        public DateTimeOffset Now { get; set; } = DateTimeOffset.MinValue;
        public long Frame { get; set; }
        public WorldMode Mode { get; set; } = WorldMode.Live;
    }

    private sealed class TestBus : IWorldEventBus
    {
        private readonly object _lock = new();
        private readonly Dictionary<Type, List<Delegate>> _handlers = new();

        public IDisposable Subscribe<T>(Action<Frame<T>> handler)
        {
            ArgumentNullException.ThrowIfNull(handler);
            lock (_lock)
            {
                if (!_handlers.TryGetValue(typeof(T), out var list))
                    _handlers[typeof(T)] = list = new List<Delegate>();
                list.Add(handler);
                return new Sub(this, typeof(T), handler);
            }
        }

        public void Publish<T>(DateTimeOffset timestamp, T payload)
        {
            List<Delegate>? snap;
            lock (_lock)
            {
                if (!_handlers.TryGetValue(typeof(T), out var list)) return;
                snap = list.ToList();
            }
            var frame = new Frame<T>(timestamp, payload);
            foreach (var h in snap) ((Action<Frame<T>>)h)(frame);
        }

        private sealed class Sub(TestBus owner, Type type, Delegate handler) : IDisposable
        {
            public void Dispose()
            {
                lock (owner._lock)
                {
                    if (owner._handlers.TryGetValue(type, out var list)) list.Remove(handler);
                }
            }
        }
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
