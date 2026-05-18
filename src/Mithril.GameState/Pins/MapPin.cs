namespace Mithril.GameState.Pins;

/// <summary>
/// A single player-placed map pin in the current area. The <see cref="X"/>/
/// <see cref="Z"/> ground-plane coordinate is the same per-area engine-unit
/// world frame as <c>npcs.json</c> <c>Pos</c> / <c>landmarks.json</c>
/// <c>Loc</c> and the player's own position — verified signed, negatives are
/// common. The log's <c>Y</c> argument is always <c>0.00</c> for pins (they
/// are 2-D map markers) and is intentionally dropped.
///
/// <para><see cref="Label"/>, <see cref="Shape"/> and <see cref="Color"/> are
/// the player's own choices. They are <b>identity/UX metadata only</b> — a
/// pin is keyed by its coordinate, never by label (a rename keeps the
/// coordinate; see <see cref="PlayerPinTracker"/>). <see cref="RawList"/> is
/// the still-undecoded leading <c>A</c> argument (invariant <c>1</c> in every
/// capture); surfaced verbatim rather than interpreted.</para>
/// </summary>
/// <param name="X">Ground-plane east/west world coordinate (signed; the
/// log's first coordinate component).</param>
/// <param name="Z">Ground-plane north/south world coordinate (signed; the
/// log's third component — the middle <c>Y</c> is always 0 and dropped).</param>
/// <param name="Label">The player-typed pin label; may be empty (see
/// <see cref="DisplayName"/> for a non-blank fallback).</param>
/// <param name="Shape">Decoded from log arg <c>B</c> (the editor's "Design").</param>
/// <param name="Color">Decoded from log arg <c>C</c> (the editor's palette).</param>
/// <param name="RawList">The opaque, undecoded leading log arg <c>A</c>
/// (invariant <c>1</c> in every capture). Surfaced for forward-compat; do not
/// interpret.</param>
public sealed record MapPin(
    double X,
    double Z,
    string Label,
    PinShape Shape,
    PinColor Color,
    int RawList)
{
    /// <summary>
    /// Human phrase for the calibration UX, e.g. <c>"red square"</c> /
    /// <c>"dot"</c> (colour omitted when unknown). Lets the existing-pins
    /// route say "click where <em>Fire Magic 25</em> (red dot) is" without
    /// the colour/shape ever reaching the solver.
    /// </summary>
    public string Appearance
    {
        get
        {
            var c = Color.ToDisplayWord();
            var s = Shape.ToDisplayWord();
            return string.IsNullOrEmpty(c) ? s : $"{c} {s}";
        }
    }

    /// <summary>The label, or a stable fallback for unlabeled pins, so the
    /// calibration picker never shows a blank row.</summary>
    public string DisplayName =>
        string.IsNullOrWhiteSpace(Label) ? "Unnamed pin" : Label;
}
