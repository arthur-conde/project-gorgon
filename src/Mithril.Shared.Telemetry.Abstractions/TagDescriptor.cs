namespace Mithril.Shared.Telemetry.Abstractions;

/// <summary>
/// Declares a tag key that a producer may emit on spans or metric instruments.
/// Subsystems contribute these via <see cref="ITagDescriptorProvider"/> so the
/// telemetry catalog (and the settings tag-cloud UI) knows about every known
/// key without runtime discovery.
/// </summary>
/// <param name="Key">Tag key as it appears on the Activity/Measurement (e.g. <c>"module.id"</c>).</param>
/// <param name="Classification">PII classification driving the default export state.</param>
/// <param name="Subsystem">Human-readable subsystem grouping for the UI (e.g. <c>"Mithril.Arda.Player"</c>).</param>
/// <param name="Description">One-line description shown in the chip tooltip.</param>
public sealed record TagDescriptor(
    string Key,
    PiiClassification Classification,
    string Subsystem,
    string Description)
{
    /// <summary>Default export state — Safe and Identifying default ON, Sensitive defaults OFF.</summary>
    public bool DefaultExported => Classification != PiiClassification.Sensitive;
}
