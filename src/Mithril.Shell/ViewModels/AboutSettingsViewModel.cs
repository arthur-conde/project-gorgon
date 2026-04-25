using System.Diagnostics;
using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Mithril.Shell.Updates;

namespace Mithril.Shell.ViewModels;

public sealed partial class AboutSettingsViewModel : ObservableObject, IDisposable
{
    private const string RepoUrl = "https://github.com/arthur-conde/project-gorgon";

    private readonly IUpdateStatusService _status;
    private readonly IUpdateChecker _checker;
    private readonly IUpdateApplier _applier;
    private CancellationTokenSource? _manualCheckCts;

    public AboutSettingsViewModel(
        IUpdateStatusService status,
        IUpdateChecker checker,
        IUpdateApplier applier,
        ShellSettings settings)
    {
        _status = status;
        _checker = checker;
        _applier = applier;
        Settings = settings;
        _status.StateChanged += OnStateChanged;
    }

    public ShellSettings Settings { get; }

    public AssemblyVersionInfo Local => _status.Local;
    public UpdateChannelInfo Channel => _status.Channel;

    public string SemanticVersion => string.IsNullOrEmpty(Local.SemanticVersion) ? "(unknown)" : Local.SemanticVersion;
    public string CommitDisplay => Local.HasCommitSha ? Local.ShortCommitSha : "(no git metadata)";
    public bool HasCommitSha => Local.HasCommitSha;
    public string ChannelDisplay => Channel.DisplayName;
    public string BuildTimestampDisplay =>
        Local.BuildTimestampUtc is { } b ? b.ToLocalTime().ToString("yyyy-MM-dd HH:mm") : "(unknown)";

    public bool IsChecking => _status.IsChecking;
    public bool NotChecking => !_status.IsChecking;
    public bool HasRemote => !string.IsNullOrEmpty(_status.RemoteVersion);
    public string RemoteDisplay => _status.RemoteVersion ?? "";
    public string RemotePublishedDisplay =>
        _status.RemotePublishedAt is { } r ? r.ToLocalTime().ToString("yyyy-MM-dd HH:mm") : "";
    public string LastCheckedDisplay =>
        _status.LastCheckedAt is { } t ? FormatRelative(t) : "never";
    public string? LastError => _status.LastError;
    public bool HasLastError => !string.IsNullOrEmpty(_status.LastError);

    public bool IsUpdateAvailable => _status.IsOutdated && !_applier.IsApplying;
    public bool IsApplying => _applier.IsApplying;

    public string StatusDisplay => _status.Status switch
    {
        UpdateComparisonStatus.Identical     => "Up to date",
        UpdateComparisonStatus.Behind        => string.IsNullOrEmpty(_status.RemoteVersion)
                                                  ? "Update available"
                                                  : $"Update available — v{_status.RemoteVersion}",
        UpdateComparisonStatus.NotApplicable => Channel.IsDevelopment
                                                  ? "Development build (no updates)"
                                                  : "Updates not applicable",
        UpdateComparisonStatus.Unknown       => _status.LastCheckedAt is null ? "Not checked yet" : "Unknown",
        _ => "Unknown",
    };

    public SolidColorBrush StatusBrush => _status.Status switch
    {
        UpdateComparisonStatus.Identical => Brushes.UpToDate,
        UpdateComparisonStatus.Behind    => Brushes.Outdated,
        _ => Brushes.Muted,
    };

    private static class Brushes
    {
        public static readonly SolidColorBrush UpToDate      = Freeze(System.Windows.Media.Color.FromRgb(0x6F, 0xCF, 0x6F));
        public static readonly SolidColorBrush Outdated      = Freeze(System.Windows.Media.Color.FromRgb(0xE8, 0xB0, 0x4A));
        public static readonly SolidColorBrush Informational = Freeze(System.Windows.Media.Color.FromRgb(0x88, 0xAA, 0xCC));
        public static readonly SolidColorBrush Muted         = Freeze(System.Windows.Media.Color.FromArgb(0x88, 0xFF, 0xFF, 0xFF));
        private static SolidColorBrush Freeze(System.Windows.Media.Color c) { var b = new SolidColorBrush(c); b.Freeze(); return b; }
    }

    [RelayCommand]
    private async Task CheckNowAsync()
    {
        if (_status.IsChecking) return;
        _manualCheckCts?.Cancel();
        _manualCheckCts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        try
        {
            await _checker.CheckAsync(_manualCheckCts.Token).ConfigureAwait(true);
        }
        catch (OperationCanceledException) { /* user navigated away or timed out */ }
    }

