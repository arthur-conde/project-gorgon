using System.ComponentModel;
using System.IO;
using Gorgon.Shared.Diagnostics;
using Gorgon.Shared.Game;

namespace Gorgon.Shared.Storage;

public sealed class StorageReportWatcher : IStorageReportWatcher
{
    private readonly GameConfig _gameConfig;
    private readonly IDiagnosticsSink? _diag;
    private FileSystemWatcher? _watcher;
    private Timer? _debounce;
    private readonly Lock _gate = new();

    private IReadOnlyList<ReportFileInfo> _reports = [];

    public StorageReportWatcher(GameConfig gameConfig, IDiagnosticsSink? diag = null)
    {
        _gameConfig = gameConfig;
        _diag = diag;
        _gameConfig.PropertyChanged += OnGameConfigChanged;
        Refresh();
    }

    public IReadOnlyList<ReportFileInfo> Reports => _reports;

    public event EventHandler? ReportsChanged;

    public void Refresh()
    {
        var dir = _gameConfig.ReportsDirectory;
        _reports = StorageReportLoader.ScanForReports(dir ?? "");
        RebuildWatcher(dir);
        ReportsChanged?.Invoke(this, EventArgs.Empty);
    }

    public void Dispose()
    {
        _gameConfig.PropertyChanged -= OnGameConfigChanged;
        lock (_gate)
        {
            _watcher?.Dispose();
            _watcher = null;
            _debounce?.Dispose();
            _debounce = null;
        }
    }

    private void OnGameConfigChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(GameConfig.ReportsDirectory)) Refresh();
    }

    private void RebuildWatcher(string? dir)
    {
        lock (_gate)
        {
            _watcher?.Dispose();
            _watcher = null;

            if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir)) return;

            try
            {
                _watcher = new FileSystemWatcher(dir, "*_items_*.json")
                {
                    NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.CreationTime | NotifyFilters.Size,
                    EnableRaisingEvents = true,
                };
                _watcher.Created += OnFileChanged;
                _watcher.Changed += OnFileChanged;
                _watcher.Deleted += OnFileChanged;
                _watcher.Renamed += OnFileRenamed;
            }
            catch (Exception ex)
            {
                _diag?.Warn("Storage", $"FileSystemWatcher setup failed: {ex.Message}");
            }
        }
    }

    private void OnFileChanged(object sender, FileSystemEventArgs e) => ScheduleRefresh();
    private void OnFileRenamed(object sender, RenamedEventArgs e) => ScheduleRefresh();

    private void ScheduleRefresh()
    {
        // Debounce: the game writes the file in chunks, firing many Changed events.
        lock (_gate)
        {
            _debounce?.Dispose();
            _debounce = new Timer(_ =>
            {
                try
                {
                    var dir = _gameConfig.ReportsDirectory;
                    _reports = StorageReportLoader.ScanForReports(dir ?? "");
                    ReportsChanged?.Invoke(this, EventArgs.Empty);
                }
                catch (Exception ex)
                {
                    _diag?.Warn("Storage", $"Refresh failed: {ex.Message}");
                }
            }, null, TimeSpan.FromMilliseconds(500), Timeout.InfiniteTimeSpan);
        }
    }
}
