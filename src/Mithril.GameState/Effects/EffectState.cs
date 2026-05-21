namespace Mithril.GameState.Effects;

/// <summary>
/// A single active effect on the local player, as derived from Player.log's
/// <c>ProcessAddEffects</c> / <c>ProcessRemoveEffects</c> /
/// <c>ProcessUpdateEffectName</c> verbs.
///
/// <para><b>Identity model.</b> Two id spaces are at play: the small
/// <see cref="CatalogId"/> from <c>effects.json</c> (the Add-side identifier,
/// e.g. <c>302</c> for Performance Appreciation base), and the large
/// per-application <see cref="InstanceId"/> minted by the game engine and
/// surfaced only via <c>ProcessUpdateEffectName</c> and
/// <c>ProcessRemoveEffects</c> (e.g. <c>259320</c>). v1 keys the live set by
/// <see cref="CatalogId"/> (single-instance-per-catalog-id; see issue #590
/// "Known unknowns"). <see cref="InstanceId"/> is populated lazily when (and
/// only if) a same-timestamp <c>ProcessUpdateEffectName</c> correlates back.
/// Catalog-id-only effects (the majority — equipment bonuses, character
/// traits) leave <see cref="InstanceId"/> null and cannot be removed by id;
/// they linger until snapshot replay reconciles them.</para>
///
/// <para><b>DisplayName.</b> Present only when PG fired a
/// <c>ProcessUpdateEffectName</c> for this entry — typical for level-tagged
/// or runtime-generated names (e.g. <c>"Performance Appreciation, Level 0"</c>).
/// Consumers needing a name for catalog-id-only entries resolve it via
/// <c>IReferenceDataService.Effects</c>.</para>
///
/// <para><b>SourceCharId.</b> The character that applied the effect.
/// <c>0</c> is observed exclusively on <c>arg4 == False</c> login / zone-replay
/// bursts and is the "system / no applier on replay" sentinel; live
/// applications carry a non-zero applier (equal to the target's own char-id
/// for self-applied buffs).</para>
///
/// <para><b>AppliedAt.</b> UTC instant from the source log line. Re-emits of
/// an already-tracked catalog id refresh the timestamp (see issue #590
/// "Behaviour" section, idempotent timestamp-refresh rule mirroring
/// <see cref="Mithril.GameState.Inventory.InventoryService"/>'s add-reemit
/// handling).</para>
/// </summary>
public readonly record struct EffectState(
    int CatalogId,
    long? InstanceId,
    string? DisplayName,
    long SourceCharId,
    DateTimeOffset AppliedAt);

/// <summary>
/// Lifecycle event for a single <see cref="EffectState"/>. <see cref="Kind"/>
/// distinguishes the three transitions the service surfaces;
/// <see cref="Timestamp"/> is the UTC instant of the source log line (same
/// as <see cref="EffectState.AppliedAt"/> on <see cref="EffectEventKind.Added"/>
/// and the originating event's line on the other two kinds).
/// </summary>
public readonly record struct EffectEvent(
    EffectEventKind Kind,
    EffectState State,
    DateTimeOffset Timestamp);

/// <summary>Kind of an <see cref="EffectEvent"/>.</summary>
public enum EffectEventKind
{
    /// <summary>New catalog id added (active <c>True</c> apply or
    /// additive <c>False</c> snapshot reconcile).</summary>
    Added,

    /// <summary>A <c>ProcessRemoveEffects</c> by instance id removed an entry
    /// that had previously been correlated to that instance via
    /// <see cref="DisplayNameChanged"/>. Catalog-id-only entries never
    /// surface a <c>Removed</c> by construction (no instance-id bridge).</summary>
    Removed,

    /// <summary>A <c>ProcessUpdateEffectName</c> assigned a display name (and
    /// the originating instance id) to an existing entry. Fires once per Update
    /// line; subsequent Updates for the same instance also fire if the name
    /// actually changes.</summary>
    DisplayNameChanged,
}
