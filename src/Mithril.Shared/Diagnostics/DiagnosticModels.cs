namespace Mithril.Shared.Diagnostics;

public enum DiagnosticLevel { Trace, Info, Warn, Error }

public sealed record DiagnosticEntry(
    DateTime Timestamp,
    DiagnosticLevel Level,
    string Category,
    string Message);
