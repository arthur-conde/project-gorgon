using System.IO;
using Legolas.Domain;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Mithril.MapCalibration;

namespace Legolas.Services;

/// <summary>
/// One-time on-start import of <see cref="LegolasSettings.AreaCalibrations"/>
/// (the legacy per-user calibration store) into
/// <see cref="IMapCalibrationService"/>'s user-refinement store. Required by
/// the lift in #836 so a user upgrading from a prior Mithril keeps every area
/// they've already calibrated.
///
/// <para>Idempotent across restarts: skips any area where the shared service
/// already reports an active <see cref="CalibrationSource.UserRefinement"/>.
/// During the transition window <see cref="AreaCalibrationService.CalibrateCurrentArea"/>
/// dual-writes to both stores, so settings and service stay in sync; this
/// import path only ever has work to do on the very first run after the lift
/// (or after a rollback that wrote to the legacy field while the new store
/// was offline).</para>
/// </summary>
internal sealed class LegolasAreaCalibrationMigration : IHostedService
{
    private readonly LegolasSettings _settings;
    private readonly IMapCalibrationService _mapCal;
    private readonly ILogger? _logger;

    public LegolasAreaCalibrationMigration(
        LegolasSettings settings,
        IMapCalibrationService mapCal,
        ILoggerFactory? loggerFactory = null)
    {
        _settings = settings;
        _mapCal = mapCal;
        _logger = loggerFactory?.CreateLogger("Legolas.MapCalibrationMigration");
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        if (_settings.AreaCalibrations is null || _settings.AreaCalibrations.Count == 0)
            return Task.CompletedTask;

        // Delegate the per-entry skip-vs-import-vs-overwrite decision to the
        // service. ImportUserRefinements is silent + batched (one persist, no
        // Changed events), so the host.StartAsync chain doesn't pay N disk
        // writes and doesn't broadcast N cross-thread events to whatever
        // happens to be subscribed at the time. The "skip-if-already-imported"
        // gate is also moved there so it can check the user store directly,
        // not the precedence-aware GetCalibration (which would re-import any
        // entry whose user refinement lost to a baseline on every cold start
        // once the baseline JSON is populated).
        try
        {
            var imported = _mapCal.ImportUserRefinements(_settings.AreaCalibrations);
            if (imported > 0)
            {
                _logger?.LogInformation(
                    "Imported {Imported}/{Total} area calibrations from LegolasSettings into the shared user-refinement store.",
                    imported, _settings.AreaCalibrations.Count);
            }
        }
        catch (IOException ex)
        {
            // Persist threw (disk full / AV lock / OneDrive placeholder). Log
            // and continue startup — the legacy store still holds the data, so
            // a later restart can retry. Better to launch the shell without
            // the import than to deny startup over a transient IO blip.
            _logger?.LogWarning(ex,
                "Failed to persist user refinements during legacy import; will retry on next startup.");
        }
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
