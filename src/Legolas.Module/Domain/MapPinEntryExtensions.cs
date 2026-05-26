using Arda.World.Player;

namespace Legolas.Domain;

/// <summary>
/// Presentation helpers for <see cref="MapPinEntry"/> — restores the
/// <c>Appearance</c> and <c>DisplayName</c> properties that lived on the
/// retired <c>Mithril.GameState.Pins.MapPin</c>.
/// </summary>
internal static class MapPinEntryExtensions
{
    public static string Appearance(this MapPinEntry pin)
    {
        var color = ColorWord(pin.Color);
        var shape = ShapeWord(pin.Shape);
        return string.IsNullOrEmpty(color) ? shape : $"{color} {shape}";
    }

    public static string DisplayName(this MapPinEntry pin) =>
        string.IsNullOrWhiteSpace(pin.Label) ? "Unnamed pin" : pin.Label;

    private static string ShapeWord(int shape) => shape switch
    {
        0 => "dot",
        1 => "square",
        _ => "pin",
    };

    private static string ColorWord(int color) => color switch
    {
        0 => "white",
        1 => "red",
        2 => "orange",
        3 => "yellow",
        4 => "green",
        5 => "cyan",
        6 => "blue",
        7 => "purple",
        8 => "pink",
        9 => "black",
        _ => "",
    };
}
