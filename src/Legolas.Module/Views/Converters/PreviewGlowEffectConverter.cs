using System.Globalization;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Media.Effects;
using Legolas.Domain;

namespace Legolas.Views.Converters;

/// <summary>
/// Preview-only sibling to <see cref="GlowEffectConverter"/>. Skips the
/// IsSelected / IsListening gates (those don't apply to a static preview)
/// and just returns a <see cref="DropShadowEffect"/> when the user has
/// <see cref="ActivePinTreatment.Glow"/> selected. Inputs:
/// <c>(ActivePinTreatment, ActivePin brush, GlowBlurRadius)</c>.
/// </summary>
public sealed class PreviewGlowEffectConverter : IMultiValueConverter
{
    public static readonly PreviewGlowEffectConverter Instance = new();

    public object? Convert(object[] values, Type targetType, object? parameter, CultureInfo culture)
    {
        if (values.Length < 3) return null;
        if (values[0] is not ActivePinTreatment treatment || treatment != ActivePinTreatment.Glow) return null;

        var color = values[1] is SolidColorBrush brush ? brush.Color : Colors.White;
        var blur = values[2] switch
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
            ShadowDepth = 0,
            BlurRadius = blur,
            Opacity = color.A / 255.0,
        };
    }

    public object[] ConvertBack(object value, Type[] targetTypes, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
