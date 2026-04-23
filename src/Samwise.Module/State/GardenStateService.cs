using Gorgon.Shared.Settings;

namespace Samwise.State;

/// <summary>
/// Persists the snapshot of the GardenStateMachine to disk via a generic
/// settings store. Subscribes to PlotChanged with a 500 ms debounce.
/// </summary>
public sealed class GardenStateService : IDisposable
{
    private readonly GardenStateMachine _state;
    private readonly ISettingsStore<GardenState> _store;
    private readonly System.Timers.Timer _debounce;
    private bool _dirty;

    public GardenStateService(GardenStateMachine state, ISettingsStore<GardenState> store)
    {
        _state = state;
        _store = store;
        _debounce = new System.Timers.Timer(500) { AutoReset = false };
        _debounce.Elapsed += (_, __) => Flush();
        _state.PlotChanged += OnChanged;
        _state.PlotsRemoved += OnRemoved;
    }

    /// <summary>
    /// Reads persisted state from disk. Does NOT hydrate the state machine —
    /// <see cref="GardenStateMachine.Hydrate"/> raises <c>PlotChanged</c> which
    /// mutates the VM's bound <c>ObservableCollection</c>, so callers must
    /// invoke it on the WPF dispatcher thread.
    /// </summary>
    public Task<GardenState> LoadAsync(CancellationToken ct = default)
        => _store.LoadAsync(ct);

    private void OnChanged(object? sender, PlotChangedArgs e) => MarkDirty();

    private void OnRemoved(object? sender, EventArgs e) => MarkDirty();

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
        try { _store.Save(BuildSnapshot()); } catch { }
    }

    private GardenState BuildSnapshot()
    {
        var s = new GardenState();
        foreach (var (charName, plots) in _state.Snapshot())
        {
            var bucket = new Dictionary<string, PersistedPlot>(StringComparer.Ordinal);
            foreach (var (id, p) in plots)
            {
                bucket[id] = new PersistedPlot
                {
                    CropType = p.CropType, Stage = p.Stage,
                    Title = p.Title, Description = p.Description,
                    Action = p.Action, Scale = p.Scale,
                    PlantedAt = p.PlantedAt, UpdatedAt = p.UpdatedAt,
                    PausedSince = p.PausedSince, PausedDuration = p.PausedDuration,
                };
            }
            s.PlotsByChar[charName] = bucket;
        }
        return s;
    }

    public void Dispose()
    {
        _state.PlotChanged -= OnChanged;
        _state.PlotsRemoved -= OnRemoved;
        _debounce.Stop();
        _debounce.Dispose();
        Flush();
    }
}
