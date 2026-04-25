using System.ComponentModel;
using System.Windows.Threading;

namespace Mithril.Shared.Settings;

public sealed class SettingsAutoSaver<T> : IDisposable where T : class, INotifyPropertyChanged, new()
{
    private readonly ISettingsStore<T> _store;
    private readonly T _instance;
    private readonly DispatcherTimer _timer;
    private bool _dirty;

    public SettingsAutoSaver(ISettingsStore<T> store, T instance, TimeSpan? debounce = null)
    {
        _store = store;
        _instance = instance;
        _timer = new DispatcherTimer { Interval = debounce ?? TimeSpan.FromMilliseconds(500) };
        _timer.Tick += OnTick;
        _instance.PropertyChanged += OnChanged;
    }

    private void OnChanged(object? sender, PropertyChangedEventArgs e)
    {
        _dirty = true;
        if (!_timer.IsEnabled) _timer.Start();
    }

    /// <summary>Mark settings dirty explicitly; used when a change happens
    /// outside the INotifyPropertyChanged graph (e.g. window layout drag).</summary>
    public void Touch()
    {
        _dirty = true;
        if (!_timer.IsEnabled) _timer.Start();
    }

    private void OnTick(object? sender, EventArgs e)
    {
        _timer.Stop();
        if (!_dirty) return;
        _dirty = false;
        try { _store.Save(_instance); }
        catch { /* best-effort autosave */ }
    }

    public void Dispose()
    {
        _timer.Stop();
        _timer.Tick -= OnTick;
        _instance.PropertyChanged -= OnChanged;
        if (_dirty) try { _store.Save(_instance); } catch { }
    }
}
