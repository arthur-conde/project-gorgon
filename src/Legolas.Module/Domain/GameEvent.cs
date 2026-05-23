using Mithril.Shared.Logging;

namespace Legolas.Domain;

public abstract record GameEvent(DateTime Timestamp) : LogEvent(Timestamp);

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
/// diagnostic-only — placement uses <see cref="World"/> exclusively. Post-#606
/// the trailing <see cref="Message"/> also feeds the calibration verify-mode
/// <c>NoteSurvey</c> hook (the chat-side <c>[Status]</c> directional banner
/// retired); <see cref="PlayerLogParser.TryParseMapFxRelativeOffset"/> extracts
/// the inline relative offset from this string.</para>
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

/// <summary>
/// The <c>ProcessScreenText(ImportantInfo, "&lt;Mineral&gt; collected!")</c>
/// survey-yield readout (#606). Parsed from <b>Player.log</b> by
/// <see cref="PlayerLogParser"/>, retiring the prior chat <c>[Status]</c>
/// source. The Player.log payload is byte-identical to the retired chat line
/// minus the <c>[Status] </c> prefix — including the optional
/// <c>Also found &lt;Bonus&gt; x&lt;N&gt; (speed bonus!)</c> tail (parsed into
/// <see cref="SpeedBonusItem"/>).
///
/// <para><see cref="Count"/> is preserved as a field-shape carryover from the
/// retired chat parser; PG emits no count on the primary "collected!" line
/// (#699 then accepted this as a structural single-world property —
/// <see cref="ItemCollectionTracker"/> credits one per matched
/// (Add, Collect) pair against <c>IPlayerWorld.Bus</c>'s
/// <see cref="Mithril.GameState.Inventory.PlayerInventoryAdded"/> events, with
/// no separate quantity composition). So this always carries <c>1</c> from
/// <see cref="PlayerLogParser"/>.</para>
/// </summary>
public sealed record ItemCollected(
    DateTime Timestamp,
    string Name,
    int Count,
    string? SpeedBonusItem = null) : GameEvent(Timestamp);

// "[Status] X xN added to inventory." retired in #606. The Add channel for
// the state-machine attribution post-#699 is IPlayerWorld.Bus<
// PlayerInventoryAdded> — instance-id + InternalName, no quantities, single
// world (no cross-source view-layer composition). ItemCollectionTracker is
// the in-Legolas consumer.

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

// The chat-log area banner ("******* Entering Area: <FriendlyName>") was
// retired in #605. PlayerAreaTracker (Mithril.GameState.Areas) is the
// authoritative area-key source, fed by Player.log's LOADING LEVEL line, and
// PlayerLogIngestionService.ApplyAreaIfChanged drives the calibration service
// directly. See #531 for the redundancy analysis.
