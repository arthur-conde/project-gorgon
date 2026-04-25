using Mithril.Shared.Character;
using Pippin.Domain;

namespace Pippin.State;

/// <summary>
/// Bridges the <see cref="GourmandStateMachine"/> to per-character persistence.
/// On character switch, hydrates the machine from the new character's file. On state
/// changes, snapshots the machine back into <see cref="PerCharacterView{T}.Current"/>
/// and writes to disk with a 500 ms debounce.
/// </summary>
public sealed class GourmandStateService : IDisposable
{
    private readonly GourmandStateMachine _state;
    private readonly PerCharacterView<GourmandState> _view;
    private readonly System.Timers.Timer _debounce;
    private bool _dirty;
    private bool _hydrating;

    public GourmandStateService(GourmandStateMachine state, PerCharacterView<GourmandState> view)
    {
        _state = state;
        _view = view;
        _debounce = new System.Timers.Timer(500) { AutoReset = false };
        _debounce.Elapsed += (_, _) => Flush();
        _state.StateChanged += OnChanged;
        _view.CurrentChanged += OnCurrentChanged;
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
    }

    private void SyncSnapshotToView()
    {
        var current = _view.Current;
        if (current is null) return;
        current.EatenFoods = new Dictionary<string, int>(_state.EatenFoods, StringComparer.OrdinalIgnoreCase);
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
        _debounce.Stop();
        _debounce.Dispose();
        Flush();
    }
}
