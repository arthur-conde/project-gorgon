using System.Collections;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using FluentAssertions;
using Mithril.GameState.Inventory;
using Mithril.Reference.Models.Items;
using Mithril.Reference.Models.Recipes;
using Mithril.Shared.Collections;
using Mithril.Shared.Logging;
using Mithril.Shared.Reference;
using Mithril.WorldSim;
using Palantir.ViewModels;
using Xunit;

namespace Palantir.Tests;

/// <summary>
/// Tests for the #726 migration: Palantir's live-inventory VM now binds to
/// <see cref="IInventoryView.Items"/> directly instead of mirroring the
/// legacy <see cref="InventoryEvent"/> shim into a local collection. Each
/// test seeds / mutates a <see cref="FakeInventoryView"/>'s items and asserts
/// the VM's <c>View</c>/<c>LiveCount</c>/<c>DeletedCount</c>/<c>ShowDeleted</c>
/// surfaces reflect the bound state. The view-internal bus → items mutation
/// path is covered by <c>InventoryViewTests</c> in
/// <c>Mithril.GameState.Tests</c> — these tests pin the consumer-side
/// projection only.
/// </summary>
public sealed class LiveInventoryViewModelTests
{
    [Fact]
    public void Binding_ReflectsCurrentViewItems()
    {
        var view = new FakeInventoryView();
        view.Seed(NewItem(1, "Moonstone", stack: 1, confirmed: true));
        view.Seed(NewItem(2, "Guava", stack: 4, confirmed: true));

        using var vm = NewVm(view);

        vm.Items.Should().HaveCount(2);
        vm.LiveCount.Should().Be(2);
        vm.DeletedCount.Should().Be(0);
        VisibleRows(vm).Should().HaveCount(2);
    }

    [Fact]
    public void Added_AfterBind_AppearsInBoundCollection()
    {
        var view = new FakeInventoryView();
        using var vm = NewVm(view);

        view.Seed(NewItem(1, "Moonstone", stack: 1, confirmed: true));

        vm.Items.Should().ContainSingle();
        vm.Items.Single().InternalName.Should().Be("Moonstone");
        vm.LiveCount.Should().Be(1);
    }

    [Fact]
    public void RowStackSize_PropagatesViaInpc()
    {
        var view = new FakeInventoryView();
        var item = NewItem(1, "Guava", stack: 1, confirmed: false);
        view.Seed(item);
        using var vm = NewVm(view);

        item.StackSize = 4;
        item.SizeConfirmed = true;

        vm.Items.Single().StackSize.Should().Be(4);
        vm.Items.Single().SizeConfirmed.Should().BeTrue();
    }

    [Fact]
    public void IsDeletedFlip_HidesRowFromDefaultViewAndUpdatesCounts()
    {
        var view = new FakeInventoryView();
        var skull = NewItem(1, "GiantSkull", stack: 50, confirmed: true);
        view.Seed(skull);
        view.Seed(NewItem(2, "Guava", stack: 4, confirmed: true));
        using var vm = NewVm(view);

        skull.IsDeleted = true;

        VisibleRows(vm).Select(r => r.InstanceId).Should().BeEquivalentTo([2L]);
        vm.LiveCount.Should().Be(1);
        vm.DeletedCount.Should().Be(1);
        // Soft-delete contract: the row stays in the underlying collection.
        vm.Items.Should().HaveCount(2);
        skull.StackSize.Should().Be(50);
    }

    [Fact]
    public void ShowDeletedToggle_RevealsDeletedRows()
    {
        var view = new FakeInventoryView();
        var ms = NewItem(1, "Moonstone", stack: 1, confirmed: true);
        view.Seed(ms);
        using var vm = NewVm(view);

        ms.IsDeleted = true;
        VisibleRows(vm).Should().BeEmpty();

        vm.ShowDeleted = true;
        VisibleRows(vm).Should().ContainSingle().Which.InstanceId.Should().Be(1);
    }

    [Fact]
    public void DisplayMetadata_ResolvesViaReferenceData()
    {
        // Display projection lives in the XAML cell converter; this test pins
        // the VM-side hook (ReferenceData accessor) the converter binds to,
        // so the binding path is unbroken across the migration.
        var refData = new FakeRefData(
            new Item { Id = 0, Name = "Moonstone Crystal", InternalName = "Moonstone", MaxStackSize = 100, IconId = 4242, Keywords = [] });
        var view = new FakeInventoryView();
        using var vm = NewVm(view, refData);

        vm.ReferenceData.Should().BeSameAs(refData);
        vm.ReferenceData!.ItemsByInternalName["Moonstone"].IconId.Should().Be(4242);
    }

    [Fact]
    public void Dispose_DetachesFromUnderlyingCollection()
    {
        var view = new FakeInventoryView();
        var vm = NewVm(view);
        view.Seed(NewItem(1, "Moonstone", stack: 1, confirmed: true));
        vm.LiveCount.Should().Be(1);

        vm.Dispose();
        view.Seed(NewItem(2, "Guava", stack: 4, confirmed: true));

        vm.LiveCount.Should().Be(1, "the disposed VM must stop re-counting on collection mutations");
    }

    private static LiveInventoryViewModel NewVm(IInventoryView view, IReferenceDataService? refData = null)
        // Synchronous dispatcher: test thread runs both VM ctor + event handlers.
        => new(view, refData, dispatch: action => action());

