using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Text.Json;
using Mithril.Shared.Diagnostics;
using Mithril.Shared.Game;
using Mithril.Shared.Storage;

namespace Mithril.Shared.Character;

/// <summary>
/// Owns the Reports-directory scan for both character + storage exports and holds
/// the active-character selection. Single source of truth — no module should open
/// its own <c>FileSystemWatcher</c> on the Reports directory or parse
/// <c>ProcessAddPlayer</c> for the current character. Log events are funneled
/// through <see cref="ActiveCharacterLogSynchronizer"/>.
/// </summary>
public sealed class ActiveCharacterService : IActiveCharacterService
{
    private readonly GameConfig _gameConfig;
    private readonly IActiveCharacterPersistence _persistence;
    private readonly IDiagnosticsSink? _diag;
    private readonly Lock _gate = new();

    private FileSystemWatcher? _watcher;
    private Timer? _debounce;

    private IReadOnlyList<CharacterSnapshot> _characters = [];
    private IReadOnlyList<ReportFileInfo> _storageReports = [];

    private string? _activeCharacterName;
    private string? _activeServer;
    private CharacterSnapshot? _activeCharacter;

    private ReportFileInfo? _cachedActiveReport;
    private StorageReport? _cachedStorageContents;
    private DateTime _cachedStorageMtime;

    public ActiveCharacterService(
        GameConfig gameConfig,
        IActiveCharacterPersistence persistence,
        IDiagnosticsSink? diag = null)
    {
        _gameConfig = gameConfig;
        _persistence = persistence;
        _diag = diag;

        _activeCharacterName = persistence.ActiveCharacterName;
        _activeServer = persistence.ActiveServer;

        _gameConfig.PropertyChanged += OnGameConfigChanged;
        Refresh();
    }

    public IReadOnlyList<CharacterSnapshot> Characters => _characters;
    public IReadOnlyList<ReportFileInfo> StorageReports => _storageReports;

    public string? ActiveCharacterName => _activeCharacterName;
    public string? ActiveServer => _activeServer;
    public CharacterSnapshot? ActiveCharacter => _activeCharacter;

    public ReportFileInfo? ActiveStorageReport
    {
        get
        {
            if (string.IsNullOrEmpty(_activeCharacterName)) return null;
            return _storageReports.FirstOrDefault(r =>
                string.Equals(r.Character, _activeCharacterName, StringComparison.OrdinalIgnoreCase) &&
                (string.IsNullOrEmpty(_activeServer) ||
                 string.Equals(r.Server, _activeServer, StringComparison.OrdinalIgnoreCase)));
        }
    }

    public StorageReport? ActiveStorageContents
    {
        get
        {
            var current = ActiveStorageReport;
            if (current is null)
            {
                _cachedActiveReport = null;
                _cachedStorageContents = null;
                return null;
            }

            // Invalidate on path change or mtime change.
            var mtime = File.Exists(current.FilePath) ? File.GetLastWriteTimeUtc(current.FilePath) : default;
            if (_cachedActiveReport?.FilePath != current.FilePath || _cachedStorageMtime != mtime)
            {
                try
                {
                    _cachedStorageContents = StorageReportLoader.Load(current.FilePath);
                    _cachedActiveReport = current;
                    _cachedStorageMtime = mtime;
                }
                catch (Exception ex)
                {
                    _diag?.Warn("ActiveChar", $"Failed to load {current.FilePath}: {ex.Message}");
                    return null;
                }
            }
            return _cachedStorageContents;
        }
    }

    public event EventHandler? ActiveCharacterChanged;
    public event EventHandler? CharacterExportsChanged;
    public event EventHandler? StorageReportsChanged;

