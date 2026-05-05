using System.ComponentModel;
using System.Windows.Threading;
using Microsoft.Extensions.Hosting;

namespace Mithril.Shared.Settings;

public sealed class SettingsAutoSaver<T> : IHostedService, IDisposable where T : class, INotifyPropertyChanged, new()
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

    public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    /// <summary>Flush any pending dirty state synchronously on graceful shutdown
    /// — closes the small data-loss window where the user edits a setting and
    /// closes the app within the debounce interval (500ms by default).</summary>
    public Task StopAsync(CancellationToken cancellationToken)
    {
        _timer.Stop();
        FlushIfDirty();
        return Task.CompletedTask;
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
        FlushIfDirty();
    }

    private void FlushIfDirty()
    {
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
    }
}
