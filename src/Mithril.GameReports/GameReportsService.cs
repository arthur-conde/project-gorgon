using System.IO;

namespace Mithril.GameReports;

/// <summary>
/// File-system-backed <see cref="IGameReportsService"/>. Owns a debounced
/// <see cref="FileSystemWatcher"/> over the configured reports directory and
/// caches parsed snapshots; cache invalidates on path or mtime change.
///
/// <para>Two file types are watched in the same directory:
/// <c>*_items_*.json</c> (storage exports) and <c>Character_*.json</c>
/// (character sheet exports). They have independent change events and
/// independent caches so a consumer interested in only one side doesn't pay
/// reparse cost for the other.</para>
///
/// <para>Diagnostics are surfaced via an optional <c>logWarn</c> callback so
/// the foundation assembly stays free of a hard dependency on Mithril.Shared's
/// <c>IDiagnosticsSink</c>. Callers pass an adapter; nulls are tolerated.</para>
/// </summary>
public sealed class GameReportsService : IGameReportsService
{
    private readonly Func<string?> _reportsDirectoryAccessor;
    private readonly Action<string, string>? _logWarn;
    private readonly Lock _gate = new();

    private string? _watchedDir;
    private FileSystemWatcher? _watcher;
    private Timer? _debounce;
    private readonly CancellationTokenSource _disposeCts = new();

    private IReadOnlyList<ReportFileInfo> _storageReports = [];
    private IReadOnlyList<CharacterSnapshot> _characterSnapshots = [];

    // Cached parsed storage snapshot keyed by file path + mtime.
    private ReportFileInfo? _cachedStorageReport;
    private StorageReport? _cachedStorageContents;
    private DateTime _cachedStorageMtime;

    /// <param name="reportsDirectoryAccessor">
    /// Late-bound directory read. Called whenever <see cref="Refresh"/> runs
    /// or the watcher is (re)built. Returning null / empty disables the
    /// service (no scan, no watch).
    /// </param>
    /// <param name="logWarn">
    /// Optional diagnostic callback: <c>(category, message)</c>. Used for
    /// soft-failures (parse errors, watcher setup failures). Null tolerated.
    /// </param>
    public GameReportsService(
        Func<string?> reportsDirectoryAccessor,
        Action<string, string>? logWarn = null)
    {
        _reportsDirectoryAccessor = reportsDirectoryAccessor;
        _logWarn = logWarn;
        Refresh();
    }

    public IReadOnlyList<ReportFileInfo> StorageReports => _storageReports;
    public IReadOnlyList<CharacterSnapshot> CharacterSnapshots => _characterSnapshots;

    public event EventHandler? StorageReportsChanged;
    public event EventHandler? CharacterSnapshotsChanged;

    public ReportFileInfo? GetStorageReport(string? character, string? server)
    {
        if (string.IsNullOrEmpty(character)) return null;
        return _storageReports.FirstOrDefault(r =>
            string.Equals(r.Character, character, StringComparison.OrdinalIgnoreCase) &&
            (string.IsNullOrEmpty(server) ||
             string.Equals(r.Server, server, StringComparison.OrdinalIgnoreCase)));
    }

    public StorageReport? GetStorageContents(string? character, string? server)
    {
        var current = GetStorageReport(character, server);
        if (current is null)
        {
            lock (_gate)
            {
                _cachedStorageReport = null;
                _cachedStorageContents = null;
                _cachedStorageMtime = default;
            }
            return null;
        }

        // Invalidate on path change or mtime change.
        var mtime = File.Exists(current.FilePath) ? File.GetLastWriteTimeUtc(current.FilePath) : default;
        lock (_gate)
        {
            if (_cachedStorageReport?.FilePath != current.FilePath || _cachedStorageMtime != mtime)
            {
                try
                {
                    _cachedStorageContents = StorageReportLoader.Load(current.FilePath);
                    _cachedStorageReport = current;
                    _cachedStorageMtime = mtime;
                }
                catch (Exception ex)
                {
                    _logWarn?.Invoke("GameReports", $"Failed to load {current.FilePath}: {ex.Message}");
                    return null;
                }
            }
            return _cachedStorageContents;
        }
    }

    public CharacterSnapshot? GetCharacterSnapshot(string? character, string? server)
    {
        if (string.IsNullOrEmpty(character)) return null;
        return _characterSnapshots.FirstOrDefault(c =>
            string.Equals(c.Name, character, StringComparison.OrdinalIgnoreCase) &&
            (string.IsNullOrEmpty(server) ||
             string.Equals(c.Server, server, StringComparison.OrdinalIgnoreCase)));
    }

