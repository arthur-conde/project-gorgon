using System.Diagnostics.CodeAnalysis;
using System.Text.RegularExpressions;
using Mithril.Shared.Reference;

namespace Pippin.Domain;

/// <summary>
/// Builds a lookup of all edible foods from the CDN item catalog. Exposes both
/// display-name and InternalName indices: the in-game Foods Consumed report only
/// gives display names, but persisted state and share payloads key by InternalName
/// so they survive renames and CDN drift.
/// </summary>
public sealed partial class FoodCatalog
{
    private readonly IReferenceDataService _refData;
    private Dictionary<string, FoodEntry> _byName = new(StringComparer.OrdinalIgnoreCase);
    private Dictionary<string, FoodEntry> _byInternalName = new(StringComparer.Ordinal);

    [GeneratedRegex(@"^Level\s+(\d+)\s+(.+)$")]
    private static partial Regex FoodDescRx();

    public FoodCatalog(IReferenceDataService refData)
    {
        _refData = refData;
        Build();
        _refData.FileUpdated += (_, _) =>
        {
            Build();
            CatalogChanged?.Invoke(this, EventArgs.Empty);
        };
    }

    public IReadOnlyDictionary<string, FoodEntry> ByName => _byName;
    public IReadOnlyDictionary<string, FoodEntry> ByInternalName => _byInternalName;
    public int TotalCount => _byInternalName.Count;

    /// <summary>True once at least one food entry has been resolved from reference data.</summary>
    public bool IsReady => _byInternalName.Count > 0;

    /// <summary>Fires after each <see cref="Build"/> completes — both initial load and CDN refreshes.</summary>
    public event EventHandler? CatalogChanged;

    /// <summary>Resolve a display name (case-insensitive) to its catalog entry.</summary>
    public bool TryGetByName(string name, [MaybeNullWhen(false)] out FoodEntry entry)
        => _byName.TryGetValue(name, out entry);

    /// <summary>Resolve an InternalName (case-sensitive) to its catalog entry.</summary>
    public bool TryGetByInternalName(string internalName, [MaybeNullWhen(false)] out FoodEntry entry)
        => _byInternalName.TryGetValue(internalName, out entry);

    private void Build()
    {
        var byName = new Dictionary<string, FoodEntry>(600, StringComparer.OrdinalIgnoreCase);
        var byInternal = new Dictionary<string, FoodEntry>(600, StringComparer.Ordinal);

        foreach (var item in _refData.Items.Values)
        {
            if (item.FoodDesc is null) continue;

            var m = FoodDescRx().Match(item.FoodDesc);
            if (!m.Success) continue;

            var foodLevel = int.Parse(m.Groups[1].Value);
            var foodType = m.Groups[2].Value; // "Meal", "Snack", "Instant Snack"

            var gourmandReq = 0;
            if (item.SkillReqs is not null && item.SkillReqs.TryGetValue("Gourmand", out var req))
                gourmandReq = req;

            var dietaryTags = ExtractDietaryTags(item.Keywords);

            var entry = new FoodEntry(item.Id, item.InternalName, item.Name, item.IconId, foodType, foodLevel, gourmandReq, dietaryTags);
            byName[item.Name] = entry;
            byInternal[item.InternalName] = entry;
        }

        _byName = byName;
        _byInternalName = byInternal;
    }

    private static IReadOnlyList<string> ExtractDietaryTags(IReadOnlyList<ItemKeyword> keywords)
    {
        var tags = new List<string>();
        foreach (var kw in keywords)
        {
            switch (kw.Tag)
            {
                case "VegetarianDish": tags.Add("Vegetarian"); break;
                case "VeganDish": tags.Add("Vegan"); break;
                case "DairyDish": tags.Add("Dairy"); break;
                case "Cheese": tags.Add("Dairy"); break;
                case "EggDish": tags.Add("Eggs"); break;
                case "FishDish": tags.Add("Fish"); break;
                case "MeatDish": tags.Add("Meat"); break;
            }
        }
        // Deduplicate
        return tags.Distinct().ToList();
    }
}
