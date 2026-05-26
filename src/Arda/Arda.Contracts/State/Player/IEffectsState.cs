namespace Arda.World.Player;

/// <summary>
/// A single active effect on the local player, keyed by catalog id.
/// </summary>
public readonly record struct EffectStateEntry(
    int CatalogId,
    long? InstanceId,
    string? DisplayName,
    long SourceCharId,
    DateTimeOffset AppliedAt);

/// <summary>
/// Read-only view of the player's active effects. Catalog-id-keyed live set
/// with instance-to-catalog bridging via <c>ProcessUpdateEffectName</c>.
/// Session-scoped — resets on character switch.
/// </summary>
public interface IEffectsState
{
    /// <summary>Active effects keyed by catalog id.</summary>
    IReadOnlyDictionary<int, EffectStateEntry> ActiveEffects { get; }

    /// <summary>Try to retrieve a specific effect by catalog id.</summary>
    bool TryGet(int catalogId, out EffectStateEntry state);
}
