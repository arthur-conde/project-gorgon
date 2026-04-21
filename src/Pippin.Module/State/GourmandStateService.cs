using Gorgon.Shared.Settings;
using Pippin.Domain;

namespace Pippin.State;

/// <summary>
/// Persists the GourmandStateMachine to disk with a 500 ms debounce.
/// </summary>
public sealed class GourmandStateService : IDisposable
{
    private readonly GourmandStateMachine _state;
    private readonly ISettingsStore<GourmandState> _store;
    private readonly System.Timers.Timer _debounce;
    private bool _dirty;

    public GourmandStateService(GourmandStateMachine state, ISettingsStore<GourmandState> store)
    {
        _state = state;
        _store = store;
        _debounce = new System.Timers.Timer(500) { AutoReset = false };
        _debounce.Elapsed += (_, _) => Flush();
        _state.StateChanged += OnChanged;
    }

    public async Task LoadAsync(CancellationToken ct = default)
    {
        var loaded = await _store.LoadAsync(ct).ConfigureAwait(false);
        _state.Hydrate(loaded);
    }

    private void OnChanged(object? sender, EventArgs e) => MarkDirty();

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

    private GourmandState BuildSnapshot() => new()
    {
        EatenFoods = new Dictionary<string, int>(_state.EatenFoods, StringComparer.OrdinalIgnoreCase),
        LastReportTime = _state.LastReportTime,
    };

    public void Dispose()
    {
        _state.StateChanged -= OnChanged;
        _debounce.Stop();
        _debounce.Dispose();
        Flush();
    }
}
