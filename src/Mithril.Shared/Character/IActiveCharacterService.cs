using Mithril.Shared.Storage;

namespace Mithril.Shared.Character;

/// <summary>
/// Single source of truth for the active in-game character across every module.
/// Owns the Reports directory scan (both <c>Character_*.json</c> and <c>*_items_*.json</c>),
/// the Player.log login signal, and the persisted user selection. Modules should
/// never parse <c>ProcessAddPlayer</c> independently or open their own
/// <c>FileSystemWatcher</c> against the Reports directory.
/// </summary>
public interface IActiveCharacterService : IDisposable
{
    /// <summary>Parsed <c>Character_*.json</c> exports, newest first.</summary>
    IReadOnlyList<CharacterSnapshot> Characters { get; }

    /// <summary>Discovered <c>*_items_*.json</c> storage exports, newest first.</summary>
    IReadOnlyList<ReportFileInfo> StorageReports { get; }

    /// <summary>
    /// Name of the active character. Populated when a log event, persisted setting,
    /// or a character export supplies one. May be non-null even when
    /// <see cref="ActiveCharacter"/> is null (log told us the name before an export landed).
    /// </summary>
    string? ActiveCharacterName { get; }

    /// <summary>Server of the active character; best-guess from exports or persisted setting.</summary>
    string? ActiveServer { get; }

    /// <summary>Full snapshot if an export matches the active name + server; else null.</summary>
    CharacterSnapshot? ActiveCharacter { get; }

    /// <summary>Newest <c>*_items_*.json</c> for the active character, if any.</summary>
    ReportFileInfo? ActiveStorageReport { get; }

    /// <summary>Lazily parsed + cached contents of <see cref="ActiveStorageReport"/>.</summary>
    StorageReport? ActiveStorageContents { get; }

    /// <summary>Set the active character. Persists to settings and fires <see cref="ActiveCharacterChanged"/>.</summary>
    void SetActiveCharacter(string name, string server);

    /// <summary>Rescan the Reports directory.</summary>
    void Refresh();

    /// <summary>Fires when <see cref="ActiveCharacterName"/> or <see cref="ActiveServer"/> changes.</summary>
    event EventHandler? ActiveCharacterChanged;

    /// <summary>Fires when <see cref="Characters"/> changes (export created/updated/deleted).</summary>
    event EventHandler? CharacterExportsChanged;

    /// <summary>Fires when <see cref="StorageReports"/> changes.</summary>
    event EventHandler? StorageReportsChanged;
}
