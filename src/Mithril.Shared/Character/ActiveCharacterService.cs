using Microsoft.Extensions.Logging;
using System.ComponentModel;
using Mithril.GameReports;
using Mithril.Shared.Game;

namespace Mithril.Shared.Character;

/// <summary>
/// Single source of truth for the active in-game character across every module.
/// Holds the active-character selection (server + name) and exposes a read-only
/// view over both <c>Character_*.json</c> and <c>*_items_*.json</c> snapshots
/// for the active character.
///
/// <para>The report loading / <c>FileSystemWatcher</c> / per-character scope
/// query lives in <see cref="IGameReportsService"/> (foundation layer) since
/// #612 — this service now <i>delegates</i> the file-side concerns and adds
/// the "active selection" axis (persistence + log-driven name resolution +
/// the changed-events that fire when the user switches characters).</para>
/// </summary>
public sealed class ActiveCharacterService : IActiveCharacterService
{
    private readonly GameConfig _gameConfig;
    private readonly IActiveCharacterPersistence _persistence;
    private readonly IGameReportsService _reports;
    private readonly ILogger? _logger;

    private string? _activeCharacterName;
    private string? _activeServer;
    private CharacterSnapshot? _activeCharacter;

    public ActiveCharacterService(
        GameConfig gameConfig,
        IActiveCharacterPersistence persistence,
        IGameReportsService reports,
        ILogger? logger = null)
    {
        _gameConfig = gameConfig;
        _persistence = persistence;
        _reports = reports;
        _logger = logger;

        _activeCharacterName = persistence.ActiveCharacterName;
        _activeServer = persistence.ActiveServer;

        _gameConfig.PropertyChanged += OnGameConfigChanged;
        _reports.StorageReportsChanged += OnStorageReportsChanged;
        _reports.CharacterSnapshotsChanged += OnCharacterSnapshotsChanged;
        SyncWithReports();
    }

    public IReadOnlyList<CharacterSnapshot> Characters => _reports.CharacterSnapshots;
    public IReadOnlyList<ReportFileInfo> StorageReports => _reports.StorageReports;

    public string? ActiveCharacterName => _activeCharacterName;
    public string? ActiveServer => _activeServer;
    public CharacterSnapshot? ActiveCharacter => _activeCharacter;

    public ReportFileInfo? ActiveStorageReport =>
        _reports.GetStorageReport(_activeCharacterName, _activeServer);

    public StorageReport? ActiveStorageContents =>
        _reports.GetStorageContents(_activeCharacterName, _activeServer);

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
        ActiveCharacterChanged?.Invoke(this, EventArgs.Empty);
    }

    public void Refresh()
    {
        _reports.Refresh();
        // The Refresh() call above re-emits StorageReportsChanged / CharacterSnapshotsChanged
        // synchronously if either set changed; OnStorageReportsChanged + OnCharacterSnapshotsChanged
        // pick those up and forward to our consumers + invalidate active resolution as needed.
        // Direct invocation here would double-fire, so we don't repeat the work.
    }

    public void Dispose()
    {
        _gameConfig.PropertyChanged -= OnGameConfigChanged;
        _reports.StorageReportsChanged -= OnStorageReportsChanged;
        _reports.CharacterSnapshotsChanged -= OnCharacterSnapshotsChanged;
    }

    // ── Private helpers ─────────────────────────────────────────────────

    /// <summary>On first construction (or after a reports-dir change), if no name was
    /// persisted and no log event has arrived, fall back to the newest export.</summary>
    private void InitializeActiveOnFirstRun()
    {
        if (!string.IsNullOrEmpty(_activeCharacterName)) return;
        var newest = _reports.CharacterSnapshots.FirstOrDefault();
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
        _activeCharacter = _reports.GetCharacterSnapshot(_activeCharacterName, _activeServer)
            ?? _reports.GetCharacterSnapshot(_activeCharacterName, null);

        // If server was unknown and we now found a matching name-only snapshot, adopt its server.
        if (_activeCharacter is not null && string.IsNullOrEmpty(_activeServer))
        {
            _activeServer = _activeCharacter.Server;
            _persistence.ActiveServer = _activeCharacter.Server;
        }
    }

    private void SyncWithReports()
    {
        InitializeActiveOnFirstRun();
        ResolveActiveCharacter();
    }

    private void OnStorageReportsChanged(object? sender, EventArgs e)
    {
        StorageReportsChanged?.Invoke(this, EventArgs.Empty);
    }

    private void OnCharacterSnapshotsChanged(object? sender, EventArgs e)
    {
        SyncWithReports();
        CharacterExportsChanged?.Invoke(this, EventArgs.Empty);
    }

    private void OnGameConfigChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(GameConfig.ReportsDirectory))
        {
            // IGameReportsService also subscribes to the reports-dir via its
            // own accessor; nudge it to re-scan, then re-resolve our active.
            _reports.Refresh();
            SyncWithReports();
        }
    }
}
