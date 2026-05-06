using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace Legolas.Domain;

/// <summary>
/// User-configurable map overlay marker colors. Stored as ARGB hex strings
/// (e.g. <c>#FFFF0000</c>) for JSON friendliness and AlphA support (the bearing
/// wedges need translucency). Manual <see cref="INotifyPropertyChanged"/> for
/// the same reason as <see cref="InventoryGridSettings"/> — the CommunityToolkit
/// + STJ source generators race on partial properties and silently drop fields.
/// </summary>
public sealed class LegolasColors : INotifyPropertyChanged
{
    /// <summary>Pin shown for an uncorrected (projector-suggested) survey target.</summary>
    private string _pinPending = "#FF00FFFF"; // cyan
    public string PinPending
    {
        get => _pinPending;
        set => SetHex(ref _pinPending, value);
    }

    /// <summary>Pin shown for a manually-corrected (finalized) survey target.</summary>
    private string _pinFinalized = "#FFFF0000"; // red
    public string PinFinalized
    {
        get => _pinFinalized;
        set => SetHex(ref _pinFinalized, value);
    }

    /// <summary>Centre dot of the player position marker.</summary>
    private string _playerMarker = "#FF4CAF50"; // green
    public string PlayerMarker
    {
        get => _playerMarker;
        set => SetHex(ref _playerMarker, value);
    }

    /// <summary>Optimised route polyline.</summary>
    private string _routeLine = "#FFFFD700"; // gold
    public string RouteLine
    {
        get => _routeLine;
        set => SetHex(ref _routeLine, value);
    }

    /// <summary>Fill of each bearing-uncertainty wedge. Should be translucent.</summary>
    private string _bearingWedgeFill = "#33FFFF80";
    public string BearingWedgeFill
    {
        get => _bearingWedgeFill;
        set => SetHex(ref _bearingWedgeFill, value);
    }

    /// <summary>Outline of each bearing-uncertainty wedge.</summary>
    private string _bearingWedgeStroke = "#55FFFF80";
    public string BearingWedgeStroke
    {
        get => _bearingWedgeStroke;
        set => SetHex(ref _bearingWedgeStroke, value);
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void SetHex(ref string field, string? value, [CallerMemberName] string? name = null)
    {
        var normalized = Normalize(value);
        if (string.Equals(field, normalized, StringComparison.OrdinalIgnoreCase)) return;
        field = normalized;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }

    /// <summary>
    /// Coerces input to a canonical 8-digit ARGB hex string. Accepts either
    /// 6-digit (RGB; alpha defaults to FF) or 8-digit (ARGB) forms with or
    /// without a leading '#'. Invalid strings fall back to opaque magenta so
    /// the bug is visible rather than silently transparent.
    /// </summary>
    public static string Normalize(string? input)
    {
        if (string.IsNullOrWhiteSpace(input)) return "#FFFF00FF";
        var s = input.Trim();
        if (s.StartsWith("#")) s = s.Substring(1);
        return s.Length switch
        {
            6 when IsHex(s) => "#FF" + s.ToUpperInvariant(),
            8 when IsHex(s) => "#" + s.ToUpperInvariant(),
            _ => "#FFFF00FF",
        };
    }

    private static bool IsHex(string s)
    {
        foreach (var c in s)
        {
            if (!Uri.IsHexDigit(c)) return false;
        }
        return true;
    }
}
