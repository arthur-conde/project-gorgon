namespace Gorgon.Shared.Storage;

/// <summary>
/// Watches the game's Reports directory for storage export files (*_items_*.json)
/// and raises <see cref="ReportsChanged"/> when the list changes.
/// </summary>
public interface IStorageReportWatcher : IDisposable
{
    /// <summary>Known storage export files, newest first.</summary>
    IReadOnlyList<ReportFileInfo> Reports { get; }

    /// <summary>Force a re-scan of the reports directory.</summary>
    void Refresh();

    /// <summary>Raised when the report list changes (new export, deleted export, directory change).</summary>
    event EventHandler? ReportsChanged;
}
