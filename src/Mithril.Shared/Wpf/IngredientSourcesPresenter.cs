using System.Windows;
using Mithril.Shared.Reference;

namespace Mithril.Shared.Wpf;

public sealed class IngredientSourcesPresenter : IIngredientSourcesPresenter
{
    private readonly IReferenceDataService _refData;

    public IngredientSourcesPresenter(IReferenceDataService refData)
    {
        _refData = refData;
    }

    public void Show(IngredientSourcesInput input)
    {
        var vm = IngredientSourcesViewModel.Build(input, _refData);
        var window = new IngredientSourcesWindow(vm)
        {
            Owner = Application.Current?.MainWindow,
        };
        window.Show();
    }
}
