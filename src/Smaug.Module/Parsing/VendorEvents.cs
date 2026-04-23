namespace Smaug.Parsing;

public abstract record VendorEvent(DateTime Timestamp);

/// <summary>
/// Emitted when the player opens a vendor shop. Captures the favor tier
/// at that moment plus the vendor's current gold pool.
/// </summary>
public sealed record VendorScreenOpened(
    DateTime Timestamp,
    int EntityId,
    string FavorTier,
    long RemainingGold,
    long GoldCap) : VendorEvent(Timestamp);

/// <summary>
/// Emitted on every <c>ProcessVendorAddItem</c> — the player sold one item
/// to the open vendor. Price is the exact gold paid.
/// </summary>
public sealed record VendorItemSold(
    DateTime Timestamp,
    long Price,
    string InternalName,
    long InstanceId) : VendorEvent(Timestamp);

/// <summary>Emitted on <c>ProcessVendorUpdateAvailableGold</c>.</summary>
public sealed record VendorGoldUpdated(
    DateTime Timestamp,
    long RemainingGold,
    long GoldCap) : VendorEvent(Timestamp);

/// <summary>
/// Maps an entity id to an NPC key — the game logs <c>ProcessStartInteraction</c>
/// just before opening any NPC screen. We stash this so we can resolve the
/// entity id in <c>ProcessVendorScreen</c> back to a real NPC key.
/// </summary>
public sealed record NpcInteractionStarted(
    DateTime Timestamp,
    int EntityId,
    string NpcKey) : VendorEvent(Timestamp);

/// <summary>
/// Civic Pride skill snapshot from <c>ProcessLoadSkills</c> (session start) or
/// <c>ProcessUpdateSkill</c> (on level up). Effective level is <c>Raw + Bonus</c>.
/// </summary>
public sealed record CivicPrideUpdated(
    DateTime Timestamp,
    int Raw,
    int Bonus) : VendorEvent(Timestamp)
{
    public int EffectiveLevel => Raw + Bonus;
}

