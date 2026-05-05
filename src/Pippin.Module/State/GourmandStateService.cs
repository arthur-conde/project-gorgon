using Mithril.Shared.Character;
using Pippin.Domain;

namespace Pippin.State;

/// <summary>
/// Bridges the <see cref="GourmandStateMachine"/> to per-character persistence.
/// On character switch, hydrates the machine from the new character's file. On state
/// changes, snapshots the machine back into <see cref="PerCharacterView{T}.Current"/>
/// and writes to disk with a 500 ms debounce.
///
/// Also drives the second phase of the v1→v2 schema migration: when a hydrated state
/// has a non-empty <see cref="GourmandState.PendingLegacyByName"/> and the catalog
/// reports ready, the legacy display-name dict is resolved through the catalog into
/// <see cref="GourmandState.EatenFoodsByInternalName"/> + <see cref="GourmandState.UnknownByName"/>.
/// </summary>
public sealed class GourmandStateService : IDisposable
{
    private readonly GourmandStateMachine _state;
    private readonly PerCharacterView<GourmandState> _view;
    private readonly FoodCatalog _catalog;
    private readonly System.Timers.Timer _debounce;
    private bool _dirty;
    private bool _hydrating;

    public GourmandStateService(
        GourmandStateMachine state,
        PerCharacterView<GourmandState> view,
        FoodCatalog catalog)
    {
        _state = state;
        _view = view;
        _catalog = catalog;
        _debounce = new System.Timers.Timer(500) { AutoReset = false };
        _debounce.Elapsed += (_, _) => Flush();
        _state.StateChanged += OnChanged;
        _view.CurrentChanged += OnCurrentChanged;
        _catalog.CatalogChanged += OnCatalogChanged;
    }

    /// <summary>
    /// Hydrate the state machine from the active character's persisted state, if any.
    /// A no-op when no character is active yet — <see cref="OnCurrentChanged"/> will fire
    /// once one resolves.
    /// </summary>
    public Task LoadAsync(CancellationToken ct = default)
    {
        HydrateFromCurrent();
        return Task.CompletedTask;
    }

    private void OnChanged(object? sender, EventArgs e)
    {
        if (_hydrating) return;
        SyncSnapshotToView();
        MarkDirty();
    }

    private void OnCurrentChanged(object? sender, EventArgs e) => HydrateFromCurrent();

    private void OnCatalogChanged(object? sender, EventArgs e)
    {
        // Catalog (re)built — try to drain any pending legacy data and reconcile previously
        // unknown foods now that we have more reference data on hand.
        var promoted = PromoteLegacyIfReady();
        var reconciled = _state.ReconcileUnknowns();
        if (promoted || reconciled) MarkDirty();
    }

    private void HydrateFromCurrent()
    {
        var current = _view.Current;
        _hydrating = true;
        try
        {
            _state.Hydrate(current ?? new GourmandState());
        }
        finally
        {
            _hydrating = false;
        }

        // Try to finish a deferred v1→v2 migration if the catalog is already ready.
        PromoteLegacyIfReady();
    }

    /// <summary>
    /// Drain <see cref="GourmandState.PendingLegacyByName"/> through the catalog.
    /// No-op when the catalog isn't ready, when there's no pending data, or when no
    /// character is active. Returns true when state was modified.
    /// </summary>
    private bool PromoteLegacyIfReady()
    {
        var current = _view.Current;
        if (current is null) return false;
        if (current.PendingLegacyByName is not { Count: > 0 } pending) return false;
        if (!_catalog.IsReady) return false;

        _hydrating = true;
        try
        {
            _state.ApplyLegacyByName(pending);
        }
        finally
        {
            _hydrating = false;
        }

        current.PendingLegacyByName = null;
        SyncSnapshotToView();
        MarkDirty();
        return true;
    }

    private void SyncSnapshotToView()
    {
        var current = _view.Current;
        if (current is null) return;
        current.EatenFoodsByInternalName = new Dictionary<string, int>(_state.EatenFoodsByInternalName, StringComparer.Ordinal);
        current.UnknownByName = new Dictionary<string, int>(_state.UnknownByName, StringComparer.OrdinalIgnoreCase);
        current.LastReportTime = _state.LastReportTime;
    }

    private void MarkDirty()
    {
        _dirty = true;
        _debounce.Stop();
        _debounce.Start();
    }

    private void Flush()
    {
        if (!_dirty) return;
        _dirty = false;
        try { _view.Save(); } catch { }
    }

    public void Dispose()
    {
        _state.StateChanged -= OnChanged;
        _view.CurrentChanged -= OnCurrentChanged;
        _catalog.CatalogChanged -= OnCatalogChanged;
        _debounce.Stop();
        _debounce.Dispose();
        Flush();
    }
}
