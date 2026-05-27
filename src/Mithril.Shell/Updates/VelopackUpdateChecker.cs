using Microsoft.Extensions.Logging;
using Mithril.Shared;

namespace Mithril.Shell.Updates;

public sealed class VelopackUpdateChecker : IUpdateChecker
{
    private readonly MithrilUpdateManager _holder;
    private readonly IUpdateStatusService _status;
    private readonly ILogger _logger;

    public VelopackUpdateChecker(MithrilUpdateManager holder, IUpdateStatusService status, ILogger logger)
    {
        _holder = holder;
        _status = status;
        _logger = logger;
    }

    public async Task CheckAsync(CancellationToken ct)
    {
        if (!_holder.IsAvailable)
        {
            _status.ReportNotApplicable();
            _logger.LogInformation($"Skipping update check — channel '{_holder.Channel.Name}' is not subject to Velopack updates.");
            return;
        }

        _status.BeginCheck();

        try
        {
            var info = await _holder.Manager.CheckForUpdatesAsync().ConfigureAwait(false);
            _holder.Pending = info;

            if (info is null || info.TargetFullRelease is null)
            {
                _status.ReportResult(
                    remoteVersion: _status.Local.SemanticVersion,
                    remotePublishedAt: null,
                    status: UpdateComparisonStatus.Identical,
                    releaseNotesUrl: null);
                _logger.LogInformation($"Build is up to date ({_status.Local.SemanticVersion}, channel={_holder.Channel.Name}).");
                return;
            }

            var targetVersion = info.TargetFullRelease.Version.ToString();
            _status.ReportResult(
                remoteVersion: targetVersion,
                remotePublishedAt: null,
                status: UpdateComparisonStatus.Behind,
                releaseNotesUrl: $"{MithrilRepository.Url}/releases/tag/v{targetVersion}");

            _logger.LogInformation($"Update available: local={_status.Local.SemanticVersion} remote={targetVersion} channel={_holder.Channel.Name}");
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _status.ReportError(ex.Message);
            _logger.LogWarning(ex, "Update check failed: {ExceptionType} {Message}", ex.GetType().Name, ex.Message);
        }
    }
}
