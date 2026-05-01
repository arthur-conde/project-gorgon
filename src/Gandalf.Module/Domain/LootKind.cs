namespace Gandalf.Domain;

/// <summary>
/// Discriminator for the two loot-cooldown sub-feeds. Both kinds render through
/// one <c>LootSource</c> and share storage; the kind drives only catalog
/// derivation (chest cooldowns are game-emitted; defeat cooldowns come from
/// auto-discovery + community calibration overlay) and the UI's filter chip.
/// </summary>
public enum LootKind { Chest, Defeat }

/// <summary>
/// Source-metadata payload attached to every loot <c>TimerCatalogEntry</c> so
/// callers can round-trip the kind without re-parsing the row key.
///
/// <para>
/// <see cref="IsDurationVerified"/> distinguishes calibrated durations
/// (community-aggregated, ✓) from the folklore-default placeholder used for
/// freshly-discovered bosses with no calibration entry yet. The UI surfaces
/// the difference so users know which timer they can trust to the second
/// vs which one is "best guess until someone observes it twice."
/// </para>
/// </summary>
public sealed record LootCatalogPayload(
    LootKind Kind,
    string InternalName,
    string? Region,
    bool IsDurationVerified = true);
