namespace Gorgon.Shell.Updates;

public sealed class UpdateStatusService : IUpdateStatusService
{
    private readonly ShellSettings _settings;
    private readonly object _gate = new();

    public UpdateStatusService(ShellSettings settings)
    {
        _settings = settings;
        Local = AssemblyVersionInfo.FromEntryAssembly();
    }

    public AssemblyVersionInfo Local { get; }

    public string? RemoteSha { get; private set; }
    public DateTimeOffset? RemoteCommittedAt { get; private set; }
    public UpdateComparisonStatus Status { get; private set; } = UpdateComparisonStatus.Unknown;
    public int BehindByCount { get; private set; }
    public string? CompareUrl { get; private set; }

    public bool IsChecking { get; private set; }
    public DateTimeOffset? LastCheckedAt { get; private set; }
    public string? LastError { get; private set; }

    public bool IsOutdated =>
        Status == UpdateComparisonStatus.Behind &&
        !string.IsNullOrEmpty(RemoteSha) &&
        !string.Equals(RemoteSha, _settings.LastDismissedUpdateSha, StringComparison.OrdinalIgnoreCase);

    public event EventHandler? StateChanged;

    public void Dismiss()
    {
        lock (_gate)
        {
            if (string.IsNullOrEmpty(RemoteSha)) return;
            _settings.LastDismissedUpdateSha = RemoteSha;
        }
        RaiseChanged();
    }

    public void BeginCheck()
    {
        lock (_gate)
        {
            IsChecking = true;
            LastError = null;
        }
        RaiseChanged();
    }

    public void ReportResult(string remoteSha, DateTimeOffset? remoteCommittedAt, UpdateComparisonStatus status, int behindBy, string? compareUrl)
    {
        lock (_gate)
        {
            RemoteSha = remoteSha;
            RemoteCommittedAt = remoteCommittedAt;
            Status = status;
            BehindByCount = behindBy;
            CompareUrl = compareUrl;
            IsChecking = false;
            LastCheckedAt = DateTimeOffset.UtcNow;
            LastError = null;
        }
        RaiseChanged();
    }

    public void ReportError(string message)
    {
        lock (_gate)
        {
            IsChecking = false;
            LastCheckedAt = DateTimeOffset.UtcNow;
            LastError = message;
        }
        RaiseChanged();
    }

    public void ReportNotApplicable()
    {
        lock (_gate)
        {
            Status = UpdateComparisonStatus.NotApplicable;
            IsChecking = false;
            LastCheckedAt = DateTimeOffset.UtcNow;
            LastError = null;
        }
        RaiseChanged();
    }

    private void RaiseChanged()
    {
        var d = System.Windows.Application.Current?.Dispatcher;
        if (d is null || d.CheckAccess())
            StateChanged?.Invoke(this, EventArgs.Empty);
        else
            d.InvokeAsync(() => StateChanged?.Invoke(this, EventArgs.Empty));
    }
}
