using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Threading;
using Arda.Composition.Events;
using Arda.Dispatch;
using Arda.World.Player.Events;
using Mithril.GameReports;
using Mithril.Shared.Character;
using Mithril.Shared.Reference;

namespace Arda.Inventory;

/// <summary>
/// Self-contained observable inventory ledger. Subscribes to Arda domain events
/// (L3 <c>InventoryItemAdded/Updated/Removed</c>, L4 <c>InventoryItemResolved</c>)
/// and reconciles with storage report snapshots. Maintains a persistent per-character
/// dictionary with soft delete, exposed as a bindable <see cref="Items"/> collection.
/// </summary>
public sealed class InventoryLedger : IDisposable
{
    private static readonly TimeSpan DefaultRetentionTtl = TimeSpan.FromDays(30);
    private static readonly TimeSpan SaveDebounce = TimeSpan.FromMilliseconds(500);

    private readonly ILedgerStateView _view;
    private readonly IActiveCharacterService _activeChar;
    private readonly IReferenceDataService? _refData;
    private readonly Action<Action> _dispatch;
    private readonly Dictionary<long, InventoryItemModel> _index = new();

    private IDisposable? _addedSub;
    private IDisposable? _updatedSub;
    private IDisposable? _removedSub;
    private IDisposable? _resolvedSub;

    private DispatcherTimer? _saveTimer;
    private bool _dirty;
    private bool _disposed;

    /// <summary>Bindable collection. Modules bind directly to this.</summary>
    public ObservableCollection<InventoryItemModel> Items { get; } = [];

    /// <summary>Production constructor. Wraps <see cref="PerCharacterView{T}"/>.</summary>
    public InventoryLedger(
        IDomainEventSubscriber bus,
        PerCharacterView<InventoryLedgerState> view,
        IActiveCharacterService activeChar,
        IReferenceDataService? refData = null,
        Action<Action>? dispatch = null)
        : this(bus, new PerCharacterLedgerStateView(view), activeChar, refData, dispatch)
    { }

    /// <summary>Test-friendly constructor accepting <see cref="ILedgerStateView"/>.</summary>
    internal InventoryLedger(
        IDomainEventSubscriber bus,
        ILedgerStateView view,
        IActiveCharacterService activeChar,
        IReferenceDataService? refData = null,
        Action<Action>? dispatch = null)
    {
        _view = view;
        _activeChar = activeChar;
        _refData = refData;
        _dispatch = dispatch ?? DefaultDispatch;

        HydrateFromState();

        _addedSub = bus.Subscribe<InventoryItemAdded>(OnItemAdded);
        _updatedSub = bus.Subscribe<InventoryItemUpdated>(OnItemUpdated);
        _removedSub = bus.Subscribe<InventoryItemRemoved>(OnItemRemoved);
        _resolvedSub = bus.Subscribe<InventoryItemResolved>(OnItemResolved);

        _view.CurrentChanged += OnCharacterChanged;
        activeChar.StorageReportsChanged += OnStorageReportsChanged;
    }

    // ── Source 1: Player.log (L3) ──────────────────────────────────────────

    private void OnItemAdded(InventoryItemAdded e) => _dispatch(() =>
    {
        var now = e.Metadata.Timestamp ?? e.Metadata.ReadOn;

        if (_index.TryGetValue(e.InstanceId, out var existing))
        {
            existing.InternalName = e.InternalName;
            existing.Sources |= InventorySource.PlayerLog;
            existing.LastUpdatedAt = now;
            if (existing.IsRemoved)
            {
                existing.IsRemoved = false;
                existing.RemovedAt = null;
            }
            EnrichFromReferenceData(existing);
        }
        else
        {
            var model = new InventoryItemModel(e.InstanceId)
            {
                InternalName = e.InternalName,
                StackSize = 1,
                Sources = InventorySource.PlayerLog,
                FirstSeenAt = now,
                LastUpdatedAt = now,
            };
            EnrichFromReferenceData(model);
            _index[e.InstanceId] = model;
            Items.Add(model);
        }

        MarkDirty();
    });

