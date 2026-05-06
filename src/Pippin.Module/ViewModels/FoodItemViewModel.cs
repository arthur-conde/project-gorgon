using CommunityToolkit.Mvvm.ComponentModel;
using Pippin.Domain;

namespace Pippin.ViewModels;

public sealed partial class FoodItemViewModel : ObservableObject
{
    public FoodItemViewModel(FoodEntry catalog, bool isEaten, int eatenCount, int playerGourmandLevel)
    {
        Name = catalog.Name;
        FoodType = catalog.FoodType;
        FoodLevel = catalog.FoodLevel;
        GourmandLevelReq = catalog.GourmandLevelReq;
        DietaryTagList = catalog.DietaryTags;
        DietaryTags = string.Join(", ", catalog.DietaryTags);
        IconId = catalog.IconId;
        IsEaten = isEaten;
        EatenCount = eatenCount;
        IsLocked = !isEaten && GourmandLevelReq > 0 && playerGourmandLevel < GourmandLevelReq;
    }

    /// <summary>For foods eaten that don't appear in the CDN catalog.</summary>
    public FoodItemViewModel(string name, int eatenCount)
    {
        Name = name;
        FoodType = "Unknown";
        DietaryTagList = Array.Empty<string>();
        DietaryTags = "";
        IsEaten = true;
        EatenCount = eatenCount;
    }

    public string Name { get; }
    public string FoodType { get; }
    public int FoodLevel { get; }
    public int GourmandLevelReq { get; }
    public string DietaryTags { get; }
    public IReadOnlyList<string> DietaryTagList { get; }
    public int IconId { get; }
    public bool IsLocked { get; }
    public string? LockedReason => IsLocked ? $"Requires Gourmand {GourmandLevelReq}" : null;

    [ObservableProperty] private bool _isEaten;
    [ObservableProperty] private int _eatenCount;
}
