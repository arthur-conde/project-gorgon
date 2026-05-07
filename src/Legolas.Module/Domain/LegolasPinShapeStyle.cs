using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace Legolas.Domain;

/// <summary>
/// Configuration for one of the two survey-pin shapes (outer ring or centre
/// indicator). Both fill and stroke are independent so users can build
/// hollow / filled / outlined pins in any combination. Manual
/// <see cref="INotifyPropertyChanged"/> for the same reason as
/// <see cref="LegolasColors"/> — the CommunityToolkit + STJ source generators
/// race on partial properties and silently drop fields.
/// </summary>
public sealed class LegolasPinShapeStyle : INotifyPropertyChanged
{
    private PinShape _shape = PinShape.Circle;
    public PinShape Shape
    {
        get => _shape;
        set => Set(ref _shape, value);
    }

    private string _fillColor = "#01000000";
    public string FillColor
    {
        get => _fillColor;
        set => SetHex(ref _fillColor, value);
    }

    private string _strokeColor = "#FF00FFFF";
    public string StrokeColor
    {
        get => _strokeColor;
        set => SetHex(ref _strokeColor, value);
    }

    private PinStrokeStyle _strokeStyle = PinStrokeStyle.Dashed;
    public PinStrokeStyle StrokeStyle
    {
        get => _strokeStyle;
        set => Set(ref _strokeStyle, value);
    }

    private double _strokeThickness = 2.0;
    public double StrokeThickness
    {
        get => _strokeThickness;
        set => Set(ref _strokeThickness, Math.Max(0, value));
    }

    private double _size = 5.0;
    public double Size
    {
        get => _size;
        set => Set(ref _size, Math.Max(0, value));
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void SetHex(ref string field, string? value, [CallerMemberName] string? name = null)
    {
        var normalized = HexColor.Normalize(value);
        if (string.Equals(field, normalized, StringComparison.OrdinalIgnoreCase)) return;
        field = normalized;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }

    private void Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return;
        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}
