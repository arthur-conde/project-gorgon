using Microsoft.Extensions.Logging;
using System.ComponentModel;
using System.Windows.Threading;
using Microsoft.Extensions.Hosting;

namespace Mithril.Shared.Settings;

public sealed class SettingsAutoSaver<T> : IHostedService, IDisposable where T : class, INotifyPropertyChanged, new()
{
    private readonly ISettingsStore<T> _store;
    private readonly T _instance;
    private readonly ILogger? _logger;
    private readonly DispatcherTimer _timer;
    private bool _dirty;

    /// <summary>
    /// Raised after each successful flush. UI can subscribe to surface a
    /// "Saved" indicator. Fires on the dispatcher thread (the saver is
    /// dispatcher-bound via its timer) so handlers can touch UI directly.
    /// </summary>
    public event Action<DateTimeOffset>? Saved;

    public SettingsAutoSaver(
        ISettingsStore<T> store,
        T instance,
        ILogger? logger = null,
        TimeSpan? debounce = null)
    {
        _store = store;
        _instance = instance;
        _logger = logger;
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
        try
        {
            _store.Save(_instance);
            Saved?.Invoke(DateTimeOffset.Now);
        }
        catch (Exception ex)
        {
            // Best-effort by design — a failed autosave shouldn't crash
            // the host — but no longer silent. Without this surface every
            // serialization / IO error in a settings type was invisible
            // (same foot-gun that hid the GandalfShiftSettings nested-INPC
            // bug for as long as it did).
            _logger?.LogWarning(ex, "Save failed for {SettingsType}: {ExceptionType}", typeof(T).Name, ex.GetType().Name);
        }
    }

    public void Dispose()
    {
        _timer.Stop();
        _timer.Tick -= OnTick;
        _instance.PropertyChanged -= OnChanged;
    }
}
