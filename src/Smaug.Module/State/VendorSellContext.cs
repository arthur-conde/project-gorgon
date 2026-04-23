namespace Smaug.State;

/// <summary>
/// Current live state used to attribute a <c>ProcessVendorAddItem</c> sale back
/// to the correct (NpcKey, FavorTier, CivicPrideLevel) triple. All fields are
/// null/zero until the relevant log events have been observed.
/// </summary>
public sealed class VendorSellContext
{
    /// <summary>EntityId of the NPC whose vendor screen is currently open, if any.</summary>
    public int? ActiveVendorEntityId { get; set; }

    /// <summary>Favor tier at the moment the vendor screen opened.</summary>
    public string? ActiveFavorTier { get; set; }

    /// <summary>NPC key resolved from ProcessStartInteraction for the active entityId.</summary>
    public string? ActiveNpcKey { get; set; }

    /// <summary>Most recently observed Civic Pride effective level (raw + bonus).</summary>
    public int CivicPrideLevel { get; set; }

    /// <summary>Rolling map of entityId → NPC_Key for resolving vendor screens.</summary>
    public Dictionary<int, string> EntityToNpc { get; } = new();

    /// <summary>Trim when we reach this many entries to keep memory flat on long sessions.</summary>
    private const int MaxEntityMapSize = 4000;

    public void RememberEntity(int entityId, string npcKey)
    {
        EntityToNpc[entityId] = npcKey;
        if (EntityToNpc.Count > MaxEntityMapSize)
        {
            // Drop the oldest half in insertion order (Dictionary preserves insertion order in .NET).
            var toRemove = EntityToNpc.Keys.Take(MaxEntityMapSize / 2).ToList();
            foreach (var k in toRemove) EntityToNpc.Remove(k);
        }
    }

    public void OnVendorScreenOpened(int entityId, string favorTier)
    {
        ActiveVendorEntityId = entityId;
        ActiveFavorTier = favorTier;
        EntityToNpc.TryGetValue(entityId, out var npcKey);
        ActiveNpcKey = npcKey;
    }

    public void Clear()
    {
        ActiveVendorEntityId = null;
        ActiveFavorTier = null;
        ActiveNpcKey = null;
    }

    public bool IsReadyToRecord =>
        !string.IsNullOrEmpty(ActiveNpcKey) && !string.IsNullOrEmpty(ActiveFavorTier);
}
