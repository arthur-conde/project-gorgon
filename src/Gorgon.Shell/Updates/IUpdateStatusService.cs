namespace Gorgon.Shell.Updates;

public enum UpdateComparisonStatus
{
    Unknown,
    Identical,      // No newer release in our channel
    Behind,         // A newer release exists; install button is offered
    NotApplicable,  // Local build runs in 'dev' channel; updates are skipped entirely
}

public interface IUpdateStatusService
{
    AssemblyVersionInfo Local { get; }
    UpdateChannelInfo Channel { get; }

    string? RemoteVersion { get; }
    DateTimeOffset? RemotePublishedAt { get; }
    string? ReleaseNotesUrl { get; }
    UpdateComparisonStatus Status { get; }

    bool IsChecking { get; }
    DateTimeOffset? LastCheckedAt { get; }
    string? LastError { get; }

    bool IsOutdated { get; }

    event EventHandler? StateChanged;

    /// <summary>Stash the remote version so it isn't surfaced as 'available' again
    /// until a newer one ships.</summary>
    void Dismiss();

    // Intended for IUpdateChecker to call.
    void BeginCheck();
    void ReportResult(string? remoteVersion, DateTimeOffset? remotePublishedAt, UpdateComparisonStatus status, string? releaseNotesUrl);
    void ReportError(string message);
    void ReportNotApplicable();
}
