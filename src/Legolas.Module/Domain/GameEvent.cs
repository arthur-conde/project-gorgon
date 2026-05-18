using Mithril.Shared.Logging;

namespace Legolas.Domain;

public abstract record GameEvent(DateTime Timestamp) : LogEvent(Timestamp);

public sealed record SurveyDetected(
    DateTime Timestamp,
    string Name,
    MetreOffset Offset) : GameEvent(Timestamp);

/// <summary>
/// Player.log <c>LocalPlayer: ProcessMapFx((X,Y,Z), …, "&lt;short&gt;",
/// &lt;Category&gt;, "&lt;msg&gt;")</c> — emitted once per survey
/// (<c>Check Survey</c>, Mining/Geology) or treasure-map (<c>Check Map</c>,
/// Treasure Cartography) use, carrying the target's <b>exact absolute world
/// coordinate</b> (#454). Verified invariant in <c>(X,Z)</c> across re-pings
/// while the relative <c>&lt;msg&gt;</c> text drifts. Keyed off
/// <c>ProcessMapFx</c> itself, not the use-verb, so both survey families are
/// handled uniformly; Motherlode is auto-excluded (it emits
/// <c>ProcessScreenText</c>, never <c>ProcessMapFx</c>).
///
/// <para><see cref="Short"/>/<see cref="Category"/>/<see cref="Message"/> are
/// diagnostic-only — placement uses <see cref="World"/> exclusively.</para>
/// </summary>
public sealed record MapTargetDetected(
    DateTime Timestamp,
    WorldCoord World,
    string Short,
    string Category,
    string Message) : GameEvent(Timestamp);

public sealed record ItemCollected(
    DateTime Timestamp,
    string Name,
    int Count,
    string? SpeedBonusItem = null) : GameEvent(Timestamp);

/// <summary>
/// "[Status] X xN added to inventory." — the only chat line that carries the real
/// item count. The matching <see cref="ItemCollected"/> line that follows has no
/// count for survey collections (PG moved counts onto "added to inventory" lines).
/// LogIngestionService buffers these and drains the buffer on the next
/// <see cref="ItemCollected"/>, so non-survey adds (skinning, crafting, vendor
/// purchases) don't leak into the survey report.
/// </summary>
public sealed record ItemAddedToInventory(
    DateTime Timestamp,
    string Name,
    int Count) : GameEvent(Timestamp);

public sealed record MotherlodeDistance(
    DateTime Timestamp,
    int DistanceMetres) : GameEvent(Timestamp);

/// <summary>
/// The chat-log area banner (<c>"******* Entering Area: Eltibule"</c>). Carries
/// the area's <em>friendly</em> name (the only thing the log gives); the calibration
/// service resolves it to the internal area key. This is the sole area signal
/// available — Player.log has no area-change marker.
/// </summary>
public sealed record AreaEntered(
    DateTime Timestamp,
    string AreaFriendlyName) : GameEvent(Timestamp);

public sealed record UnknownLine(
    DateTime Timestamp,
    string RawLine) : GameEvent(Timestamp);
