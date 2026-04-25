using System.Windows.Controls;
using Elrond.Domain;
using Elrond.ViewModels;
using Mithril.Shared.Settings;
using Mithril.Shared.Wpf;

namespace Elrond.Views;

public partial class SkillAdvisorView : UserControl
{
    public SkillAdvisorView(ElrondSettings settings, SettingsAutoSaver<ElrondSettings> saver)
    {
        InitializeComponent();
        Loaded += (_, _) =>
        {
            if (DataContext is SkillAdvisorViewModel vm)
                DataGridStateBinder.Bind(RecipeGrid, settings.RecipeGrid, vm.ApplyRecipeFilter, saver.Touch);
        };
    }
}
