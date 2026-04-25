using System.Text.Json.Serialization;

namespace Mithril.Shared.Character;

/// <summary>Raw shape of Character_{Name}_{Server}.json as exported by the game.</summary>
public sealed class RawCharacterExport
{
    public string? Character { get; set; }
    public string? ServerName { get; set; }
    public string? Timestamp { get; set; }
    public string? Report { get; set; }
    public Dictionary<string, RawCharacterSkill>? Skills { get; set; }
    public Dictionary<string, int>? RecipeCompletions { get; set; }
    public Dictionary<string, RawNpcFavor>? NPCs { get; set; }
}

public sealed class RawNpcFavor
{
    public string? FavorLevel { get; set; }
}

public sealed class RawCharacterSkill
{
    public int? Level { get; set; }
    public int? BonusLevels { get; set; }
    public long? XpTowardNextLevel { get; set; }
    public long? XpNeededForNextLevel { get; set; }
}

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    PropertyNameCaseInsensitive = true,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(RawCharacterExport))]
public partial class CharacterJsonContext : JsonSerializerContext { }
