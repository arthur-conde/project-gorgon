using Mithril.Shared.Diagnostics;

namespace Mithril.Shell.Updates;

public sealed class VelopackUpdateChecker : IUpdateChecker
{
    private readonly MithrilUpdateManager _holder;
    private readonly IUpdateStatusService _status;
    private readonly IDiagnosticsSink _diag;

    public VelopackUpdateChecker(MithrilUpdateManager holder, IUpdateStatusService status, IDiagnosticsSink diag)
    {
        _holder = holder;
        _status = status;
        _diag = diag;
    }

    public async Task CheckAsync(CancellationToken ct)
    {
        if (!_holder.IsAvailable)
        {
            _status.ReportNotApplicable();
            _diag.Info("updates", $"Skipping update check — channel '{_holder.Channel.Name}' is not subject to Velopack updates.");
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
                _diag.Info("updates", $"Build is up to date ({_status.Local.SemanticVersion}, channel={_holder.Channel.Name}).");
                return;
            }

            var targetVersion = info.TargetFullRelease.Version.ToString();
            _status.ReportResult(
                remoteVersion: targetVersion,
                remotePublishedAt: null,
                status: UpdateComparisonStatus.Behind,
                releaseNotesUrl: $"{MithrilUpdateManager.RepoUrl}/releases/tag/v{targetVersion}");

            _diag.Info("updates", $"Update available: local={_status.Local.SemanticVersion} remote={targetVersion} channel={_holder.Channel.Name}");
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _status.ReportError(ex.Message);
            _diag.Warn("updates", $"Update check failed: {ex.GetType().Name} {ex.Message}");
        }
    }
}
