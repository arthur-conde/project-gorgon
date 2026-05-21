using Mithril.GameReports;

namespace Mithril.Shared.Character;

/// <summary>
/// Single source of truth for the <i>active</i> in-game character across every
/// module. Owns the active-selection axis: the Player.log login signal, the
/// persisted user selection, and a delegating snapshot view that projects the
/// raw report data through that active <c>(name, server)</c> pair (see
/// <see cref="ActiveCharacter"/> / <see cref="ActiveStorageReport"/>).
///
/// <para>The raw Reports directory scan + <c>FileSystemWatcher</c> for both
/// <c>Character_*.json</c> and <c>*_items_*.json</c> are owned by
/// <see cref="Mithril.GameReports.IGameReportsService"/> (the foundation
/// service). Modules wanting the unfiltered set of reports — or reports for a
/// character other than the active one — should consume that service
/// directly; this interface re-exposes the active-character slice
/// (<see cref="Characters"/>, <see cref="StorageReports"/>) only as a
/// convenience for callers that already track the active session.</para>
///
/// <para>Modules should never parse <c>ProcessAddPlayer</c> independently or
/// open their own <c>FileSystemWatcher</c> against the Reports directory —
/// delegate to <see cref="Mithril.GameReports.IGameReportsService"/> for raw
/// report data.</para>
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