    private void OnItemUpdated(InventoryItemUpdated e) => _dispatch(() =>
    {
        if (!_index.TryGetValue(e.InstanceId, out var model)) return;

        model.StackSize = e.NewStackSize;
        model.LastUpdatedAt = e.Metadata.Timestamp ?? e.Metadata.ReadOn;
        MarkDirty();
    });

    private void OnItemRemoved(InventoryItemRemoved e) => _dispatch(() =>
    {
        if (!_index.TryGetValue(e.InstanceId, out var model)) return;

        model.IsRemoved = true;
        model.RemovedAt = e.Metadata.Timestamp ?? e.Metadata.ReadOn;
        model.LastUpdatedAt = model.RemovedAt.Value;
        MarkDirty();
    });

    // ── Source 2: Chat (L4 resolved) ───────────────────────────────────────

    private void OnItemResolved(InventoryItemResolved e) => _dispatch(() =>
    {
        if (!_index.TryGetValue(e.InstanceId, out var model)) return;

        model.DisplayName = e.DisplayName;
        model.StackSize = e.Count;
        model.Sources |= InventorySource.ChatLog;
        model.LastUpdatedAt = e.Metadata.Timestamp ?? e.Metadata.ReadOn;
        MarkDirty();
    });

    // ── Source 3: Storage Report (snapshot reconciliation) ──────────────────

    private void OnStorageReportsChanged(object? sender, EventArgs e) =>
        _dispatch(ReconcileStorageReport);

    internal void ReconcileStorageReport()
    {
        var report = _activeChar.ActiveStorageContents;
        if (report is null) return;

        if (!DateTimeOffset.TryParse(report.Timestamp, out var reportTime))
            return;

        var state = _view.Current;
        if (state is null) return;

        if (state.LastStorageReportTimestamp.HasValue &&
            reportTime <= state.LastStorageReportTimestamp.Value)
            return;

        var bagItems = report.Items
            .Where(si => si.IsInInventory)
            .ToList();

        var matched = new HashSet<long>();

        foreach (var si in bagItems)
        {
            var internalName = ResolveInternalName(si);
            if (internalName is null) continue;

            var existing = FindByInternalName(internalName);
            if (existing is not null)
            {
                existing.TypeId = si.TypeID;
                existing.IconId ??= ResolveIconId(si);
                existing.DisplayName ??= si.Name;
                existing.Sources |= InventorySource.StorageReport;
                existing.LastUpdatedAt = reportTime;
                matched.Add(existing.InstanceId);
            }
        }

        foreach (var model in _index.Values)
        {
            if (matched.Contains(model.InstanceId)) continue;
            if (model.IsRemoved) continue;
            if (model.LastUpdatedAt >= reportTime) continue;

            model.IsRemoved = true;
            model.RemovedAt = reportTime;
            model.LastUpdatedAt = reportTime;
        }

        state.LastStorageReportTimestamp = reportTime;
        MarkDirty();
    }

    // ── Character switch ───────────────────────────────────────────────────

    private void OnCharacterChanged(object? sender, EventArgs e) =>
        _dispatch(HydrateFromState);

    // ── Persistence ────────────────────────────────────────────────────────

    internal void HydrateFromState()
    {
        _index.Clear();
        Items.Clear();

        var state = _view.Current;
        if (state is null) return;

        SweepRetention(state);

        foreach (var (id, p) in state.Entries)
        {
            var model = new InventoryItemModel(id)
            {
                InternalName = p.InternalName,
                DisplayName = p.DisplayName,
                StackSize = p.StackSize,
                TypeId = p.TypeId,
                IconId = p.IconId,
                IsRemoved = p.RemovedAt.HasValue,
                RemovedAt = p.RemovedAt,
                Sources = p.Sources,
                FirstSeenAt = p.FirstSeenAt,
                LastUpdatedAt = p.LastUpdatedAt,
            };
            _index[id] = model;
            Items.Add(model);
        }
    }

