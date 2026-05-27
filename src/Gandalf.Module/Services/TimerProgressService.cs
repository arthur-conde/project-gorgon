using Microsoft.Extensions.Logging;
using System.IO;
using Gandalf.Domain;
using Mithril.Shared.Character;
using Mithril.Shared.Diagnostics;
using Mithril.Shared.Game;

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
    private readonly ILogger? _logger;
    private readonly IGameClock _gameClock;
    private readonly TimeProvider _time;
    private readonly System.Timers.Timer _debounce;
    private readonly Lock _flushLock = new();
    private readonly HashSet<string> _expiredNotified = new(StringComparer.Ordinal);
    private bool _dirty;

    public TimerProgressService(
        PerCharacterView<GandalfProgress> view,
        TimerDefinitionsService defs,
        PerCharacterStoreOptions storeOptions,
        ILogger? logger = null,
        IGameClock? gameClock = null,
        TimeProvider? time = null)
    {
        _view = view;
        _defs = defs;
        _storeOptions = storeOptions;
        _logger = logger;
        _time = time ?? TimeProvider.System;
        _gameClock = gameClock ?? new GameClock(_time);
        _view.CurrentChanged += OnCurrentChanged;
        _debounce = new System.Timers.Timer(500) { AutoReset = false };
        _debounce.Elapsed += (_, _) => Flush();
        // Initial rehydrate — covers the case where a character was already
        // active when the service was constructed (no CurrentChanged fires
        // for that). Without this, a Running game-clock timer loaded from
        // disk would have FiringAt=null until the user switched characters.
        RehydrateFiringAt();
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

        // Wall-clock per principle-13 user-action carve-out (Tier A): StartedAt feeds
        // UI elapsed/remaining display via TimerView, so it anchors in the user's
        // perceived "now," not the world clock. See docs/world-simulator.md §Decisions
        // ratified post-#642.
        var startedAt = _time.GetUtcNow();
        progress.StartedAt = startedAt;
        progress.CompletedAt = null;
        progress.FiringAt = ComputeFiringAt(def, startedAt, _gameClock);
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

        // Wall-clock per principle-13 user-action carve-out (Tier A): StartedAt feeds
        // UI elapsed/remaining display via TimerView, so it anchors in the user's
        // perceived "now," not the world clock. See docs/world-simulator.md §Decisions
        // ratified post-#642.
        var startedAt = _time.GetUtcNow();
        progress.StartedAt = startedAt;
        progress.CompletedAt = null;
        progress.FiringAt = ComputeFiringAt(def, startedAt, _gameClock);
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
        progress.FiringAt = null;
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
                    _logger?.LogDiagnosticWarn("Gandalf.Progress", $"Failed to delete {path}: {ex.Message}");
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
            progress.FiringAt = null;
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
    /// the same lifecycle — each id fires at most once per run cycle. For
    /// <see cref="GandalfTriggerKind.GameTimeOfDay"/> timers with
    /// <see cref="GandalfTimerDef.Recurring"/>, the row is re-armed to the next
    /// in-game day instead of latching <c>CompletedAt</c> — the alarm event still
    /// fires so consumers (TimerAlarmService) ring on each cycle.
    /// </summary>
    public void CheckExpirations() => CheckExpirations(_time.GetUtcNow());

    /// <summary>
    /// Overload that takes the comparison "now" explicitly. The Gandalf
    /// scheduler-collapse migration (#613, world-sim item #12) routes
    /// <see cref="Mithril.WorldSim.CalendarTimeAdvanced"/> ticks here via
    /// <see cref="TimerExpirationDriver"/> with the event's <c>Now</c> —
    /// replay-deterministic per principle 13 ("calendar time is a domain
    /// event, not a clock read"). The no-arg overload above stays for tests
    /// that drive a <see cref="TimeProvider"/> directly without a world.
    /// </summary>
    public void CheckExpirations(DateTimeOffset now)
    {
        var current = _view.Current;
        if (current is null) return;

        foreach (var (id, progress) in current.ByTimerId)
        {
            var def = _defs.Definitions.FirstOrDefault(d => d.Id == id);
            if (def is null) continue;
            if (progress.StartedAt is null || progress.CompletedAt is not null) continue;

            var firingAt = progress.FiringAt ?? progress.StartedAt.Value + def.Duration;
            if (now < firingAt) continue;

            var firstFire = _expiredNotified.Add(id);

            if (def.Kind == GandalfTriggerKind.GameTimeOfDay && def.Recurring)
            {
                // Fire the alarm event for the just-completed run *before* mutating
                // — handlers run synchronously and read the pre-rearm state. Then
                // re-arm in place: anchor at now (so the next cycle is computed
                // against current real time, skipping any cycles missed while the
                // app was suspended), and reset _expiredNotified so the *next*
                // fire is allowed. Forgetting that reset silently swallows the
                // second fire, which is the regression test in the recurring path.
                if (firstFire)
                    TimerExpired?.Invoke(this, new TimerExpiredEventArgs(def, progress));

                progress.StartedAt = now;
                progress.CompletedAt = null;
                progress.FiringAt = ComputeFiringAt(def, now, _gameClock);
                _expiredNotified.Remove(id);
                MarkDirty();
            }
            else
            {
                progress.CompletedAt = now;
                MarkDirty();

                if (firstFire)
                    TimerExpired?.Invoke(this, new TimerExpiredEventArgs(def, progress));
            }
        }
    }

    /// <summary>
    /// Recompute <see cref="TimerProgress.FiringAt"/> for every Running entry on
    /// the active character. Called on construction and on character-switch.
    /// FiringAt is <c>[JsonIgnore]</c> so it isn't carried across app restarts —
    /// rehydrating here is what restores it.
    /// </summary>
    private void RehydrateFiringAt()
    {
        var current = _view.Current;
        if (current is null) return;

        foreach (var (id, progress) in current.ByTimerId)
        {
            if (progress.StartedAt is null || progress.CompletedAt is not null) continue;
            var def = _defs.Definitions.FirstOrDefault(d => d.Id == id);
            if (def is null) continue;
            progress.FiringAt = ComputeFiringAt(def, progress.StartedAt.Value, _gameClock);
        }
    }

    /// <summary>
    /// Wall-clock instant at which a Running timer of this <paramref name="def"/>
    /// fires, given when it started. Single dispatch point for the
    /// <see cref="GandalfTriggerKind.Countdown"/>/<see cref="GandalfTriggerKind.GameTimeOfDay"/>
    /// branch — every other code path reads the cached
    /// <see cref="TimerProgress.FiringAt"/>.
    /// </summary>
    public static DateTimeOffset ComputeFiringAt(
        GandalfTimerDef def, DateTimeOffset startedAt, IGameClock gameClock)
    {
        return def.Kind switch
        {
            GandalfTriggerKind.GameTimeOfDay => gameClock.NextOccurrence(
                new GameTimeOfDay(def.GameHour ?? 0, def.GameMinute ?? 0),
                startedAt),
            _ => startedAt + def.Duration,
        };
    }

    private void OnCurrentChanged(object? sender, EventArgs e)
    {
        // Each character has its own notification ledger — clear on switch.
        _expiredNotified.Clear();
        // Restore FiringAt for the new active character's running timers — it
        // isn't persisted, and downstream consumers (scheduler, TimerView, row
        // render) all dispatch off it.
        RehydrateFiringAt();
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
