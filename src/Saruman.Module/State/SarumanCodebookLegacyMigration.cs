using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using Mithril.Shared.Character;

namespace Saruman.State;

/// <summary>
/// Recovers the Word-of-Power codebook from the old two-file per-character
/// layout (<c>wop-discovery.json</c> + <c>wop-spent.json</c>) produced by the
/// pre-Arda <c>PlayerWordOfPowerDiscoveryStateService</c> and
/// <c>WordOfPowerView</c>. Merges both into a unified <see cref="SarumanCodebook"/>.
///
/// <para>The old files live in the same character directory
/// (<c>characters/{slug}/</c>) as the new <c>saruman-codebook.json</c>. On
/// successful migration the store deletes the legacy files.</para>
/// </summary>
public sealed class SarumanCodebookLegacyMigration : ILegacyMigration<SarumanCodebook>
{
    private readonly string _charactersRootDir;

    public SarumanCodebookLegacyMigration(string charactersRootDir)
    {
        _charactersRootDir = charactersRootDir;
    }

    public bool TryMigrate(string character, string server, out SarumanCodebook migrated, out string legacyPath)
    {
        migrated = new SarumanCodebook();
        legacyPath = "";

        var charDir = Path.Combine(_charactersRootDir, PerCharacterStore<SarumanCodebook>.Slug(character, server));
        var discoveryPath = Path.Combine(charDir, "wop-discovery.json");
        var spentPath = Path.Combine(charDir, "wop-spent.json");

        if (!File.Exists(discoveryPath) && !File.Exists(spentPath))
            return false;

        var discoveries = TryReadDiscovery(discoveryPath);
        var spent = TryReadSpent(spentPath);

        if (discoveries is null && spent is null)
            return false;

        if (discoveries is not null)
        {
            foreach (var (code, record) in discoveries.Discoveries)
            {
                DateTimeOffset? lastSpent = null;
                if (spent is not null && spent.SpentAt.TryGetValue(code, out var spentAt) && spentAt != default)
                    lastSpent = new DateTimeOffset(spentAt, TimeSpan.Zero);

                migrated.Entries[code] = new SarumanCodebook.CodebookEntry
                {
                    Code = code,
                    Effect = record.EffectName ?? "",
                    Description = record.Description,
                    DiscoveredAt = record.DiscoveredAt,
                    LastSpentAt = lastSpent,
                };
            }
        }

        // Spent codes that aren't in discovery (shouldn't happen, but defensive)
        if (spent is not null)
        {
            foreach (var (code, spentAt) in spent.SpentAt)
            {
                if (migrated.Entries.ContainsKey(code)) continue;
                migrated.Entries[code] = new SarumanCodebook.CodebookEntry
                {
                    Code = code,
                    Effect = "(unknown)",
                    Description = null,
                    DiscoveredAt = new DateTimeOffset(spentAt, TimeSpan.Zero),
                    LastSpentAt = new DateTimeOffset(spentAt, TimeSpan.Zero),
                };
            }
        }

        legacyPath = discoveryPath;
        return true;
    }

    private static LegacyDiscoveryState? TryReadDiscovery(string path)
    {
        if (!File.Exists(path)) return null;
        try
        {
            using var stream = File.OpenRead(path);
            return JsonSerializer.Deserialize(stream, LegacyDiscoveryJsonContext.Default.LegacyDiscoveryState);
        }
        catch { return null; }
    }

    private static LegacySpentState? TryReadSpent(string path)
    {
        if (!File.Exists(path)) return null;
        try
        {
            using var stream = File.OpenRead(path);
            return JsonSerializer.Deserialize(stream, LegacySpentJsonContext.Default.LegacySpentState);
        }
        catch { return null; }
    }
}

// ── Legacy DTOs (read-only, for migration) ─────────────────────────────────

internal sealed class LegacyDiscoveryState
{
    public int SchemaVersion { get; set; }
    public Dictionary<string, LegacyDiscoveryRecord> Discoveries { get; set; } = new(StringComparer.Ordinal);
}

internal sealed class LegacyDiscoveryRecord
{
    public string? Code { get; set; }
    public string? EffectName { get; set; }
    public string? Description { get; set; }
    public DateTimeOffset DiscoveredAt { get; set; }
}

internal sealed class LegacySpentState
{
    public int SchemaVersion { get; set; }
    public Dictionary<string, DateTime> SpentAt { get; set; } = new(StringComparer.Ordinal);
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(LegacyDiscoveryState))]
internal partial class LegacyDiscoveryJsonContext : JsonSerializerContext;

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(LegacySpentState))]
internal partial class LegacySpentJsonContext : JsonSerializerContext;
