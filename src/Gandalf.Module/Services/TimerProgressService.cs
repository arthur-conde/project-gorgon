using System.IO;
using Gandalf.Domain;
using Mithril.Shared.Character;
using Mithril.Shared.Diagnostics;

namespace Gandalf.Services;

/// <summary>
/// Event payload for timer expiration. Carries both the definition (for alarm formatting)
/// and the progress row (for catching the completion timestamp if callers need it).
/// </summary>
public sealed class TimerExpiredEventArgs : EventArgs
{
    public TimerExpiredEventArgs(GandalfTimerDef def, TimerProgress progress)
    {
        Def = def;
        Progress = progress;
    }

    public GandalfTimerDef Def { get; }
    public TimerProgress Progress { get; }
}

/// <summary>
/// Owns the per-character <see cref="GandalfProgress"/> map and provides the Start/Restart/
/// Reset transitions the UI invokes. The active character is implicit — the underlying
/// <see cref="PerCharacterView{T}"/> swaps <c>Current</c> on character-switch and we
/// re-fire <see cref="ProgressChanged"/> so the VM rebuilds its list.
/// </summary>
public sealed class TimerProgressService : IDisposable
{
    private readonly PerCharacterView<GandalfProgress> _view;
    private readonly TimerDefinitionsService _defs;
    private readonly PerCharacterStoreOptions _storeOptions;
    private readonly IDiagnosticsSink? _diag;
    private readonly System.Timers.Timer _debounce;
    private readonly Lock _flushLock = new();
    private readonly HashSet<string> _expiredNotified = new(StringComparer.Ordinal);
    private bool _dirty;

    public TimerProgressService(
        PerCharacterView<GandalfProgress> view,
        TimerDefinitionsService defs,
        PerCharacterStoreOptions storeOptions,
        IDiagnosticsSink? diag = null)
    {
        _view = view;
        _defs = defs;
        _storeOptions = storeOptions;
        _diag = diag;
        _view.CurrentChanged += OnCurrentChanged;
        _debounce = new System.Timers.Timer(500) { AutoReset = false };
        _debounce.Elapsed += (_, _) => Flush();
    }

    /// <summary>Progress for a specific timer id on the active character, or null if none.</summary>
    public TimerProgress? GetProgress(string id)
    {
        var current = _view.Current;
        if (current is null || string.IsNullOrEmpty(id)) return null;
        return current.ByTimerId.TryGetValue(id, out var p) ? p : null;
    }

    /// <summary>Fires on mutation or character-switch.</summary>
    public event EventHandler? ProgressChanged;

    /// <summary>Fires once per transition into <see cref="TimerState.Done"/>.</summary>
    public event EventHandler<TimerExpiredEventArgs>? TimerExpired;

    public void Start(string id)
    {
        var current = _view.Current;
        if (current is null) return;
        var def = _defs.Definitions.FirstOrDefault(d => d.Id == id);
        if (def is null) return;

        var progress = EnsureProgress(current, id);
        var view = new TimerView(def, progress);
        if (view.State != TimerState.Idle) return;

        progress.StartedAt = DateTimeOffset.UtcNow;
        progress.CompletedAt = null;
        _expiredNotified.Remove(id);
        SaveNow();
        ProgressChanged?.Invoke(this, EventArgs.Empty);
    }

