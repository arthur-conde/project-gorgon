namespace Mithril.GameReports;

/// <summary>
/// Shared foundation service for PG's character export reports. Owns the
/// directory scan + <c>FileSystemWatcher</c> + cached parsed snapshots for both
/// <c>Character_*.json</c> (character sheet exports) and <c>*_items_*.json</c>
/// (storage / vault exports). Multiple modules consume; no module "owns" the
/// file.
///
/// <para>Per the world-simulator design notebook
/// (<c>docs/world-simulator.md</c> §Three categories of data), reports are an
/// <i>external shared data source</i>, distinct from world-derived state.
/// Vault contents are the canonical case requiring this service — the worlds
/// can't observe vault items; only the report includes them.</para>
///
/// <para>Per-character / per-server scope: queries are keyed by
/// <c>(Server, Character)</c>. Callers (typically modules) pass the active
/// session's identifiers and read the most recent matching snapshot.</para>
/// </summary>
public interface IGameReportsService : IDisposable
{
    // ── Storage reports (Reports/{Character}_{Server}_items_{ts}.json) ─────

    /// <summary>Discovered <c>*_items_*.json</c> storage exports, newest first.</summary>
    IReadOnlyList<ReportFileInfo> StorageReports { get; }

    /// <summary>
    /// Newest storage report for the given <paramref name="character"/> +
    /// <paramref name="server"/>. <paramref name="server"/> may be null/empty
    /// to match by name only (last-write wins in that case).
    /// </summary>
    ReportFileInfo? GetStorageReport(string? character, string? server);

    /// <summary>
    /// Lazily parsed + cached contents of the newest storage export for
    /// <paramref name="character"/> + <paramref name="server"/>. Returns null
    /// if no matching report exists or the file fails to parse.
    /// </summary>
    StorageReport? GetStorageContents(string? character, string? server);

    /// <summary>Fires when the storage-report set changes (file created / updated / deleted).</summary>
    event EventHandler? StorageReportsChanged;

    // ── Character snapshots (Reports/Character_{Name}_{Server}.json) ───────

    /// <summary>Parsed <c>Character_*.json</c> exports, newest first.</summary>
    IReadOnlyList<CharacterSnapshot> CharacterSnapshots { get; }

    /// <summary>
    /// Newest character snapshot matching <paramref name="character"/> +
    /// <paramref name="server"/>. <paramref name="server"/> may be null/empty
    /// to match by name only.
    /// </summary>
    CharacterSnapshot? GetCharacterSnapshot(string? character, string? server);

    /// <summary>Fires when the character-snapshot set changes (export created / updated / deleted).</summary>
    event EventHandler? CharacterSnapshotsChanged;

    // ── Lifecycle ──────────────────────────────────────────────────────────

    /// <summary>Rescan the reports directory. Idempotent.</summary>
    void Refresh();
}
