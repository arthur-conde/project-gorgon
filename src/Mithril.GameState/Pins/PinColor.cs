namespace Mithril.GameState.Pins;

/// <summary>
/// A map pin's <em>colour</em> — freely chosen per pin in the in-game pin
/// editor's "Color" palette. Decoded 2026-05-18 from the third positional
/// argument of <c>ProcessMapPin{Add,Remove}(A, B, <b>C</b>, (X,0,Z), "label")</c>
/// (#468). The palette is two rows of five; the index runs left→right, top
/// row then bottom row. <c>0 = White</c> is user-confirmed, <c>1 = Red</c> is
/// empirical (the captured campfire pins), <c>2–9</c> are ratified from the
/// palette order. There is <b>no system-assigned colour</b> — every value is
/// the player's deliberate choice, which is exactly what makes it a good
/// human disambiguator for #468's existing-pins calibration route.
/// </summary>
public enum PinColor
{
    /// <summary>Argument <c>C</c> outside 0–9 — kept, not thrown.</summary>
    Unknown = -1,

    White = 0,
    Red = 1,
    Orange = 2,
    Yellow = 3,
    Green = 4,
    Cyan = 5,
    Blue = 6,
    Purple = 7,
    Pink = 8,
    Black = 9,
}

/// <summary>Maps the raw <c>C</c> argument to a <see cref="PinColor"/>.</summary>
public static class PinColorExtensions
{
    /// <summary>Decode log arg <c>C</c> into a colour.</summary>
    /// <param name="rawColor">The palette index from
    /// <c>ProcessMapPin*</c>.</param>
    /// <returns>The matching <see cref="PinColor"/> for <c>0–9</c>, else
    /// <see cref="PinColor.Unknown"/> (never throws).</returns>
    public static PinColor ToPinColor(this int rawColor) =>
        rawColor is >= 0 and <= 9 ? (PinColor)rawColor : PinColor.Unknown;

    /// <summary>Lower-case human word for calibration UX ("<b>red</b> dot").</summary>
    /// <param name="color">The colour to render.</param>
    /// <returns>The lower-case colour name, or an <b>empty string</b> when
    /// unknown — callers (e.g. <see cref="MapPin.Appearance"/>) treat empty as
    /// "omit the colour word".</returns>
    public static string ToDisplayWord(this PinColor color) =>
        color == PinColor.Unknown ? "" : color.ToString().ToLowerInvariant();
}
