namespace Mithril.Shell.Updates;

public sealed class UpdateStatusService : IUpdateStatusService
{
    private readonly ShellSettings _settings;
    private readonly object _gate = new();

    public UpdateStatusService(ShellSettings settings, UpdateChannelInfo channel)
    {
        _settings = settings;
        Local = AssemblyVersionInfo.FromEntryAssembly();
        Channel = channel;
    }

    public AssemblyVersionInfo Local { get; }
    public UpdateChannelInfo Channel { get; }

    public string? RemoteVersion { get; private set; }
    public DateTimeOffset? RemotePublishedAt { get; private set; }
    public string? ReleaseNotesUrl { get; private set; }
    public UpdateComparisonStatus Status { get; private set; } = UpdateComparisonStatus.Unknown;

    public bool IsChecking { get; private set; }
    public DateTimeOffset? LastCheckedAt { get; private set; }
    public string? LastError { get; private set; }

    public bool IsOutdated =>
        Status == UpdateComparisonStatus.Behind &&
        !string.IsNullOrEmpty(RemoteVersion) &&
        !string.Equals(RemoteVersion, _settings.LastDismissedUpdateVersion, StringComparison.OrdinalIgnoreCase);

    public event EventHandler? StateChanged;

    public void Dismiss()
    {
        lock (_gate)
        {
            if (string.IsNullOrEmpty(RemoteVersion)) return;
            _settings.LastDismissedUpdateVersion = RemoteVersion;
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

    public void ReportResult(string? remoteVersion, DateTimeOffset? remotePublishedAt, UpdateComparisonStatus status, string? releaseNotesUrl)
    {
        lock (_gate)
        {
            RemoteVersion = remoteVersion;
            RemotePublishedAt = remotePublishedAt;
            ReleaseNotesUrl = releaseNotesUrl;
            Status = status;
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
