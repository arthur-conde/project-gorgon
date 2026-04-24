using Gorgon.Shared.Reference;

namespace Celebrimbor.Services;

/// <summary>
/// Case-insensitive substring search over every known recipe. Rebuilds when the
/// reference data file 'recipes' is updated. Results are sorted by skill then name.
/// </summary>
public sealed class RecipeSearchIndex
{
    private readonly IReferenceDataService _refData;
    private IReadOnlyList<RecipeEntry> _all;

    public RecipeSearchIndex(IReferenceDataService refData)
    {
        _refData = refData;
        _all = Snapshot(refData);
        refData.FileUpdated += OnFileUpdated;
    }

    public IReadOnlyList<RecipeEntry> AllRecipes => _all;

    public IEnumerable<RecipeEntry> Search(string? query)
    {
        if (string.IsNullOrWhiteSpace(query)) return _all;
        return _all.Where(r =>
            r.Name.Contains(query, StringComparison.OrdinalIgnoreCase) ||
            r.InternalName.Contains(query, StringComparison.OrdinalIgnoreCase) ||
            r.Skill.Contains(query, StringComparison.OrdinalIgnoreCase));
    }

    private void OnFileUpdated(object? sender, string key)
    {
        if (!string.Equals(key, "recipes", StringComparison.OrdinalIgnoreCase)) return;
        _all = Snapshot(_refData);
    }

    private static IReadOnlyList<RecipeEntry> Snapshot(IReferenceDataService refData)
        => refData.Recipes.Values
            .OrderBy(r => r.Skill, StringComparer.OrdinalIgnoreCase)
            .ThenBy(r => r.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
}
