namespace Celebrimbor.Domain;

/// <summary>
/// User-entered on-hand count for an ingredient. Overrides whatever the
/// active character's storage export reports when present (i.e. non-null
/// Quantity). Keyed by item InternalName to survive reference-data refreshes.
/// </summary>
public sealed class ManualOnHandOverride
{
    public string ItemInternalName { get; set; } = "";
    public int Quantity { get; set; }
}
