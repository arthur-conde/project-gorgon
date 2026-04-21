using System.Text.RegularExpressions;
using Gorgon.Shared.Reference;

namespace Pippin.Domain;

/// <summary>
/// Builds a lookup of all edible foods from the CDN item catalog.
/// Keyed by display name (matching the in-game Foods Consumed report).
/// </summary>
public sealed partial class FoodCatalog
{
    private readonly IReferenceDataService _refData;
    private Dictionary<string, FoodEntry> _byName = new(StringComparer.OrdinalIgnoreCase);

    [GeneratedRegex(@"^Level\s+(\d+)\s+(.+)$")]
    private static partial Regex FoodDescRx();

    public FoodCatalog(IReferenceDataService refData)
    {
        _refData = refData;
        Build();
        _refData.FileUpdated += (_, _) => Build();
    }

    public IReadOnlyDictionary<string, FoodEntry> ByName => _byName;
    public int TotalCount => _byName.Count;

    private void Build()
    {
        var result = new Dictionary<string, FoodEntry>(600, StringComparer.OrdinalIgnoreCase);

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

            var entry = new FoodEntry(item.Id, item.Name, item.IconId, foodType, foodLevel, gourmandReq, dietaryTags);
            result[item.Name] = entry;
        }

        _byName = result;
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
