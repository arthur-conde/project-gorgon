namespace Gandalf.Domain;

/// <summary>
/// Discriminator for the two loot-cooldown sub-feeds. Both kinds render through
/// one <c>LootSource</c> and share storage; the kind drives only catalog
/// derivation (chest cooldowns are game-emitted; defeat cooldowns come from
/// mithril-calibration) and the UI's filter chip.
/// </summary>
public enum LootKind { Chest, Defeat }

/// <summary>
/// Source-metadata payload attached to every loot <c>TimerCatalogEntry</c> so
/// callers can round-trip the kind without re-parsing the row key.
/// </summary>
public sealed record LootCatalogPayload(LootKind Kind, string InternalName, string? Region);
