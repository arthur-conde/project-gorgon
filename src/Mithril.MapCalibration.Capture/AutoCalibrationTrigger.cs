using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Arda.Contracts;
using Arda.World.Player.Events;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Mithril.MapCalibration;
using Mithril.Overlay;

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
/// capture-&amp;-calibrate hotkey always attempts, by design.)</para>
///
/// <para><b>Retry-on-re-entry (GATE-2 Fix C).</b> An area is marked "done"
/// (suppressing re-attempt) ONLY when the attempt persisted a transform. A
/// non-persisted outcome (e.g. "no bbox", "not zoomed out") leaves the area
/// un-marked, so a genuine later re-entry — the user zones out, zooms the map
/// properly, zones back — gets a fresh attempt. An in-flight guard prevents a
/// burst of duplicate area-changed events from launching concurrent/looping
/// attempts; there is no timer/polling loop, retries happen only on fresh
/// area-change events.</para>
///
/// <para>On a non-persisted, <i>actionable</i> reject the trigger surfaces the
/// reason on the overlay status chip (spec §10/§11) so the user learns why
/// auto-cal isn't engaging; a persisted success clears the chip silently.</para>
/// </summary>
public sealed class AutoCalibrationTrigger : IHostedService, IDisposable
{
    private readonly IDomainEventSubscriber _bus;
    private readonly IAutoCalibrationRunner _runner;
    private readonly IMapCaptureRegionProvider _region;
    private readonly IGameWindowLocator _windowLocator;
    private readonly IMapCalibrationService _calibrationService;
    private readonly IOverlayWindow _overlay;
    private readonly ILogger _logger;

    private IDisposable? _subscription;
    private readonly object _gate = new();
    // Areas whose auto-attempt PERSISTED — never re-attempted (Fix C).
    private readonly HashSet<string> _persistedAreas = new(StringComparer.Ordinal);
    // Areas with an attempt currently running — skip duplicate concurrent launches
    // (the in-flight guard against a retry storm; Fix C).
    private readonly HashSet<string> _inFlightAreas = new(StringComparer.Ordinal);

    public AutoCalibrationTrigger(
        IDomainEventSubscriber bus,
        IAutoCalibrationRunner runner,
        IMapCaptureRegionProvider region,
        IGameWindowLocator windowLocator,
        IMapCalibrationService calibrationService,
        IOverlayWindow overlay,
        ILogger logger)
    {
        _bus = bus;
        _runner = runner;
        _region = region;
        _windowLocator = windowLocator;
        _calibrationService = calibrationService;
        _overlay = overlay;
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

        lock (_gate)
        {
            // Already persisted for this area → never re-attempt (Fix C).
            if (_persistedAreas.Contains(area)) return;
            // An attempt is already running for this area → skip the duplicate so
            // a burst of area-changed events can't launch a concurrent/looping
            // attempt (in-flight guard, Fix C).
            if (!_inFlightAreas.Add(area)) return;
        }

        try
        {
            if (_region.Current is null) return;                 // no bbox → can't capture
            if (_windowLocator.Locate() is null) return;         // PG not foreground

            // Auto path only upgrades an uncalibrated area or a bundled baseline; it
            // never displaces a converged user/auto transform.
            var existing = _calibrationService.GetCalibration(area);
            if (existing is not null && existing.Source != CalibrationSource.BundledBaseline)
            {
                return;
            }

            _logger.LogInformation("Auto-attempting calibration on zone-in to {Area}.", area);
            AutoCalibrationOutcome? outcome = null;
            try
            {
                outcome = await _runner.TryCalibrateCurrentAreaAsync(CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Auto-calibration attempt for {Area} threw; the area stays as-is.", area);
            }

            if (outcome is null) return; // threw → leave un-marked so a later re-entry retries

            if (outcome.Persisted)
            {
                lock (_gate) { _persistedAreas.Add(area); }
                // Silent upgrade (spec §10): a successful auto-persist clears any
                // prior status chip. Idempotent on the concrete overlay.
                _overlay.SetStatusMessage(null);
            }
            else
            {
                // Non-persisted → leave the area un-marked so a genuine later
                // re-entry retries (Fix C). Surface the actionable reason so the
                // user learns why auto-cal isn't engaging (spec §10/§11). Setting
                // the same string is idempotent (the concrete overlay no-ops it).
                _overlay.SetStatusMessage(CalibrationStatusFormatter.ForOutcome(outcome));
            }
        }
        finally
        {
            lock (_gate) { _inFlightAreas.Remove(area); }
        }
    }

    public void Dispose() => _subscription?.Dispose();
}
