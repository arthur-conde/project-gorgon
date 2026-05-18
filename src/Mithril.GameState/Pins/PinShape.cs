namespace Mithril.GameState.Pins;

/// <summary>
/// A map pin's <em>shape</em> — the in-game pin editor's "Design" row, which
/// the player picks per pin. Decoded 2026-05-18 from the second positional
/// argument of <c>ProcessMapPin{Add,Remove}(A, <b>B</b>, C, (X,0,Z), "label")</c>
/// (#468): <c>B</c> is the design index. Only <see cref="Dot"/> ever appeared
/// in the captured logs; <see cref="Square"/> is the editor's second design.
/// </summary>
public enum PinShape
{
    /// <summary>Argument <c>B</c> outside the known range — kept rather than
    /// thrown so an unrecognised future design degrades gracefully.</summary>
    Unknown = -1,

    /// <summary><c>B = 0</c> — circle with a centre dot (the default).</summary>
    Dot = 0,

    /// <summary><c>B = 1</c> — square with a centre dot.</summary>
    Square = 1,
}

/// <summary>Maps the raw <c>B</c> argument to a <see cref="PinShape"/>.</summary>
public static class PinShapeExtensions
{
    /// <summary>Decode log arg <c>B</c> into a shape.</summary>
    /// <param name="rawDesign">The leading "Design" integer from
    /// <c>ProcessMapPin*</c>.</param>
    /// <returns>The matching <see cref="PinShape"/>, or
    /// <see cref="PinShape.Unknown"/> for an unrecognised value (never
    /// throws).</returns>
    public static PinShape ToPinShape(this int rawDesign) => rawDesign switch
    {
        0 => PinShape.Dot,
        1 => PinShape.Square,
        _ => PinShape.Unknown,
    };

    /// <summary>Lower-case human word for calibration UX ("red <b>dot</b>").</summary>
    /// <param name="shape">The shape to render.</param>
    /// <returns>"dot" / "square", or "pin" when unknown (never empty, so the
    /// UX phrase always reads naturally).</returns>
    public static string ToDisplayWord(this PinShape shape) => shape switch
    {
        PinShape.Dot => "dot",
        PinShape.Square => "square",
        _ => "pin",
    };
}
