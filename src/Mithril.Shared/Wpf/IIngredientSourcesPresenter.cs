namespace Mithril.Shared.Wpf;

/// <summary>
/// Opens an <see cref="IngredientSourcesWindow"/> for a given input. Modules
/// project their domain rows into <see cref="IngredientSourcesInput"/> and
/// dispatch through this presenter.
/// </summary>
public interface IIngredientSourcesPresenter
{
    void Show(IngredientSourcesInput input);
}