    public void Refresh()
    {
        var dir = _reportsDirectoryAccessor();
        var previousStorage = _storageReports;
        var previousChars = _characterSnapshots;

        if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir))
        {
            _storageReports = [];
            _characterSnapshots = [];
        }
        else
        {
            _storageReports = StorageReportLoader.ScanForReports(dir);
            _characterSnapshots = LoadCharacterSnapshots(dir);
        }

        RebuildWatcher(dir);

        var storageChanged = !StorageReportSetsEqual(previousStorage, _storageReports);
        var charactersChanged = !CharacterSetsEqual(previousChars, _characterSnapshots);

        if (storageChanged)
        {
            lock (_gate)
            {
                // Invalidate cached storage contents on any set change so a stale
                // (path, mtime) pair can't shadow a fresh export the watcher fired on.
                _cachedStorageReport = null;
                _cachedStorageContents = null;
                _cachedStorageMtime = default;
            }
            StorageReportsChanged?.Invoke(this, EventArgs.Empty);
        }

        if (charactersChanged)
            CharacterSnapshotsChanged?.Invoke(this, EventArgs.Empty);
    }

    public void Dispose()
    {
        // Cancel first so any in-flight debounce callback observes the token
        // before it touches Refresh(). Without this, the watcher could fire
        // between the dispose-window and the timer-fire-window and call
        // Refresh() on an already-disposed accessor.
        _disposeCts.Cancel();
        lock (_gate)
        {
            _watcher?.Dispose();
            _watcher = null;
            _debounce?.Dispose();
            _debounce = null;
        }
        _disposeCts.Dispose();
    }

    // ── Private helpers ─────────────────────────────────────────────────

    private List<CharacterSnapshot> LoadCharacterSnapshots(string dir)
    {
        var result = new List<CharacterSnapshot>();
        foreach (var file in Directory.EnumerateFiles(dir, "Character_*.json"))
        {
            CharacterSnapshot? snap;
            try
            {
                snap = CharacterReportLoader.Load(file);
            }
            catch (Exception ex)
            {
                _logWarn?.Invoke("GameReports", $"Failed to parse {Path.GetFileName(file)}: {ex.Message}");
                continue;
            }
            if (snap is not null) result.Add(snap);
        }
        result.Sort((a, b) => b.ExportedAt.CompareTo(a.ExportedAt));
        return result;
    }

    private void RebuildWatcher(string? dir)
    {
        lock (_gate)
        {
            // Same dir + still healthy? Keep the existing watcher.
            if (string.Equals(_watchedDir, dir, StringComparison.OrdinalIgnoreCase) &&
                _watcher is not null)
                return;

            _watcher?.Dispose();
            _watcher = null;
            _watchedDir = null;

            if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir)) return;

            try
            {
                _watcher = new FileSystemWatcher(dir)
                {
                    // Catches both Character_*.json and *_items_*.json — the handler
                    // doesn't distinguish; Refresh() rescans the full directory
                    // (cheap: tens of files at most) and only fires events for sets
                    // that actually changed.
                    Filter = "*.json",
                    NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.CreationTime | NotifyFilters.Size,
                    EnableRaisingEvents = true,
                };
                _watcher.Created += OnReportsChanged;
                _watcher.Changed += OnReportsChanged;
                _watcher.Deleted += OnReportsChanged;
                _watcher.Renamed += OnReportsChanged;
                _watchedDir = dir;
            }
            catch (Exception ex)
            {
                _logWarn?.Invoke("GameReports", $"FileSystemWatcher setup failed: {ex.Message}");
            }
        }
    }

    private void OnReportsChanged(object sender, FileSystemEventArgs e)
    {
        // Debounce chunked writes. PG's export writes the file in multiple
        // chunks and we don't want to parse a half-written JSON.
        //
        // The timer callback may fire after Dispose() — between the dispose
        // and the 500ms fire-window — so it checks the cancellation token
        // before touching Refresh(). The worst case without this check is one
        // stale Refresh() against a torn-down accessor; innocuous, but the
        // token keeps cleanup airtight.
        lock (_gate)
        {
            if (_disposeCts.IsCancellationRequested) return;
            _debounce?.Dispose();
            _debounce = new Timer(_ =>
            {
                if (_disposeCts.IsCancellationRequested) return;
                try { Refresh(); }
                catch (ObjectDisposedException) { /* disposed mid-callback; nothing to do. */ }
                catch (Exception ex) { _logWarn?.Invoke("GameReports", $"Refresh failed: {ex.Message}"); }
            }, null, TimeSpan.FromMilliseconds(500), Timeout.InfiniteTimeSpan);
        }
    }

    private static bool StorageReportSetsEqual(
        IReadOnlyList<ReportFileInfo> a, IReadOnlyList<ReportFileInfo> b)
    {
        if (a.Count != b.Count) return false;
        for (int i = 0; i < a.Count; i++)
        {
            if (a[i].FilePath != b[i].FilePath) return false;
            if (a[i].LastModifiedUtc != b[i].LastModifiedUtc) return false;
        }
        return true;
    }

    private static bool CharacterSetsEqual(
        IReadOnlyList<CharacterSnapshot> a, IReadOnlyList<CharacterSnapshot> b)
    {
        if (a.Count != b.Count) return false;
        for (int i = 0; i < a.Count; i++)
        {
            if (a[i].Name != b[i].Name) return false;
            if (a[i].Server != b[i].Server) return false;
            if (a[i].ExportedAt != b[i].ExportedAt) return false;
        }
        return true;
    }
}
