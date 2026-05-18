using Mithril.Shared.Logging;

namespace Mithril.GameState.Pins;

/// <summary>Whether a <see cref="MapPinLogEvent"/> places or removes a pin.</summary>
public enum MapPinChange
{
    /// <summary><c>ProcessMapPinAdd</c> — pin placed (also bulk-replayed on
    /// login / area entry).</summary>
    Added,

    /// <summary><c>ProcessMapPinRemove</c> — pin deleted. PG has <b>no</b>
    /// edit/move verb: a rename or move is a <see cref="Removed"/> of the old
    /// pin followed by an <see cref="Added"/> of the new one (same
    /// timestamp-second).</summary>
    Removed,
}

/// <summary>
/// One parsed <c>LocalPlayer: ProcessMapPin{Add,Remove}(A, B, C, (X, 0.00, Z),
/// "label")</c> line (#468). The pure line→event output of
/// <see cref="MapPinParser"/>; <see cref="PlayerPinTracker"/> folds a stream
/// of these into the area-scoped current set.
///
/// <para>Carries a <see cref="LogEvent"/>-mandated <see cref="DateTime"/>
/// timestamp (Player.log <c>[HH:MM:SS]</c> is UTC). It is converted to a
/// <see cref="DateTimeOffset"/> at the tracker boundary — model/notification
/// surfaces use <see cref="DateTimeOffset"/>; the parser interface is not
/// widened.</para>
/// </summary>
/// <param name="Timestamp">The source line's reconstructed UTC instant
/// (Player.log <c>[HH:MM:SS]</c> is UTC).</param>
/// <param name="Change">Whether this line places or removes a pin.</param>
/// <param name="X">Ground-plane east/west world coordinate (signed).</param>
/// <param name="Z">Ground-plane north/south world coordinate (signed; the
/// log's third triple component — the middle <c>Y</c> is dropped).</param>
/// <param name="Label">The player-typed label arg (may be empty).</param>
/// <param name="Shape">Decoded log arg <c>B</c>.</param>
/// <param name="Color">Decoded log arg <c>C</c>.</param>
/// <param name="RawList">Opaque leading log arg <c>A</c> (invariant
/// <c>1</c>); passed through unmodified.</param>
public sealed record MapPinLogEvent(
    DateTime Timestamp,
    MapPinChange Change,
    double X,
    double Z,
    string Label,
    PinShape Shape,
    PinColor Color,
    int RawList) : LogEvent(Timestamp);
