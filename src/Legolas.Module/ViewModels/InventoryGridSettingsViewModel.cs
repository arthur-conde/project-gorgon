using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Legolas.Domain;

namespace Legolas.ViewModels;

public sealed partial class InventoryGridSettingsViewModel : ObservableObject
{
    public InventoryGridSettingsViewModel(InventoryGridSettings grid)
    {
        Grid = grid;
    }

    public InventoryGridSettings Grid { get; }

    [RelayCommand]
    private void ResetToDefaults()
    {
        Grid.Columns = 10;
        Grid.CellWidth = 50;
        Grid.CellHeight = 50;
        Grid.ColumnGap = 2;
        Grid.RowGap = 2;
    }
}
