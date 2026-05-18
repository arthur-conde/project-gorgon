namespace Legolas.Domain;

/// <summary>
/// Where a Motherlode location sample's world coordinate came from. The solver
/// is <b>source-agnostic</b> — it never branches on this; the value only feeds
/// the inverse-variance weight (accuracy ordering, #488). v1 produces the first
/// two; <see cref="Gazetteer"/>/<see cref="OverlayClick"/> are contract-ready
/// (additive feeders, deferred — they need declare-location UI #113 owns).
/// </summary>
public enum MotherlodePositionSource
{
    /// <summary>Feeder #1 — opportunistic <c>ProcessAddPlayer</c>/
    /// <c>ProcessNewPosition</c> (the only feeder whose Pᵢ <i>is</i> the frame
    /// the engine used for dᵢ; zero standoff). Highest confidence.</summary>
    LogPosition,

    /// <summary>Feeder #2 — a <c>ProcessMapPinAdd</c> pin dropped at the use
    /// spot. Exact-ish (map-read), no standoff. High confidence.</summary>
    MapPin,

    /// <summary>Feeder #2 (preferred, #497) — a pin the player labelled with
    /// their character name or <c>@me</c>: an unambiguous "I am here"
    /// declaration rather than an arbitrary nearby waypoint. Above
    /// <see cref="MapPin"/>, below an engine <see cref="LogPosition"/>.</summary>
    NamedMapPin,

    /// <summary>Feeder #3 — static landmark/NPC gazetteer point you are not
    /// standing on (~5–15 m systematic standoff bias). Tagged fallback,
    /// deferred.</summary>
    Gazetteer,

    /// <summary>Feeder #4 — overlay click through the projector inverse
    /// (±10% projector error). Last-resort, deferred.</summary>
    OverlayClick,
}

/// <summary>
/// One shared player-position fix <c>Pᵢ</c> in the area's local engine-unit
/// world frame, captured at a location where the player clicked their carried
/// Motherlode (metal-slab) maps. The full sample contract is
/// <c>{ World, Source, Confidence, Timestamp }</c> — anything emitting a known
/// world coord is a feeder and lands here uniformly (#488).
/// </summary>
/// <param name="World">Player world position (X/Z ground plane; Y elevation
/// kept but planar-ignored by the solver).</param>
/// <param name="Source">Which feeder produced it (weighting only).</param>
/// <param name="Confidence">Relative quality in [0,1]; the solver turns this
/// into an inverse-variance weight. Exact per-feeder mapping is calibrated
/// empirically (#488 open knob).</param>
/// <param name="Timestamp">UTC instant the fix was observed — used for
/// label-agnostic temporal pairing with the use + distance line.</param>
public readonly record struct MotherlodePositionSample(
    WorldCoord World,
    MotherlodePositionSource Source,
    double Confidence,
    DateTimeOffset Timestamp);

/// <summary>
/// One motherlode treasure being located. <see cref="DistancesByLocation"/> is
/// parallel to the shared <see cref="MotherlodeSession.LocationSamples"/> list:
/// element <c>k</c> is the ChatLog distance read at location <c>k</c> for this
/// slot. The k-th distance line emitted at a location binds to slot k — the
/// batching contract (no per-target identity exists in the log; a player
/// carries an inventory of maps and clicks them in a stable order at every
/// spot). Slots are solved independently against the shared samples.
/// </summary>
public sealed record MotherlodeSurvey(
    Guid Id,
    IReadOnlyList<int> DistancesByLocation,
    WorldCoord? SolvedWorld,
    double? Gdop,
    double? ResidualRms,
    bool Collected,
    int? RouteOrder)
{
    /// <summary>
    /// The solver's own verdict for this slot's last solve (#113 Layer 2). Kept
    /// verbatim from <see cref="Services.MultilaterationResult.Quality"/> rather
    /// than re-derived from <see cref="Gdop"/> in the UI, so the plain-language
    /// confidence badge can never drift from the solver's GDOP gate. Null until
    /// the slot has been solved at least once.
    /// </summary>
    public Services.MultilaterationQuality? Quality { get; init; }

    public static MotherlodeSurvey Create() =>
        new(Guid.NewGuid(), Array.Empty<int>(), null, null, null, false, null);
}

/// <summary>
/// In-flight Motherlode measurement state: the shared ordered set of player
/// position fixes (Pᵢ) and the per-treasure slots solved against them.
/// </summary>
public sealed class MotherlodeSession
{
    /// <summary>Shared Pᵢ — one entry per location the player measured at,
    /// in click order. All slots solve against this common set.</summary>
    public List<MotherlodePositionSample> LocationSamples { get; } = new();

    public List<MotherlodeSurvey> Surveys { get; } = new();
}
