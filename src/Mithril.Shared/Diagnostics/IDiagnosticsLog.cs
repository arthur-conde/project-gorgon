namespace Mithril.Shared.Diagnostics;

/// <summary>
/// In-memory diagnostics ring buffer plus a hot live stream for the Shell UI.
/// Populated by <see cref="DiagnosticsLoggerProvider"/> from <see cref="Microsoft.Extensions.Logging.ILogger"/>.
/// </summary>
public interface IDiagnosticsLog
{
    IReadOnlyList<DiagnosticEntry> Snapshot();

    /// <summary>Hot stream of entries; does not complete for the app lifetime.</summary>
    IObservable<DiagnosticEntry> Live { get; }
}
