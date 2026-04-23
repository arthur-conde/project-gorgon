using Gandalf.Domain;
using Gorgon.Shared.Character;

namespace Gandalf.Services;

/// <summary>
/// Owns the timer list for the currently active character. Reads/writes through
/// <see cref="PerCharacterView{T}"/>; on a character switch the list is swapped to the
/// new character's timers and <see cref="TimerChanged"/> fires so the VM re-syncs.
/// </summary>
public sealed class TimerStateService : IDisposable
{
    private readonly PerCharacterView<GandalfState> _view;
    private readonly System.Timers.Timer _debounce;
    private readonly Lock _flushLock = new();
    private bool _dirty;

    public List<GandalfTimer> Timers => _view.Current?.Timers ?? [];

    public event EventHandler? TimerChanged;
    public event EventHandler<GandalfTimer>? TimerExpired;

    public TimerStateService(PerCharacterView<GandalfState> view)
    {
        _view = view;
        _view.CurrentChanged += OnCurrentChanged;
        _debounce = new System.Timers.Timer(500) { AutoReset = false };
        _debounce.Elapsed += (_, _) => Flush();
    }

    private void OnCurrentChanged(object? sender, EventArgs e)
        => TimerChanged?.Invoke(this, EventArgs.Empty);

    public void Add(GandalfTimer timer)
    {
        var current = _view.Current;
        if (current is null) return;
        current.Timers.Add(timer);
        MarkDirty();
        TimerChanged?.Invoke(this, EventArgs.Empty);
    }

    public void Start(string id)
    {
        var timer = Timers.Find(t => t.Id == id);
        if (timer is null || timer.State != TimerState.Idle) return;
        timer.Start();
        SaveNow();
        TimerChanged?.Invoke(this, EventArgs.Empty);
    }

    public void Restart(string id)
    {
        var timer = Timers.Find(t => t.Id == id);
        if (timer is null || timer.State != TimerState.Done) return;
        timer.Restart();
        SaveNow();
        TimerChanged?.Invoke(this, EventArgs.Empty);
    }

    public void Update(string id, Action<GandalfTimer> mutate)
    {
        var timer = Timers.Find(t => t.Id == id);
        if (timer is null) return;
        mutate(timer);
        MarkDirty();
        TimerChanged?.Invoke(this, EventArgs.Empty);
    }

    public void Remove(string id)
    {
        var current = _view.Current;
        if (current is null) return;
        if (current.Timers.RemoveAll(t => t.Id == id) > 0)
        {
            MarkDirty();
            TimerChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    public void ClearCompleted()
    {
        var current = _view.Current;
        if (current is null) return;
        if (current.Timers.RemoveAll(t => t.State == TimerState.Done) > 0)
        {
            MarkDirty();
            TimerChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    public void CheckExpirations()
    {
        foreach (var t in Timers)
        {
            if (t.StartedAt is not null && t.CompletedAt is null && t.State == TimerState.Done)
            {
                t.CompletedAt = DateTimeOffset.UtcNow;
                MarkDirty();
                TimerExpired?.Invoke(this, t);
            }
        }
    }

    /// <summary>Debounced save — coalesces rapid mutations (add, remove, edit).</summary>
    private void MarkDirty()
    {
        _dirty = true;
        _debounce.Stop();
        _debounce.Start();
    }

    /// <summary>Immediate save — bypasses debounce for state transitions that must survive a crash.</summary>
    private void SaveNow()
    {
        _debounce.Stop();
        _dirty = false;
        Flush();
    }

    private void Flush()
    {
        lock (_flushLock)
        {
            try { _view.Save(); } catch { /* best-effort */ }
        }
    }

    public void Dispose()
    {
        _view.CurrentChanged -= OnCurrentChanged;
        _debounce.Stop();
        _debounce.Dispose();
        if (_dirty)
        {
            _dirty = false;
            try { Flush(); } catch { }
        }
    }
}
