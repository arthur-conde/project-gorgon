namespace Mithril.Shared.Telemetry.Abstractions;

/// <summary>
/// Classifies a tag key by its expected privacy sensitivity. Drives the
/// default export state in <see cref="TagDescriptor.DefaultExported"/> and
/// the badge displayed in the settings tag-cloud.
/// </summary>
public enum PiiClassification
{
    /// <summary>Counts, timings, outcomes, instrument names — never PII.</summary>
    Safe,
    /// <summary>Identifying but not personally sensitive — module id, verb, file name.</summary>
    Identifying,
    /// <summary>Carries or may carry PII — character name, account-bearing path, message body.</summary>
    Sensitive,
}
