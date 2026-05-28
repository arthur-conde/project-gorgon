namespace Mithril.Shared.Telemetry.Export;

/// <summary>
/// Snapshot of OTLP exporter health for display in the settings status line.
/// Updated by <see cref="ExporterHealthMonitor"/> as the exporter's success /
/// failure events flow in. All fields nullable so the initial state ("no
/// activity yet") is representable.
/// </summary>
public sealed record ExporterHealth(
    DateTimeOffset? LastSuccessUtc,
    DateTimeOffset? LastFailureUtc,
    string? LastError);