    private static IEnumerable<InventoryItem> VisibleRows(LiveInventoryViewModel vm)
        => vm.View.Cast<InventoryItem>();

    private static InventoryItem NewItem(long id, string internalName, int stack, bool confirmed)
        => new(id, internalName, stack, confirmed);

    /// <summary>
    /// Minimal <see cref="IInventoryView"/> stub. The legacy
    /// <c>Subscribe(Action&lt;InventoryEvent&gt;)</c> shim is unused by the
    /// post-#726 Palantir VM; <c>Bus</c> is unused too (the VM binds to
    /// <see cref="IInventoryView.Items"/> directly and observes per-row INPC),
    /// so both throw to surface accidental regressions.
    /// </summary>
    private sealed class FakeInventoryView : IInventoryView
    {
        private readonly ObservableInventoryItemsStub _items = new();
        public IWorldEventBus Bus => throw new NotSupportedException("Palantir VM binds to Items directly, not Bus.");
        public IReadOnlyObservableCollection<InventoryItem> Items => _items;
        public object ItemsSyncRoot { get; } = new();

        public void Seed(InventoryItem item) => _items.AddItem(item);

        public bool TryResolve(long instanceId, out string internalName) { internalName = ""; return false; }
        public bool TryGetStackSize(long instanceId, out int stackSize) { stackSize = 0; return false; }

#pragma warning disable CS0618 // shim is obsolete; FakeInventoryView only implements it because IInventoryView requires it
        public IDisposable Subscribe(Action<InventoryEvent> handler, ReplayMode replay = ReplayMode.FromSessionStart)
            => throw new NotSupportedException("Palantir VM does not consume the legacy shim post-#726.");
#pragma warning restore CS0618
    }

    /// <summary>
    /// Wraps an <see cref="ObservableCollection{T}"/> as the read-only
    /// observable + non-generic <see cref="IList"/> surface
    /// <see cref="IInventoryView.Items"/> exposes in production (mirrors
    /// <c>ObservableInventoryItems</c> minus the internal mutator
    /// access-control — tests want to seed it from outside).
    /// </summary>
    private sealed class ObservableInventoryItemsStub : IReadOnlyObservableCollection<InventoryItem>, IList
    {
        private readonly ObservableCollection<InventoryItem> _inner = new();

        public InventoryItem this[int index] => _inner[index];
        public int Count => _inner.Count;
        public IEnumerator<InventoryItem> GetEnumerator() => _inner.GetEnumerator();
        IEnumerator IEnumerable.GetEnumerator() => ((IEnumerable)_inner).GetEnumerator();

        public event NotifyCollectionChangedEventHandler? CollectionChanged
        {
            add => _inner.CollectionChanged += value;
            remove => _inner.CollectionChanged -= value;
        }
        public event PropertyChangedEventHandler? PropertyChanged
        {
            add => ((INotifyPropertyChanged)_inner).PropertyChanged += value;
            remove => ((INotifyPropertyChanged)_inner).PropertyChanged -= value;
        }

        internal void AddItem(InventoryItem item) => _inner.Add(item);

        object? IList.this[int index] { get => _inner[index]; set => throw new NotSupportedException(); }
        bool IList.IsReadOnly => true;
        bool IList.IsFixedSize => false;
        bool ICollection.IsSynchronized => ((ICollection)_inner).IsSynchronized;
        object ICollection.SyncRoot => ((ICollection)_inner).SyncRoot;
        int IList.Add(object? value) => throw new NotSupportedException();
        void IList.Clear() => throw new NotSupportedException();
        bool IList.Contains(object? value) => ((IList)_inner).Contains(value);
        int IList.IndexOf(object? value) => ((IList)_inner).IndexOf(value);
        void IList.Insert(int index, object? value) => throw new NotSupportedException();
        void IList.Remove(object? value) => throw new NotSupportedException();
        void IList.RemoveAt(int index) => throw new NotSupportedException();
        void ICollection.CopyTo(Array array, int index) => ((ICollection)_inner).CopyTo(array, index);
    }

    private sealed class FakeRefData : IReferenceDataService
    {
        private readonly Dictionary<string, Item> _byName;

        public FakeRefData(params Item[] items)
        {
            _byName = items
                .Where(i => !string.IsNullOrEmpty(i.InternalName))
                .ToDictionary(i => i.InternalName!, i => i, StringComparer.Ordinal);
        }

        public IReadOnlyList<string> Keys { get; } = ["items"];
        public IReadOnlyDictionary<long, Item> Items { get; } = new Dictionary<long, Item>();
        public IReadOnlyDictionary<string, Item> ItemsByInternalName => _byName;
        public ItemKeywordIndex KeywordIndex { get; } = ItemKeywordIndex.Empty;
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
        public ReferenceFileSnapshot GetSnapshot(string key) => new(key, ReferenceFileSource.Bundled, "test", null, 0);
        public Task RefreshAsync(string key, CancellationToken ct = default) => Task.CompletedTask;
        public Task RefreshAllAsync(CancellationToken ct = default) => Task.CompletedTask;
        public void BeginBackgroundRefresh() { }
        public event EventHandler<string>? FileUpdated { add { } remove { } }
    }
}
