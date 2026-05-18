namespace Silmarillion.ViewModels;

/// <summary>Discriminates the two entity kinds the unified Treasure tab catalogs.</summary>
public enum TreasureRowKind
{
    /// <summary>A <c>tsysclientinfo</c> power (the catalog's primary content, ~1946 rows).</summary>
    Power,

    /// <summary>A <c>tsysprofiles</c> profile / pool (40 rows).</summary>
    Profile,
}

/// <summary>
/// One row in the Treasure tab's unified master list. The tab catalogs both
/// <see cref="TreasureRowKind.Power"/> (tsysclientinfo) and
/// <see cref="TreasureRowKind.Profile"/> (tsysprofiles) so a single browse list serves
/// both entity kinds and both #214 deep-link targets (Power-select / pool-query); the
/// detail pane is polymorphic on <see cref="Kind"/>.
/// <para>
/// Public get-only surface is reflected into <c>MithrilQueryBox.Schema</c> (cookbook
/// step 6), so every property here is a queryable column. The card secondary line is
/// the plain <see cref="Secondary"/> string — no unit-letter prefixes (cookbook card
/// format rule).
/// </para>
/// </summary>
public sealed record TreasureListRow(
    TreasureRowKind Kind,
    string InternalName,
    string Name,
    string KindLabel,
    string? Skill,
    string Secondary,
    int TierCount,
    int PowerCount);
