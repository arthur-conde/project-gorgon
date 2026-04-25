using Gandalf.Domain;
using Mithril.Shared.Settings;

namespace Gandalf.Services;

/// <summary>
/// Owns the global <see cref="GandalfDefinitions"/> list (every character sees the same
/// set of timers). Per-timer runtime progress is a separate concern, owned by
/// <see cref="TimerProgressService"/>.
/// </summary>
public sealed class TimerDefinitionsService : IDisposable
{
    private readonly ISettingsStore<GandalfDefinitions> _store;
    private readonly GandalfDefinitions _defs;
    private readonly System.Timers.Timer _debounce;
    private readonly Lock _flushLock = new();
    private bool _dirty;

    public TimerDefinitionsService(
        ISettingsStore<GandalfDefinitions> store,
        GandalfDefinitions defs)
    {
        _store = store;
        _defs = defs;
        _debounce = new System.Timers.Timer(500) { AutoReset = false };
        _debounce.Elapsed += (_, _) => Flush();
    }

    /// <summary>All known timer definitions. Shared across every character.</summary>
    public IReadOnlyList<GandalfTimerDef> Definitions => _defs.Timers;

    /// <summary>Fires on any add/update/remove/clear.</summary>
    public event EventHandler? DefinitionsChanged;

    public void Add(GandalfTimerDef def)
    {
        _defs.Timers.Add(def);
        MarkDirty();
        DefinitionsChanged?.Invoke(this, EventArgs.Empty);
    }

    public void Update(string id, Action<GandalfTimerDef> mutate)
    {
        var def = _defs.Timers.Find(d => d.Id == id);
        if (def is null) return;
        mutate(def);
        MarkDirty();
        DefinitionsChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// Remove the definition from the shared list. Per-character progress entries for this
    /// id are not touched here — the progress service garbage-collects orphan entries on its
    /// next dirty-flush cycle, and the UI never renders progress without a matching def.
    /// </summary>
    public void Remove(string id)
    {
        if (_defs.Timers.RemoveAll(d => d.Id == id) > 0)
        {
            MarkDirty();
            DefinitionsChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <summary>Wipe every definition. Used by the settings-level "Delete All Timers" action.</summary>
    public void ClearAll()
    {
        if (_defs.Timers.Count == 0) return;
        _defs.Timers.Clear();
        MarkDirty();
        DefinitionsChanged?.Invoke(this, EventArgs.Empty);
    }

    private void MarkDirty()
    {
        _dirty = true;
        _debounce.Stop();
        _debounce.Start();
    }

    private void Flush()
    {
        lock (_flushLock)
        {
            if (!_dirty) return;
            _dirty = false;
            try { _store.Save(_defs); } catch { /* best-effort */ }
        }
    }

    public void Dispose()
    {
        _debounce.Stop();
        _debounce.Dispose();
        if (_dirty) { try { _store.Save(_defs); } catch { } }
    }
}