    internal void ProjectToState()
    {
        var state = _view.Current;
        if (state is null) return;

        state.Entries.Clear();
        foreach (var (id, m) in _index)
        {
            state.Entries[id] = new PersistedItem
            {
                InternalName = m.InternalName,
                DisplayName = m.DisplayName,
                StackSize = m.StackSize,
                TypeId = m.TypeId,
                IconId = m.IconId,
                FirstSeenAt = m.FirstSeenAt,
                LastUpdatedAt = m.LastUpdatedAt,
                RemovedAt = m.RemovedAt,
                Sources = m.Sources,
            };
        }
    }

    private void MarkDirty()
    {
        _dirty = true;
        EnsureSaveTimer();
    }

    private void EnsureSaveTimer()
    {
        if (_saveTimer is not null) return;

        _saveTimer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = SaveDebounce,
        };
        _saveTimer.Tick += OnSaveTimerTick;
        _saveTimer.Start();
    }

    private void OnSaveTimerTick(object? sender, EventArgs e)
    {
        _saveTimer?.Stop();
        _saveTimer = null;
        Flush();
    }

    internal void Flush()
    {
        if (!_dirty) return;
        _dirty = false;
        ProjectToState();
        _view.Save();
    }

    // ── Retention sweep ────────────────────────────────────────────────────

    internal static void SweepRetention(InventoryLedgerState state)
    {
        SweepRetention(state, DefaultRetentionTtl);
    }

    internal static void SweepRetention(InventoryLedgerState state, TimeSpan ttl)
    {
        var cutoff = DateTimeOffset.UtcNow - ttl;
        var toRemove = state.Entries
            .Where(kv => kv.Value.RemovedAt.HasValue && kv.Value.RemovedAt.Value < cutoff)
            .Select(kv => kv.Key)
            .ToList();

        foreach (var key in toRemove)
            state.Entries.Remove(key);
    }

    // ── Reference data helpers ─────────────────────────────────────────────

    private void EnrichFromReferenceData(InventoryItemModel model)
    {
        if (_refData is null) return;
        if (!_refData.ItemsByInternalName.TryGetValue(model.InternalName, out var item)) return;

        model.DisplayName ??= item.Name;
        model.IconId ??= item.IconId;
        model.TypeId ??= (int)item.Id;
    }

    private string? ResolveInternalName(StorageItem si)
    {
        if (_refData is null) return null;
        return _refData.Items.TryGetValue(si.TypeID, out var item) ? item.InternalName : null;
    }

    private int? ResolveIconId(StorageItem si)
    {
        if (_refData is null) return null;
        return _refData.Items.TryGetValue(si.TypeID, out var item) ? item.IconId : null;
    }

    private InventoryItemModel? FindByInternalName(string internalName)
    {
        foreach (var model in _index.Values)
        {
            if (!model.IsRemoved &&
                string.Equals(model.InternalName, internalName, StringComparison.Ordinal))
                return model;
        }
        return null;
    }

    // ── Thread marshaling ──────────────────────────────────────────────────

    private static void DefaultDispatch(Action action)
    {
        var d = Application.Current?.Dispatcher;
        if (d is null || d.CheckAccess()) action();
        else d.InvokeAsync(action);
    }

    // ── Dispose ────────────────────────────────────────────────────────────

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        _addedSub?.Dispose();
        _updatedSub?.Dispose();
        _removedSub?.Dispose();
        _resolvedSub?.Dispose();
        _addedSub = null;
        _updatedSub = null;
        _removedSub = null;
        _resolvedSub = null;

        _view.CurrentChanged -= OnCharacterChanged;
        _activeChar.StorageReportsChanged -= OnStorageReportsChanged;

        _saveTimer?.Stop();
        _saveTimer = null;

        Flush();
    }
}
