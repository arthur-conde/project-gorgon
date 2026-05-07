using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Text.Json.Serialization;

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

    /// <summary>
    /// v1 single-colour pin field. Removed from the public surface in v2 — this
    /// pair stays only so <see cref="LegolasSettings.Migrate"/> can read the
    /// stored value and carry it forward into <c>PinStyle.Outer.StrokeColor</c>
    /// and <c>PinStyle.Center.FillColor</c>. The migrator clears it back to
    /// <c>null</c> so it doesn't reappear in saved JSON.
    /// </summary>
    [JsonInclude]
    [JsonPropertyName("pinPending")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? LegacyPinPending { get; set; }

    /// <summary>
    /// v1 corrected-pin colour. Unused on main since #130 collapsed the
    /// click-to-confirm flow. Migrated forward into
    /// <c>ActivePinStyle.Color</c> so users who customised it don't lose it.
    /// </summary>
    [JsonInclude]
    [JsonPropertyName("pinFinalized")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? LegacyPinFinalized { get; set; }

    /// <summary>
    /// v1 player anchor centre-dot colour. Removed from the public surface in
    /// v2; <see cref="LegolasSettings.Migrate"/> copies it into
    /// <c>PlayerPinStyle.Center.FillColor</c> so users who customised the
    /// player marker keep their colour. Cleared after migration.
    /// </summary>
    [JsonInclude]
    [JsonPropertyName("playerMarker")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? LegacyPlayerMarker { get; set; }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void SetHex(ref string field, string? value, [CallerMemberName] string? name = null)
    {
        var normalized = HexColor.Normalize(value);
        if (string.Equals(field, normalized, StringComparison.OrdinalIgnoreCase)) return;
        field = normalized;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}
