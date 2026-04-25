using System.ComponentModel;
using Microsoft.Extensions.Hosting;

namespace Mithril.Shell.Updates;

public sealed class UpdateCheckHostedService : BackgroundService
{
    private static readonly TimeSpan InitialDelay = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan MinInterval = TimeSpan.FromMinutes(15);
    private static readonly TimeSpan MaxInterval = TimeSpan.FromDays(7);

    private readonly IUpdateChecker _checker;
    private readonly ShellSettings _settings;
    private readonly object _wakeGate = new();
    private TaskCompletionSource? _intervalChanged;

    public UpdateCheckHostedService(IUpdateChecker checker, ShellSettings settings)
    {
        _checker = checker;
        _settings = settings;
        _settings.PropertyChanged += OnSettingsChanged;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try { await Task.Delay(InitialDelay, stoppingToken).ConfigureAwait(false); }
        catch (OperationCanceledException) { return; }

        while (!stoppingToken.IsCancellationRequested)
        {
            await _checker.CheckAsync(stoppingToken).ConfigureAwait(false);
            if (!await WaitForNextTickAsync(stoppingToken).ConfigureAwait(false)) return;
        }
    }

    private async Task<bool> WaitForNextTickAsync(CancellationToken ct)
    {
        TaskCompletionSource waiter;
        lock (_wakeGate)
        {
            _intervalChanged = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            waiter = _intervalChanged;
        }

        var interval = CurrentInterval();
        var delay = Task.Delay(interval, ct);
        try
        {
            var completed = await Task.WhenAny(delay, waiter.Task).ConfigureAwait(false);
            if (ct.IsCancellationRequested) return false;
            // If the interval setting changed mid-wait we just loop around and recompute —
            // we intentionally don't re-check immediately; user-initiated "Check now" has its
            // own path via IUpdateChecker.CheckAsync.
            _ = completed;
            return true;
        }
        catch (OperationCanceledException) { return false; }
    }

    private TimeSpan CurrentInterval()
    {
        var hours = _settings.UpdateCheckIntervalHours;
        if (double.IsNaN(hours) || hours <= 0) hours = 4.0;
        var span = TimeSpan.FromHours(hours);
        if (span < MinInterval) span = MinInterval;
        if (span > MaxInterval) span = MaxInterval;
        return span;
    }

    private void OnSettingsChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(ShellSettings.UpdateCheckIntervalHours)) return;
        TaskCompletionSource? waiter;
        lock (_wakeGate) { waiter = _intervalChanged; }
        waiter?.TrySetResult();
    }

    public override void Dispose()
    {
        _settings.PropertyChanged -= OnSettingsChanged;
        base.Dispose();
    }
}
