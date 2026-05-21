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

// Map-pin lifecycle (ProcessMapPin{Add,Remove}) is no longer a Legolas
// GameEvent: it was promoted to the GameState-tier MapPinParser /
// PlayerPinTracker (#468). Calibration consumers read the area-scoped pin
// set from IPlayerPinTracker, not a Legolas log event.

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

/// <summary>
/// The motherlode-map distance readout — <c>"The treasure is N meters from here."</c>
/// As of #604 this is parsed from <b>Player.log</b>
/// (<c>LocalPlayer: ProcessScreenText(ImportantInfo, "The treasure is N meters from here.")</c>)
/// by <see cref="PlayerLogParser"/>, retiring the prior chat-log source. PG emits the
/// banner on Player.log first; the chat mirror was redundant. The migration collapses the
/// canonical Tier-2 cross-source coordinator (see <c>docs/cross-source-correlation.md</c>)
/// into a single-source intra-PlayerWorld pairing; the k-th-to-slot-k temporal binding in
/// <see cref="MotherlodeMeasurementCoordinator"/> is unchanged.
/// </summary>
public sealed record MotherlodeDistance(
    DateTime Timestamp,
    int DistanceMetres) : GameEvent(Timestamp);

/// <summary>
/// Player.log <c>LocalPlayer: ProcessDoDelayLoop(&lt;sec&gt;, &lt;verb&gt;,
/// "Using … Motherlode Map", …)</c> — the player clicked a carried metal-slab
/// (Motherlode) map. The <b>use gesture</b> the measurement coordinator
/// temporally correlates a position feeder fix and the following
/// <see cref="MotherlodeDistance"/> line(s) against (#488). Pairing/binding is
/// still order-based, not name-based — the use-line name is the map <i>type</i>
/// (identical across a same-type stack, no per-map identity), carried only as a
/// display label for the working slot it creates (create-on-use).
/// </summary>
public sealed record MotherlodeUseDetected(
    DateTime Timestamp,
    string? MapName = null) : GameEvent(Timestamp);

/// <summary>
/// The chat-log area banner (<c>"******* Entering Area: Eltibule"</c>). Carries
/// the area's <em>friendly</em> name; the calibration service resolves it to the
/// internal area key. A <b>complementary</b> signal: Player.log <i>does</i> have
/// an area marker (<c>LOADING LEVEL Area&lt;Name&gt;</c>, parsed by the shared
/// <c>PlayerAreaTracker</c> — #454/#456), which is the authoritative key source;
/// this chat banner is the fallback when the Player.log seed missed.
/// </summary>
public sealed record AreaEntered(
    DateTime Timestamp,
    string AreaFriendlyName) : GameEvent(Timestamp);

public sealed record UnknownLine(
    DateTime Timestamp,
    string RawLine) : GameEvent(Timestamp);
