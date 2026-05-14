namespace Bilbo.ViewModels;

/// <summary>
/// Wraps the shared <see cref="StorageViewModel"/> for the Craftable Recipes
/// tab. Exposed as a distinct VM type so DataTemplate-by-type selects the
/// recipes view rather than the inventory view (both share the same
/// underlying VM).
/// </summary>
public sealed record CraftableRecipesTabViewModel(StorageViewModel Storage);
