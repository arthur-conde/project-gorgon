namespace Gorgon.Shell.Updates;

public interface IUpdateApplier
{
    bool IsApplying { get; }

    /// <summary>
    /// Download the pending update (set by the most recent <see cref="IUpdateChecker"/>
    /// run) and restart the app onto the new version. For portable installs that cannot
    /// self-update in place, opens the GitHub Releases page in the default browser
    /// instead of throwing.
    /// </summary>
    Task DownloadAndApplyAsync(CancellationToken ct);
}