    public void SetActiveCharacter(string name, string server)
    {
        if (string.Equals(_activeCharacterName, name, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(_activeServer, server, StringComparison.OrdinalIgnoreCase))
            return;

        _activeCharacterName = name;
        _activeServer = server;
        _persistence.ActiveCharacterName = name;
        _persistence.ActiveServer = server;

        ResolveActiveCharacter();
        InvalidateStorageCache();
        ActiveCharacterChanged?.Invoke(this, EventArgs.Empty);
    }

    public void Refresh()
    {
        var dir = _gameConfig.ReportsDirectory;
        var previousChars = _characters;
        var previousReports = _storageReports;

        _characters = LoadCharacterSnapshots(dir);
        _storageReports = string.IsNullOrEmpty(dir) ? [] : StorageReportLoader.ScanForReports(dir);

        InitializeActiveOnFirstRun();
        ResolveActiveCharacter();
        RebuildWatcher(dir);

        if (!ReferenceEquals(previousChars, _characters))
            CharacterExportsChanged?.Invoke(this, EventArgs.Empty);
        if (!ReferenceEquals(previousReports, _storageReports))
        {
            InvalidateStorageCache();
            StorageReportsChanged?.Invoke(this, EventArgs.Empty);
        }
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

    // ── Private helpers ─────────────────────────────────────────────────

    /// <summary>On first construction (or after a reports-dir change), if no name was
    /// persisted and no log event has arrived, fall back to the newest export.</summary>
    private void InitializeActiveOnFirstRun()
    {
        if (!string.IsNullOrEmpty(_activeCharacterName)) return;
        var newest = _characters.FirstOrDefault();
        if (newest is null) return;
        _activeCharacterName = newest.Name;
        _activeServer = newest.Server;
        _persistence.ActiveCharacterName = newest.Name;
        _persistence.ActiveServer = newest.Server;
    }

    /// <summary>Match the active name+server against the loaded snapshots.</summary>
    private void ResolveActiveCharacter()
    {
        if (string.IsNullOrEmpty(_activeCharacterName))
        {
            _activeCharacter = null;
            return;
        }
        _activeCharacter = _characters.FirstOrDefault(c =>
            c.Name.Equals(_activeCharacterName, StringComparison.OrdinalIgnoreCase) &&
            (string.IsNullOrEmpty(_activeServer) ||
             c.Server.Equals(_activeServer, StringComparison.OrdinalIgnoreCase)));

        // If server was unknown and we now found a matching name-only snapshot, adopt its server.
        if (_activeCharacter is not null && string.IsNullOrEmpty(_activeServer))
        {
            _activeServer = _activeCharacter.Server;
            _persistence.ActiveServer = _activeCharacter.Server;
        }
    }

    private void InvalidateStorageCache()
    {
        _cachedActiveReport = null;
        _cachedStorageContents = null;
        _cachedStorageMtime = default;
    }

    private List<CharacterSnapshot> LoadCharacterSnapshots(string? dir)
    {
        var result = new List<CharacterSnapshot>();
        if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir)) return result;

        foreach (var file in Directory.EnumerateFiles(dir, "Character_*.json"))
        {
            var snap = TryParseCharacter(file);
            if (snap is not null) result.Add(snap);
        }
        result.Sort((a, b) => b.ExportedAt.CompareTo(a.ExportedAt));
        return result;
    }

    private CharacterSnapshot? TryParseCharacter(string path)
    {
        try
        {
            using var stream = File.OpenRead(path);
            var raw = JsonSerializer.Deserialize(stream, CharacterJsonContext.Default.RawCharacterExport);
            if (raw is null || raw.Report != "CharacterSheet") return null;

            var skills = new Dictionary<string, CharacterSkill>(StringComparer.Ordinal);
            if (raw.Skills is not null)
            {
                foreach (var (key, v) in raw.Skills)
                {
                    skills[key] = new CharacterSkill(
                        v.Level ?? 0,
                        v.BonusLevels ?? 0,
                        v.XpTowardNextLevel ?? 0,
                        v.XpNeededForNextLevel ?? 0);
                }
            }

            DateTimeOffset exported = default;
            if (raw.Timestamp is not null)
            {
                DateTimeOffset.TryParse(raw.Timestamp, CultureInfo.InvariantCulture,
                    DateTimeStyles.AssumeUniversal, out exported);
            }

            var npcFavor = new Dictionary<string, string>(StringComparer.Ordinal);
            if (raw.NPCs is not null)
            {
                foreach (var (key, v) in raw.NPCs)
                    if (!string.IsNullOrEmpty(v.FavorLevel))
                        npcFavor[key] = v.FavorLevel;
            }

            return new CharacterSnapshot(
                raw.Character ?? Path.GetFileNameWithoutExtension(path),
                raw.ServerName ?? "",
                exported,
                skills,
                raw.RecipeCompletions ?? new Dictionary<string, int>(),
                npcFavor);
        }
        catch (Exception ex)
        {
            _diag?.Warn("ActiveChar", $"Failed to parse {Path.GetFileName(path)}: {ex.Message}");
            return null;
        }
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
                _watcher = new FileSystemWatcher(dir)
                {
                    // Match both Character_*.json and *_items_*.json — we filter in handler.
                    Filter = "*.json",
                    NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.CreationTime | NotifyFilters.Size,
                    EnableRaisingEvents = true,
                };
                _watcher.Created += OnReportsChanged;
                _watcher.Changed += OnReportsChanged;
                _watcher.Deleted += OnReportsChanged;
                _watcher.Renamed += OnReportsChanged;
            }
            catch (Exception ex)
            {
                _diag?.Warn("ActiveChar", $"FileSystemWatcher setup failed: {ex.Message}");
            }
        }
    }

    private void OnReportsChanged(object sender, FileSystemEventArgs e)
    {
        // Debounce chunked writes.
        lock (_gate)
        {
            _debounce?.Dispose();
            _debounce = new Timer(_ =>
            {
                try { Refresh(); }
                catch (Exception ex) { _diag?.Warn("ActiveChar", $"Refresh failed: {ex.Message}"); }
            }, null, TimeSpan.FromMilliseconds(500), Timeout.InfiniteTimeSpan);
        }
    }

    private void OnGameConfigChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(GameConfig.ReportsDirectory))
            Refresh();
    }
}
