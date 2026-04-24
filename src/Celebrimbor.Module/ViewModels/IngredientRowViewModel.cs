using Celebrimbor.Domain;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Celebrimbor.ViewModels;

public sealed partial class IngredientRowViewModel : ObservableObject
{
    public IngredientRowViewModel(AggregatedIngredient model, int? initialOverride, Action<string, int?> onOverrideChanged)
    {
        Model = model;
        _override = initialOverride;
        _onOverrideChanged = onOverrideChanged;
    }

    public AggregatedIngredient Model { get; private set; }
    private readonly Action<string, int?> _onOverrideChanged;
    private int? _override;

    public string ItemInternalName => Model.ItemInternalName;
    public string DisplayName => Model.DisplayName;
    public int IconId => Model.IconId;
    public string PrimaryTag => Model.PrimaryTag;
    public int TotalNeeded => Model.TotalNeeded;
    public double ExpectedNeeded => Model.ExpectedNeeded;
    public int OnHandDetected => Model.OnHandDetected;
    public bool IsCraftReady => EffectiveOnHand >= TotalNeeded;
    public int Remaining => Math.Max(0, TotalNeeded - EffectiveOnHand);
    public int EffectiveOnHand => Override ?? OnHandDetected;
    public IReadOnlyList<IngredientLocation> Locations => Model.Locations;
    public bool IsAlsoRecipe => Model.IsAlsoRecipe;

    /// <summary>Null means "use detected on-hand"; non-null overrides it.</summary>
    public int? Override
    {
        get => _override;
        set
        {
            if (_override == value) return;
            _override = value;
            OnPropertyChanged(nameof(Override));
            OnPropertyChanged(nameof(EffectiveOnHand));
            OnPropertyChanged(nameof(Remaining));
            OnPropertyChanged(nameof(IsCraftReady));
            _onOverrideChanged(ItemInternalName, value);
        }
    }

    public void UpdateModel(AggregatedIngredient model)
    {
        Model = model;
        OnPropertyChanged(string.Empty); // refresh everything
    }
}