    public void Restart(string id)
    {
        var current = _view.Current;
        if (current is null) return;
        var def = _defs.Definitions.FirstOrDefault(d => d.Id == id);
        if (def is null) return;

        var progress = EnsureProgress(current, id);
        var view = new TimerView(def, progress);
        if (view.State != TimerState.Done) return;

        progress.StartedAt = DateTimeOffset.UtcNow;
        progress.CompletedAt = null;
        _expiredNotified.Remove(id);
        SaveNow();
        ProgressChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Null out StartedAt/CompletedAt for a single id on the active character.</summary>
    public void Reset(string id)
    {
        var current = _view.Current;
        if (current is null) return;
        if (!current.ByTimerId.TryGetValue(id, out var progress)) return;
        if (progress.StartedAt is null && progress.CompletedAt is null) return;
        progress.StartedAt = null;
        progress.CompletedAt = null;
        _expiredNotified.Remove(id);
        MarkDirty();
        ProgressChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// Delete every character's progress file. Invalidates the view first so a racy
    /// <see cref="MarkDirty"/> can't resurrect a just-deleted file. Used by the settings-level
    /// "Delete All Timers" action alongside <see cref="TimerDefinitionsService.ClearAll"/>.
    /// </summary>
    public void ClearAllProgressForAllCharacters()
    {
        lock (_flushLock)
        {
            _dirty = false;
            _debounce.Stop();
            _view.Invalidate();

            var root = _storeOptions.CharactersRootDir;
            if (!Directory.Exists(root))
            {
                ProgressChanged?.Invoke(this, EventArgs.Empty);
                return;
            }

            foreach (var charDir in Directory.EnumerateDirectories(root))
            {
                var path = Path.Combine(charDir, "gandalf.json");
                if (!File.Exists(path)) continue;
                try { File.Delete(path); }
                catch (Exception ex)
                {
                    _diag?.Warn("Gandalf.Progress", $"Failed to delete {path}: {ex.Message}");
                }
            }

            _expiredNotified.Clear();
        }
        ProgressChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// Reset progress on every Done timer for the active character. Definitions untouched;
    /// orphan progress entries (no matching def) are also dropped in passing. Used by the
    /// list view's "Clear done" action — "done" meaning the timer finished, not that the
    /// definition should be deleted.
    /// </summary>
    public void ClearAllDoneOnActive()
    {
        var current = _view.Current;
        if (current is null) return;

        var defsById = _defs.Definitions.ToDictionary(d => d.Id, StringComparer.Ordinal);
        var changed = false;

        foreach (var id in current.ByTimerId.Keys.ToArray())
        {
            var progress = current.ByTimerId[id];
            if (!defsById.TryGetValue(id, out var def))
            {
                // Orphan — strip it.
                current.ByTimerId.Remove(id);
                _expiredNotified.Remove(id);
                changed = true;
                continue;
            }

            var state = new TimerView(def, progress).State;
            if (state != TimerState.Done) continue;

            progress.StartedAt = null;
            progress.CompletedAt = null;
            _expiredNotified.Remove(id);
            changed = true;
        }

        if (!changed) return;
        MarkDirty();
        ProgressChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// Scan the active character's progress; stamp <c>CompletedAt</c> and fire
    /// <see cref="TimerExpired"/> for any running-but-past-due timers. Idempotent within
    /// the same lifecycle — each id fires at most once per run cycle.
    /// </summary>
    public void CheckExpirations()
    {
        var current = _view.Current;
        if (current is null) return;

        foreach (var (id, progress) in current.ByTimerId)
        {
            var def = _defs.Definitions.FirstOrDefault(d => d.Id == id);
            if (def is null) continue;

            var view = new TimerView(def, progress);
            if (progress.StartedAt is null || progress.CompletedAt is not null) continue;
            if (view.State != TimerState.Done) continue;

            progress.CompletedAt = DateTimeOffset.UtcNow;
            MarkDirty();

            if (_expiredNotified.Add(id))
                TimerExpired?.Invoke(this, new TimerExpiredEventArgs(def, progress));
        }
    }

    private void OnCurrentChanged(object? sender, EventArgs e)
    {
        // Each character has its own notification ledger — clear on switch.
        _expiredNotified.Clear();
        ProgressChanged?.Invoke(this, EventArgs.Empty);
    }

    private TimerProgress EnsureProgress(GandalfProgress current, string id)
    {
        if (!current.ByTimerId.TryGetValue(id, out var progress))
        {
            progress = new TimerProgress();
            current.ByTimerId[id] = progress;
        }
        return progress;
    }

    private void MarkDirty()
    {
        _dirty = true;
        _debounce.Stop();
        _debounce.Start();
    }

    /// <summary>Immediate save — bypasses debounce for transitions that must survive a crash.</summary>
    private void SaveNow()
    {
        _debounce.Stop();
        _dirty = false;
        FlushCore();
    }

    private void Flush()
    {
        lock (_flushLock)
        {
            if (!_dirty) return;
            _dirty = false;
            FlushCore();
        }
    }

    private void FlushCore()
    {
        try
        {
            StripOrphans();
            _view.Save();
        }
        catch { /* best-effort */ }
    }

    /// <summary>
    /// Remove progress entries whose id is no longer in the shared definitions list.
    /// Timer ids are GUIDs, so collisions are impossible — safe to GC on write.
    /// </summary>
    private void StripOrphans()
    {
        var current = _view.Current;
        if (current is null) return;
        var keep = new HashSet<string>(_defs.Definitions.Select(d => d.Id), StringComparer.Ordinal);
        foreach (var id in current.ByTimerId.Keys.Where(k => !keep.Contains(k)).ToArray())
            current.ByTimerId.Remove(id);
    }

    public void Dispose()
    {
        _view.CurrentChanged -= OnCurrentChanged;
        _debounce.Stop();
        _debounce.Dispose();
        if (_dirty) { try { FlushCore(); } catch { } }
    }
}
