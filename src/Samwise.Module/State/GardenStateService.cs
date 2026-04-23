using Gorgon.Shared.Character;

namespace Samwise.State;

/// <summary>
/// Persists the GardenStateMachine to per-character files. Each character's plot dict
/// lives in <c>characters/{slug}/samwise.json</c>. The state machine still holds every
/// known character's plots in memory (the garden view shows them all), but writes are
/// scoped to just the character(s) touched by recent events.
///
/// Subscribes to <see cref="GardenStateMachine.PlotChanged"/>/<c>PlotsRemoved</c> with
/// a 500 ms debounce; on every tick, saves only the characters flagged dirty.
/// </summary>
public sealed class GardenStateService : IDisposable
{
    private readonly GardenStateMachine _state;
    private readonly PerCharacterStore<GardenCharacterState> _store;
    private readonly IActiveCharacterService _active;
    private readonly System.Timers.Timer _debounce;
    private readonly HashSet<string> _dirtyChars = new(StringComparer.OrdinalIgnoreCase);
    private readonly Lock _gate = new();

    public GardenStateService(
        GardenStateMachine state,
        PerCharacterStore<GardenCharacterState> store,
        IActiveCharacterService active)
    {
        _state = state;
        _store = store;
        _active = active;
        _debounce = new System.Timers.Timer(500) { AutoReset = false };
        _debounce.Elapsed += (_, _) => Flush();
        _state.PlotChanged += OnChanged;
        _state.PlotsRemoved += OnRemoved;
    }

    /// <summary>
    /// Read every known character's per-char file from disk. The returned map is handed
    /// to <see cref="GardenStateMachine.HydrateCharacter"/> by the caller — the caller
    /// must invoke that on the WPF thread since it raises <c>PlotChanged</c> and mutates
    /// the VM's bound collection.
    /// </summary>
    public Task<IReadOnlyList<(string CharName, IReadOnlyDictionary<string, PersistedPlot> Plots)>> LoadAllAsync(CancellationToken ct = default)
    {
        var result = new List<(string, IReadOnlyDictionary<string, PersistedPlot>)>();
        foreach (var snapshot in _active.Characters)
        {
            if (string.IsNullOrEmpty(snapshot.Name) || string.IsNullOrEmpty(snapshot.Server)) continue;
            var perChar = _store.Load(snapshot.Name, snapshot.Server);
            result.Add((snapshot.Name, perChar.Plots));
        }
        return Task.FromResult<IReadOnlyList<(string, IReadOnlyDictionary<string, PersistedPlot>)>>(result);
    }

    private void OnChanged(object? sender, PlotChangedArgs e)
    {
        if (string.IsNullOrEmpty(e.Plot.CharName)) return;
        lock (_gate) _dirtyChars.Add(e.Plot.CharName);
        MarkDirty();
    }

    private void OnRemoved(object? sender, EventArgs e)
    {
        // On removal we don't know which characters lost plots; flag them all (via snapshot).
        foreach (var (charName, _) in _state.Snapshot())
        {
            if (!string.IsNullOrEmpty(charName)) lock (_gate) _dirtyChars.Add(charName);
        }
        MarkDirty();
    }

    private void MarkDirty()
    {
        _debounce.Stop();
        _debounce.Start();
    }

    private void Flush()
    {
        string[] toFlush;
        lock (_gate)
        {
            if (_dirtyChars.Count == 0) return;
            toFlush = _dirtyChars.ToArray();
            _dirtyChars.Clear();
        }

        var snapshot = _state.Snapshot();
        var serverByName = ResolveServers();
        foreach (var charName in toFlush)
        {
            if (!serverByName.TryGetValue(charName, out var server)) continue;
            snapshot.TryGetValue(charName, out var plots);
            var perChar = new GardenCharacterState
            {
                Plots = plots is null
                    ? new Dictionary<string, PersistedPlot>(StringComparer.Ordinal)
                    : plots.ToDictionary(kv => kv.Key, kv => new PersistedPlot
                    {
                        CropType = kv.Value.CropType,
                        Stage = kv.Value.Stage,
                        Title = kv.Value.Title,
                        Description = kv.Value.Description,
                        Action = kv.Value.Action,
                        Scale = kv.Value.Scale,
                        PlantedAt = kv.Value.PlantedAt,
                        UpdatedAt = kv.Value.UpdatedAt,
                        PausedSince = kv.Value.PausedSince,
                        PausedDuration = kv.Value.PausedDuration,
                    }, StringComparer.Ordinal),
            };
            try { _store.Save(charName, server, perChar); } catch { /* best-effort */ }
        }
    }

    private Dictionary<string, string> ResolveServers()
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var snap in _active.Characters)
        {
            if (!map.ContainsKey(snap.Name)) map[snap.Name] = snap.Server;
        }
        if (!string.IsNullOrEmpty(_active.ActiveCharacterName) && !string.IsNullOrEmpty(_active.ActiveServer))
            map[_active.ActiveCharacterName] = _active.ActiveServer;
        return map;
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