    [RelayCommand]
    private async Task ApplyUpdateAsync()
    {
        if (!_status.IsOutdated || _applier.IsApplying) return;
        OnPropertyChanged(nameof(IsApplying));
        OnPropertyChanged(nameof(IsUpdateAvailable));
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(5));
            await _applier.DownloadAndApplyAsync(cts.Token).ConfigureAwait(true);
        }
        catch (OperationCanceledException) { /* timeout — user can retry */ }
        finally
        {
            OnPropertyChanged(nameof(IsApplying));
            OnPropertyChanged(nameof(IsUpdateAvailable));
        }
    }

    [RelayCommand]
    private void OpenRepo() => OpenUrl(RepoUrl);

    [RelayCommand]
    private void OpenReleaseNotes()
    {
        var url = _status.ReleaseNotesUrl ?? $"{RepoUrl}/releases/latest";
        OpenUrl(url);
    }

    // ═══════════════ Deep-link scheme registration (mithril://) ═══════════════

    /// <summary>True when HKCU\Software\Classes\mithril exists (regardless of which install it points at).</summary>
    public bool IsLinkSchemeRegistered => MithrilUriSchemeRegistrar.IsRegistered();

    public string LinkSchemeStatus
    {
        get
        {
            if (!IsLinkSchemeRegistered) return "Not registered";
            var command = MithrilUriSchemeRegistrar.CurrentRegisteredCommand();
            var expected = Environment.ProcessPath;
            if (!string.IsNullOrEmpty(command) && !string.IsNullOrEmpty(expected) &&
                !command.Contains(expected, StringComparison.OrdinalIgnoreCase))
                return $"Registered, but pointing at a different install: {command}";
            return "Registered for this install";
        }
    }

    public string LinkSchemeButtonText => IsLinkSchemeRegistered
        ? "Unregister mithril:// links"
        : "Register mithril:// links";

    [RelayCommand]
    private void ToggleLinkScheme()
    {
        try
        {
            if (IsLinkSchemeRegistered)
            {
                MithrilUriSchemeRegistrar.Unregister();
            }
            else
            {
                var exePath = Environment.ProcessPath;
                if (string.IsNullOrEmpty(exePath)) return;
                MithrilUriSchemeRegistrar.Register(exePath);
            }
        }
        catch { /* best-effort; user can retry */ }

        OnPropertyChanged(nameof(IsLinkSchemeRegistered));
        OnPropertyChanged(nameof(LinkSchemeStatus));
        OnPropertyChanged(nameof(LinkSchemeButtonText));
    }

    private static void OpenUrl(string url)
    {
        try
        {
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
        }
        catch { /* best-effort */ }
    }

    private static string FormatRelative(DateTimeOffset when)
    {
        var delta = DateTimeOffset.UtcNow - when;
        if (delta < TimeSpan.Zero) return when.ToLocalTime().ToString("HH:mm");
        if (delta < TimeSpan.FromSeconds(30)) return "just now";
        if (delta < TimeSpan.FromMinutes(1)) return $"{(int)delta.TotalSeconds}s ago";
        if (delta < TimeSpan.FromHours(1))   return $"{(int)delta.TotalMinutes}m ago";
        if (delta < TimeSpan.FromDays(1))    return $"{(int)delta.TotalHours}h ago";
        return when.ToLocalTime().ToString("yyyy-MM-dd HH:mm");
    }

    private void OnStateChanged(object? sender, EventArgs e)
    {
        OnPropertyChanged(nameof(IsChecking));
        OnPropertyChanged(nameof(NotChecking));
        OnPropertyChanged(nameof(HasRemote));
        OnPropertyChanged(nameof(RemoteDisplay));
        OnPropertyChanged(nameof(RemotePublishedDisplay));
        OnPropertyChanged(nameof(LastCheckedDisplay));
        OnPropertyChanged(nameof(LastError));
        OnPropertyChanged(nameof(HasLastError));
        OnPropertyChanged(nameof(StatusDisplay));
        OnPropertyChanged(nameof(StatusBrush));
        OnPropertyChanged(nameof(IsUpdateAvailable));
        CheckNowCommand.NotifyCanExecuteChanged();
        ApplyUpdateCommand.NotifyCanExecuteChanged();
    }

    public void Dispose()
    {
        _status.StateChanged -= OnStateChanged;
        _manualCheckCts?.Cancel();
        _manualCheckCts?.Dispose();
    }
}
