using System.Globalization;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Media.Effects;
using Legolas.Domain;

namespace Legolas.Views.Converters;

/// <summary>
/// Returns a soft <see cref="DropShadowEffect"/> for the active pin when the
/// chosen treatment is <see cref="ActivePinTreatment.Glow"/>, else <c>null</c>.
/// Inputs: <c>(IsSelected, IsListening, ActivePinTreatment, ActivePin brush,
/// GlowBlurRadius)</c>. Distinct from <see cref="HaloVisibilityConverter"/>
/// which gates a static halo ring on the same conditions but for the Halo
/// treatment. The glow doesn't compete with the in-game survey ping animation
/// (which is a hard red ring shrinking inward) the way a static halo does.
/// </summary>
public sealed class GlowEffectConverter : IMultiValueConverter
{
    public static readonly GlowEffectConverter Instance = new();

    public object? Convert(object[] values, Type targetType, object? parameter, CultureInfo culture)
    {
        if (values.Length < 5) return null;
        var isSelected = values[0] is bool b1 && b1;
        var isListening = values[1] is bool b2 && b2;
        var treatment = values[2] is ActivePinTreatment t ? t : ActivePinTreatment.Halo;
        if (!(isSelected && isListening && treatment == ActivePinTreatment.Glow)) return null;

        var color = values[3] is SolidColorBrush brush ? brush.Color : Colors.White;
        var blur = values[4] switch
        {
            double d => d,
            int i => i,
            float f => f,
            _ => 0.0,
        };
        if (blur <= 0) return null;

        return new DropShadowEffect
        {
            Color = color,
            // ShadowDepth = 0 makes the effect a symmetric glow, not a
            // directional drop shadow. Opacity carries the brush's alpha.
            ShadowDepth = 0,
            BlurRadius = blur,
            Opacity = color.A / 255.0,
        };
    }

    public object[] ConvertBack(object value, Type[] targetTypes, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
