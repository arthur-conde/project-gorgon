using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Mithril.GameReports;

/// <summary>
/// Loads and projects character export JSON files (Character_{Name}_{Server}.json)
/// into <see cref="CharacterSnapshot"/> records.
/// </summary>
public static class CharacterReportLoader
{
    /// <summary>Deserialize and project a character export JSON file.</summary>
    public static CharacterSnapshot? Load(string filePath)
    {
        using var stream = File.OpenRead(filePath);
        var raw = JsonSerializer.Deserialize(stream, CharacterJsonContext.Default.RawCharacterExport);
        if (raw is null || raw.Report != "CharacterSheet") return null;

        var skills = new Dictionary<string, CharacterSkill>(StringComparer.Ordinal);
        if (raw.Skills is not null)
        {
            foreach (var (key, v) in raw.Skills)
            {
                skills[key] = new CharacterSkill(
                    v.Level ?? 0,
                    v.BonusLevels ?? 0,
                    v.XpTowardNextLevel ?? 0,
                    v.XpNeededForNextLevel ?? 0);
            }
        }

        DateTimeOffset exported = default;
        if (raw.Timestamp is not null)
        {
            DateTimeOffset.TryParse(raw.Timestamp, CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal, out exported);
        }

        var npcFavor = new Dictionary<string, string>(StringComparer.Ordinal);
        if (raw.NPCs is not null)
        {
            foreach (var (key, v) in raw.NPCs)
                if (!string.IsNullOrEmpty(v.FavorLevel))
                    npcFavor[key] = v.FavorLevel;
        }

        return new CharacterSnapshot(
            raw.Character ?? Path.GetFileNameWithoutExtension(filePath),
            raw.ServerName ?? "",
            exported,
            skills,
            raw.RecipeCompletions ?? new Dictionary<string, int>(),
            npcFavor);
    }
}

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

/// <summary>Raw NPC entry as it appears in a character export's NPCs section.</summary>
public sealed class RawNpcFavor
{
    public string? FavorLevel { get; set; }
}

/// <summary>Raw skill entry as it appears in a character export's Skills section.</summary>
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
