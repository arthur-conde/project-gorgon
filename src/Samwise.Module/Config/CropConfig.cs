using System.Text.Json.Serialization;

namespace Samwise.Config;

public sealed class SlotFamily
{
    public int Max { get; set; }
}

public sealed class CropDefinition
{
    public string SlotFamily { get; set; } = "";
    public int? GrowthSeconds { get; set; }
    public int? IconId { get; set; }
    public string? HarvestVerb { get; set; }
    public List<string>? ModelAliases { get; set; }
    public List<string>? ItemNamePrefixes { get; set; }
}

public sealed class CropConfig
{
    public int SchemaVersion { get; set; } = 1;
    public Dictionary<string, SlotFamily> SlotFamilies { get; set; } = new();
    public Dictionary<string, CropDefinition> Crops { get; set; } = new();

    [JsonIgnore]
    public IReadOnlyDictionary<string, string> ModelAliasToCrop => _modelAliasToCrop ??= BuildModelAliasMap();

    [JsonIgnore]
    public IReadOnlyDictionary<string, string> ItemPrefixToCrop => _itemPrefixToCrop ??= BuildItemPrefixMap();

    private Dictionary<string, string>? _modelAliasToCrop;
    private Dictionary<string, string>? _itemPrefixToCrop;

    public void InvalidateCaches() { _modelAliasToCrop = null; _itemPrefixToCrop = null; }

    public bool IsHarvestVerb(string crop, string verb)
    {
        if (!Crops.TryGetValue(crop, out var def)) return false;
        var expected = def.HarvestVerb ?? "Harvest";
        return string.Equals(verb, expected, StringComparison.OrdinalIgnoreCase);
    }

    private Dictionary<string, string> BuildModelAliasMap()
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (cropName, def) in Crops)
        {
            map[cropName] = cropName; // crop name itself is its primary alias
            if (def.ModelAliases is null) continue;
            foreach (var alias in def.ModelAliases)
                if (!map.ContainsKey(alias)) map[alias] = cropName;
        }
        return map;
    }

    private Dictionary<string, string> BuildItemPrefixMap()
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (cropName, def) in Crops)
        {
            var prefixes = def.ItemNamePrefixes ?? new List<string> { cropName.Split(' ')[0] };
            foreach (var p in prefixes)
                if (!map.ContainsKey(p)) map[p] = cropName;
        }
        return map;
    }
}

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    WriteIndented = true,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(CropConfig))]
public partial class CropConfigJsonContext : JsonSerializerContext { }
