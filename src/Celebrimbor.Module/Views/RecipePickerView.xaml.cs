using System.Windows.Controls;

namespace Celebrimbor.Views;

public partial class RecipePickerView : UserControl
{
    public RecipePickerView()
    {
        InitializeComponent();
        // The grid has no user-facing selection concept. The system styles still
        // paint a row as "selected" if focus lands there (e.g. clicking the + button),
        // which inverts the text colour. Neutralise it by clearing the selection
        // immediately whenever it changes.
        RecipesGrid.SelectionChanged += (_, _) =>
        {
            if (RecipesGrid.SelectedItems.Count > 0)
                RecipesGrid.UnselectAll();
        };
    }
}
