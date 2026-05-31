using System;
using System.Threading;
using System.Threading.Tasks;
using Arda.Contracts;
using Arda.World.Player.Events;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Mithril.MapCalibration;

namespace Mithril.MapCalibration.Capture;

/// <summary>
/// Background auto-attempt trigger (spec §10). Subscribes to Arda's
/// <see cref="AreaChanged"/> and, on a zone-in, fires one auto-calibration
/// attempt <b>iff</b>:
/// <list type="bullet">
/// <item>a map capture bbox has been framed (<see cref="IMapCaptureRegionProvider.Current"/> != null), AND</item>
/// <item>the game is the foreground window (<see cref="IGameWindowLocator.Locate"/> != null), AND</item>
/// <item>the area is uncalibrated OR its active calibration is only a
/// <see cref="CalibrationSource.BundledBaseline"/> (an upgradeable fallback).</item>
/// </list>
///
/// <para><b>Never overwrites an existing <see cref="CalibrationSource.UserRefinement"/>
/// or <see cref="CalibrationSource.AutoCapture"/> on the auto path</b> — a
/// converged transform isn't re-attempted on every zone-in. (The manual
/// capture-&amp;-calibrate hotkey always attempts, by design.) Repeated
/// area-changed events for the SAME area are debounced.</para>
/// </summary>
public sealed class AutoCalibrationTrigger : IHostedService, IDisposable
{
    private readonly IDomainEventSubscriber _bus;
    private readonly IAutoCalibrationRunner _runner;
    private readonly IMapCaptureRegionProvider _region;
    private readonly IGameWindowLocator _windowLocator;
    private readonly IMapCalibrationService _calibrationService;
    private readonly ILogger? _logger;

    private IDisposable? _subscription;
    private string? _lastAttemptedArea;
    private readonly object _gate = new();

    public AutoCalibrationTrigger(
        IDomainEventSubscriber bus,
        IAutoCalibrationRunner runner,
        IMapCaptureRegionProvider region,
        IGameWindowLocator windowLocator,
        IMapCalibrationService calibrationService,
        ILogger? logger)
    {
        _bus = bus;
        _runner = runner;
        _region = region;
        _windowLocator = windowLocator;
        _calibrationService = calibrationService;
        _logger = logger;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _subscription = _bus.Subscribe<AreaChanged>(OnAreaChanged);
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        _subscription?.Dispose();
        _subscription = null;
        return Task.CompletedTask;
    }

    private void OnAreaChanged(AreaChanged e)
    {
        var area = e.CurrentArea;
        if (string.IsNullOrWhiteSpace(area)) return;
        // Fire-and-forget on the thread pool — the bus delivers synchronously on
        // the ingest thread, which must not block on a capture+solve.
        _ = Task.Run(() => OnAreaChangedAsync(area));
    }

    /// <summary>
    /// The gating decision, extracted for unit testing. Returns when the attempt
    /// completes (or is skipped). Awaited by the fire-and-forget path.
    /// </summary>
    internal async Task OnAreaChangedAsync(string area)
    {
        if (string.IsNullOrWhiteSpace(area)) return;

        // Debounce: collapse repeated area-changed events for the SAME area.
        lock (_gate)
        {
            if (string.Equals(_lastAttemptedArea, area, StringComparison.Ordinal)) return;
        }

        if (_region.Current is null) return;                 // no bbox → can't capture
        if (_windowLocator.Locate() is null) return;         // PG not foreground

        // Auto path only upgrades an uncalibrated area or a bundled baseline; it
        // never displaces a converged user/auto transform.
        var existing = _calibrationService.GetCalibration(area);
        if (existing is not null && existing.Source != CalibrationSource.BundledBaseline)
        {
            return;
        }

        lock (_gate) { _lastAttemptedArea = area; }

        _logger?.LogInformation("Auto-attempting calibration on zone-in to {Area}.", area);
        try
        {
            await _runner.TryCalibrateCurrentAreaAsync(CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Auto-calibration attempt for {Area} threw; the area stays as-is.", area);
        }
    }

    public void Dispose() => _subscription?.Dispose();
}
