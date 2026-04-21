using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Gorgon.Shared.Icons;

namespace Gorgon.Shell.ViewModels;

public sealed partial class IconSettingsViewModel : ObservableObject
{
    private readonly IIconCacheService _cache;

    public IconSettings Settings { get; }

    public IconSettingsViewModel(IconSettings settings, IIconCacheService cache)
    {
        Settings = settings;
        _cache = cache;
        UpdateCacheStats();
    }

    [ObservableProperty] private string _cacheStats = "";
    [ObservableProperty] private string _downloadProgress = "";
    [ObservableProperty] private bool _isDownloading;
    [ObservableProperty] private double _downloadFraction;

    [RelayCommand]
    private async Task ClearCacheAsync()
    {
        await _cache.ClearCacheAsync();
        UpdateCacheStats();
    }

    [RelayCommand]
    private void ResetUrlPattern()
    {
        Settings.UrlPattern = IconSettings.DefaultUrlPattern;
    }

    [RelayCommand(IncludeCancelCommand = true)]
    private async Task DownloadAllAsync(CancellationToken ct)
    {
        IsDownloading = true;
        DownloadProgress = "Scanning...";
        DownloadFraction = 0;

        try
        {
            var progress = new Progress<(int completed, int total)>(p =>
            {
                DownloadProgress = $"{p.completed} / {p.total}";
                DownloadFraction = p.total > 0 ? (double)p.completed / p.total : 0;
            });

            await _cache.DownloadAllAsync(progress, ct);
            DownloadProgress = "Done";
        }
        catch (OperationCanceledException)
        {
            DownloadProgress = "Cancelled";
        }
        finally
        {
            IsDownloading = false;
            UpdateCacheStats();
        }
    }

    private void UpdateCacheStats()
    {
        var count = _cache.CachedCount;
        var sizeMb = _cache.CacheSizeBytes / (1024.0 * 1024.0);
        CacheStats = $"{count} icons cached ({sizeMb:F1} MB)";
    }
}
