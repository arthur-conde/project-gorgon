using Gandalf.Domain;
using Gorgon.Shared.Settings;

namespace Gandalf.Services;

public sealed class TimerStateService : IDisposable
{
    private readonly ISettingsStore<GandalfState> _store;
    private readonly System.Timers.Timer _debounce;
    private readonly Lock _flushLock = new();
    private bool _dirty;

    public List<GandalfTimer> Timers { get; private set; } = [];

    public event EventHandler? TimerChanged;
    public event EventHandler<GandalfTimer>? TimerExpired;

    public TimerStateService(ISettingsStore<GandalfState> store)
    {
        _store = store;
        _debounce = new System.Timers.Timer(500) { AutoReset = false };
        _debounce.Elapsed += (_, _) => Flush();
    }

    public void Load()
    {
        var state = _store.Load();
        Timers = state.Timers;
    }

    public async Task LoadAsync(CancellationToken ct = default)
    {
        var state = await _store.LoadAsync(ct).ConfigureAwait(false);
        Timers = state.Timers;
    }

    public void Add(GandalfTimer timer)
    {
        Timers.Add(timer);
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
        if (Timers.RemoveAll(t => t.Id == id) > 0)
        {
            MarkDirty();
            TimerChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    public void ClearCompleted()
    {
        if (Timers.RemoveAll(t => t.State == TimerState.Done) > 0)
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
            _store.Save(new GandalfState { Timers = Timers });
        }
    }

    public void Dispose()
    {
        _debounce.Stop();
        _debounce.Dispose();
        if (_dirty)
        {
            _dirty = false;
            try { Flush(); } catch { }
        }
    }
}
