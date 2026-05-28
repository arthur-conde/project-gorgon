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

        var imported = 0;
        foreach (var (key, cal) in _settings.AreaCalibrations)
        {
            if (cancellationToken.IsCancellationRequested) break;

            // Skip if the shared service already exposes this area as a user
            // refinement: either we imported it on a previous run, or the user
            // re-calibrated through the new path and the dual-write covered it.
            if (_mapCal.GetCalibration(key) is { Source: CalibrationSource.UserRefinement })
                continue;

            _mapCal.SaveUserRefinement(key, cal);
            imported++;
        }

        if (imported > 0)
        {
            _logger?.LogInformation(
                "Imported {Imported}/{Total} area calibrations from LegolasSettings into the shared user-refinement store.",
                imported, _settings.AreaCalibrations.Count);
        }
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
