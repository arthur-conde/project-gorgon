using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using Legolas.Domain;

namespace Legolas.ViewModels;

public sealed partial class SurveyItemViewModel : ObservableObject
{
    [ObservableProperty]
    private Survey _model;

    [ObservableProperty]
    private Geometry? _wedgeGeometry;

    [ObservableProperty]
    private bool _isActiveTarget;

    public SurveyItemViewModel(Survey model)
    {
        _model = model;
    }

    public Guid Id => Model.Id;
    public string Name => Model.Name;
    public MetreOffset Offset => Model.Offset;
    public int GridIndex => Model.GridIndex;
    public bool Collected => Model.Collected;
    public bool Skipped => Model.Skipped;
    public int? RouteOrder => Model.RouteOrder;
    public PixelPoint? EffectivePixel => Model.EffectivePixel;
    public bool IsCorrected => Model.IsCorrected;

    public double X => EffectivePixel?.X ?? 0;
    public double Y => EffectivePixel?.Y ?? 0;
    public bool HasPixel => EffectivePixel.HasValue;
    public bool IsVisible => HasPixel && !Collected;

    public void UpdateModel(Survey model) => Model = model;

    partial void OnModelChanged(Survey value)
    {
        OnPropertyChanged(nameof(Name));
        OnPropertyChanged(nameof(Offset));
        OnPropertyChanged(nameof(GridIndex));
        OnPropertyChanged(nameof(Collected));
        OnPropertyChanged(nameof(Skipped));
        OnPropertyChanged(nameof(RouteOrder));
        OnPropertyChanged(nameof(EffectivePixel));
        OnPropertyChanged(nameof(IsCorrected));
        OnPropertyChanged(nameof(X));
        OnPropertyChanged(nameof(Y));
        OnPropertyChanged(nameof(HasPixel));
        OnPropertyChanged(nameof(IsVisible));
    }
}
