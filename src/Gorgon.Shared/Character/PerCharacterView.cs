namespace Gorgon.Shared.Character;

/// <summary>
/// Active-character-aware wrapper over <see cref="PerCharacterStore{T}"/>. Holds the
/// current character's <typeparamref name="T"/> in memory, lazy-loads it from disk on
/// first access after a character switch, and raises <see cref="CurrentChanged"/> when
/// the active character changes.
///
/// Modules typically inject <see cref="PerCharacterView{T}"/> rather than the underlying
/// store — they only care about "the current character's state," and the view owns the
/// load-on-switch + save-on-switch lifecycle.
/// </summary>
public sealed class PerCharacterView<T> : IDisposable
    where T : class, IVersionedState<T>, new()
{
    private readonly IActiveCharacterService _active;
    private readonly PerCharacterStore<T> _store;
    private readonly Lock _gate = new();

    private T? _cached;
    private (string Name, string Server)? _cachedKey;

    public PerCharacterView(IActiveCharacterService active, PerCharacterStore<T> store)
    {
        _active = active;
        _store = store;
        _active.ActiveCharacterChanged += OnActiveCharacterChanged;
    }

    /// <summary>
    /// State for the currently active character. <c>null</c> until both name and server
    /// are resolved. First access after construction or a character switch loads from disk.
    /// </summary>
    public T? Current
    {
        get
        {
            var name = _active.ActiveCharacterName;
            var server = _active.ActiveServer;
            if (string.IsNullOrEmpty(name) || string.IsNullOrEmpty(server)) return null;

            lock (_gate)
            {
                if (_cachedKey is { } key &&
                    string.Equals(key.Name, name, StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(key.Server, server, StringComparison.OrdinalIgnoreCase))
                {
                    return _cached;
                }

                var loaded = _store.Load(name, server);
                _cached = loaded;
                _cachedKey = (name, server);
                return loaded;
            }
        }
    }

    /// <summary>Persist the cached state for the currently loaded character.</summary>
    public void Save()
    {
        T? toSave;
        (string Name, string Server)? key;
        lock (_gate)
        {
            toSave = _cached;
            key = _cachedKey;
        }
        if (toSave is null || key is null) return;
        _store.Save(key.Value.Name, key.Value.Server, toSave);
    }

    /// <summary>Persist the cached state for the currently loaded character.</summary>
    public Task SaveAsync(CancellationToken ct = default)
    {
        T? toSave;
        (string Name, string Server)? key;
        lock (_gate)
        {
            toSave = _cached;
            key = _cachedKey;
        }
        if (toSave is null || key is null) return Task.CompletedTask;
        return _store.SaveAsync(key.Value.Name, key.Value.Server, toSave, ct);
    }

    /// <summary>Underlying store — exposed for cross-character access and tests.</summary>
    public PerCharacterStore<T> Store => _store;

    /// <summary>Fires when the active character changes. Subscribers should re-read <see cref="Current"/>.</summary>
    public event EventHandler? CurrentChanged;

    public void Dispose()
    {
        _active.ActiveCharacterChanged -= OnActiveCharacterChanged;
        FlushCached();
    }

    private void OnActiveCharacterChanged(object? sender, EventArgs e)
    {
        FlushCached();
        lock (_gate)
        {
            _cached = null;
            _cachedKey = null;
        }
        CurrentChanged?.Invoke(this, EventArgs.Empty);
    }

    private void FlushCached()
    {
        T? toSave;
        (string Name, string Server)? key;
        lock (_gate)
        {
            toSave = _cached;
            key = _cachedKey;
        }
        if (toSave is null || key is null) return;
        try { _store.Save(key.Value.Name, key.Value.Server, toSave); }
        catch { /* best-effort on switch/dispose */ }
    }
}
