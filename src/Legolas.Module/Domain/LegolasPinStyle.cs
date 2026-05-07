using System.ComponentModel;

namespace Legolas.Domain;

/// <summary>
/// Composite pin appearance: outer ring + centre indicator. Each
/// <see cref="LegolasPinShapeStyle"/> child is fully independent — fill,
/// stroke, shape, and stroke style are all separately configurable, so
/// "what a pin looks like" stays orthogonal to "what active means" (handled
/// by <see cref="LegolasActivePinStyle"/>).
/// </summary>
/// <remarks>
/// The outer shape's diameter intentionally is not duplicated here — it
/// remains driven by <c>LegolasSettings.SurveyPinRadiusMetres</c> so the
/// existing "Pin radius (px)" slider continues to work without a binding
/// rewrite. The centre shape carries its own <see cref="LegolasPinShapeStyle.Size"/>.
/// </remarks>
public sealed class LegolasPinStyle : INotifyPropertyChanged
{
    public LegolasPinStyle()
    {
        Outer = BuildOuterDefault();
        Center = BuildCenterDefault();
        Outer.PropertyChanged += OnChildChanged;
        Center.PropertyChanged += OnChildChanged;
    }

    public LegolasPinShapeStyle Outer { get; set; }
    public LegolasPinShapeStyle Center { get; set; }

    public event PropertyChangedEventHandler? PropertyChanged;

    private static LegolasPinShapeStyle BuildOuterDefault() => new()
    {
        Shape = PinShape.Circle,
        // Near-transparent fill keeps the outer ring hit-testable for the
        // Thumb's drag-handle behaviour while remaining visually empty.
        FillColor = "#01000000",
        StrokeColor = "#FF00FFFF",
        StrokeStyle = PinStrokeStyle.Dashed,
        StrokeThickness = 2.0,
        // Outer Size is unused — diameter is driven by SurveyPinRadiusMetres.
        Size = 0.0,
    };

    private static LegolasPinShapeStyle BuildCenterDefault() => new()
    {
        Shape = PinShape.Circle,
        FillColor = "#FF00FFFF",
        StrokeColor = "#00000000",
        StrokeStyle = PinStrokeStyle.None,
        StrokeThickness = 0.0,
        Size = 5.0,
    };

    /// <summary>
    /// Factory for the player anchor pin style. Defaults reproduce the v1
    /// hardcoded look: 18px white-stroked transparent circle outer + 2×2 green
    /// square centre dot (matches the old <c>LegolasColors.PlayerMarker</c>
    /// default of <c>#FF4CAF50</c>). Player pin's outer Size is meaningful
    /// (drives the Thumb bounds), unlike the survey pin where outer size is
    /// driven by <c>SurveyPinRadiusMetres</c>.
    /// </summary>
    public static LegolasPinStyle PlayerDefaults()
    {
        var style = new LegolasPinStyle();
        style.Outer.Shape = PinShape.Circle;
        style.Outer.FillColor = "#00000000";
        style.Outer.StrokeColor = "#FFFFFFFF";
        style.Outer.StrokeStyle = PinStrokeStyle.Solid;
        style.Outer.StrokeThickness = 2.0;
        style.Outer.Size = 18.0;

        style.Center.Shape = PinShape.Square;
        style.Center.FillColor = "#FF4CAF50";
        style.Center.StrokeColor = "#00000000";
        style.Center.StrokeStyle = PinStrokeStyle.None;
        style.Center.StrokeThickness = 0.0;
        style.Center.Size = 2.0;
        return style;
    }

    private void OnChildChanged(object? sender, PropertyChangedEventArgs e)
    {
        // Forward child-property names so brush/converter bindings against
        // PinStyle.Outer.* see the change. WPF's binding engine resolves
        // dotted paths by re-subscribing to the leaf's PropertyChanged, so
        // this is mostly belt-and-braces — but the LegolasBrushes forwarder
        // listens at this level, and that's the load-bearing consumer.
        var prefix = ReferenceEquals(sender, Outer) ? "Outer." : "Center.";
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(prefix + e.PropertyName));
    }
}
