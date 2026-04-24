using System.Diagnostics;
using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Gorgon.Shell.Updates;

namespace Gorgon.Shell.ViewModels;

public sealed partial class AboutSettingsViewModel : ObservableObject, IDisposable
{
    private const string RepoUrl = "https://github.com/arthur-conde/project-gorgon";

    private readonly IUpdateStatusService _status;
    private readonly IUpdateChecker _checker;
    private CancellationTokenSource? _manualCheckCts;

    public AboutSettingsViewModel(IUpdateStatusService status, IUpdateChecker checker, ShellSettings settings)
    {
        _status = status;
        _checker = checker;
        Settings = settings;
        _status.StateChanged += OnStateChanged;
    }

    public ShellSettings Settings { get; }

    public AssemblyVersionInfo Local => _status.Local;

    public string SemanticVersion => string.IsNullOrEmpty(Local.SemanticVersion) ? "(unknown)" : Local.SemanticVersion;
    public string CommitDisplay => Local.HasCommitSha ? Local.ShortCommitSha : "(no git metadata)";
    public bool HasCommitSha => Local.HasCommitSha;
    public string BuildTimestampDisplay =>
        Local.BuildTimestampUtc is { } b ? b.ToLocalTime().ToString("yyyy-MM-dd HH:mm") : "(unknown)";

    public bool IsChecking => _status.IsChecking;
    public bool NotChecking => !_status.IsChecking;
    public bool HasRemote => !string.IsNullOrEmpty(_status.RemoteSha);
    public string RemoteDisplay => _status.RemoteSha is { Length: > 0 } s ? s[..Math.Min(10, s.Length)] : "";
    public string RemoteCommittedDisplay =>
        _status.RemoteCommittedAt is { } r ? r.ToLocalTime().ToString("yyyy-MM-dd HH:mm") : "";
    public string LastCheckedDisplay =>
        _status.LastCheckedAt is { } t ? FormatRelative(t) : "never";
    public string? LastError => _status.LastError;
    public bool HasLastError => !string.IsNullOrEmpty(_status.LastError);

    public string StatusDisplay => _status.Status switch
    {
        UpdateComparisonStatus.Identical => "Up to date",
        UpdateComparisonStatus.Behind    => $"{_status.BehindByCount} new commit(s) on main",
        UpdateComparisonStatus.Ahead     => "Your build is ahead of main (local commits)",
        UpdateComparisonStatus.Diverged  => "Your build has diverged from main",
        UpdateComparisonStatus.NotApplicable => "Not a git build (no commit SHA embedded)",
        UpdateComparisonStatus.Unknown   => _status.LastCheckedAt is null ? "Not checked yet" : "Unknown",
        _ => "Unknown",
    };

    public SolidColorBrush StatusBrush => _status.Status switch
    {
        UpdateComparisonStatus.Identical => Brushes.UpToDate,
        UpdateComparisonStatus.Behind    => Brushes.Outdated,
        UpdateComparisonStatus.Ahead     => Brushes.Informational,
        UpdateComparisonStatus.Diverged  => Brushes.Informational,
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
    private void OpenRepo() => OpenUrl(RepoUrl);

    // ═══════════════ Deep-link scheme registration (gorgon://) ═══════════════

    /// <summary>True when HKCU\Software\Classes\gorgon exists (regardless of which install it points at).</summary>
    public bool IsLinkSchemeRegistered => GorgonUriSchemeRegistrar.IsRegistered();

    public string LinkSchemeStatus
    {
        get
        {
            if (!IsLinkSchemeRegistered) return "Not registered";
            var command = GorgonUriSchemeRegistrar.CurrentRegisteredCommand();
            var expected = Environment.ProcessPath;
            if (!string.IsNullOrEmpty(command) && !string.IsNullOrEmpty(expected) &&
                !command.Contains(expected, StringComparison.OrdinalIgnoreCase))
                return $"Registered, but pointing at a different install: {command}";
            return "Registered for this install";
        }
    }

    public string LinkSchemeButtonText => IsLinkSchemeRegistered
        ? "Unregister gorgon:// links"
        : "Register gorgon:// links";

    [RelayCommand]
    private void ToggleLinkScheme()
    {
        try
        {
            if (IsLinkSchemeRegistered)
            {
                GorgonUriSchemeRegistrar.Unregister();
            }
            else
            {
                var exePath = Environment.ProcessPath;
                if (string.IsNullOrEmpty(exePath)) return;
                GorgonUriSchemeRegistrar.Register(exePath);
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
        OnPropertyChanged(nameof(RemoteCommittedDisplay));
        OnPropertyChanged(nameof(LastCheckedDisplay));
        OnPropertyChanged(nameof(LastError));
        OnPropertyChanged(nameof(HasLastError));
        OnPropertyChanged(nameof(StatusDisplay));
        OnPropertyChanged(nameof(StatusBrush));
        CheckNowCommand.NotifyCanExecuteChanged();
    }

    public void Dispose()
    {
        _status.StateChanged -= OnStateChanged;
        _manualCheckCts?.Cancel();
        _manualCheckCts?.Dispose();
    }
}
