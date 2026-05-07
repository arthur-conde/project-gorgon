using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace Legolas.Domain;

/// <summary>
/// Highlight overlay applied to the pin currently held in
/// <c>SessionState.SelectedSurvey</c> while the FSM is in <c>Listening</c>.
/// Kept distinct from <see cref="LegolasPinStyle"/> so users can change
/// "what active looks like" without touching the base pin appearance.
/// </summary>
public sealed class LegolasActivePinStyle : INotifyPropertyChanged
{
    private ActivePinTreatment _treatment = ActivePinTreatment.Halo;
    public ActivePinTreatment Treatment
    {
        get => _treatment;
        set => Set(ref _treatment, value);
    }

    private string _color = "#FFFFFFFF";
    public string Color
    {
        get => _color;
        set => SetHex(ref _color, value);
    }

    private double _haloThickness = 2.0;
    public double HaloThickness
    {
        get => _haloThickness;
        set => Set(ref _haloThickness, Math.Max(0, value));
    }

    private double _haloPaddingPx = 3.0;
    public double HaloPaddingPx
    {
        get => _haloPaddingPx;
        set => Set(ref _haloPaddingPx, Math.Max(0, value));
    }

    /// <summary>
    /// Blur radius of the <see cref="ActivePinTreatment.Glow"/> effect, in
    /// pixels. Larger values bleed further beyond the pin shape; 0 collapses
    /// the glow to nothing. Distinct from the halo's static ring — the glow
    /// is a soft <see cref="System.Windows.Media.Effects.DropShadowEffect"/>
    /// that doesn't compete visually with the in-game survey ping animation.
    /// </summary>
    private double _glowBlurRadius = 12.0;
    public double GlowBlurRadius
    {
        get => _glowBlurRadius;
        set => Set(ref _glowBlurRadius, Math.Max(0, value));
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
