namespace Gorgon.Shell.Updates;

public enum UpdateComparisonStatus
{
    Unknown,
    Identical,
    Behind,
    Ahead,
    Diverged,
    NotApplicable, // release-tag build or no local SHA
}

public interface IUpdateStatusService
{
    AssemblyVersionInfo Local { get; }

    string? RemoteSha { get; }
    DateTimeOffset? RemoteCommittedAt { get; }
    UpdateComparisonStatus Status { get; }
    int BehindByCount { get; }
    string? CompareUrl { get; }

    bool IsChecking { get; }
    DateTimeOffset? LastCheckedAt { get; }
    string? LastError { get; }

    bool IsOutdated { get; }

    event EventHandler? StateChanged;

    void Dismiss();

    // Intended for IUpdateChecker to call.
    void BeginCheck();
    void ReportResult(string remoteSha, DateTimeOffset? remoteCommittedAt, UpdateComparisonStatus status, int behindBy, string? compareUrl);
    void ReportError(string message);
    void ReportNotApplicable();
}
