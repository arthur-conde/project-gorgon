using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using Mithril.Shared.Character;

namespace Saruman.State;

/// <summary>
/// Recovers the Word-of-Power codebook from legacy per-character data.
/// Two source formats are checked in priority order:
///
/// <list type="number">
///   <item><b>V1 saruman.json</b> — the pre-#603 format that embedded the codebook
///   directly in <c>saruman.json</c> as a <c>codebook</c> field.</item>
///   <item><b>wop-discovery.json + wop-spent.json</b> — the intermediate #603 split
///   format (may never have been created for users who jumped straight from pre-#603
///   to post-Arda).</item>
/// </list>
///
/// <para>On successful migration the store deletes the source file(s).</para>
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

        var slug = PerCharacterStore<SarumanCodebook>.Slug(character, server);

        // Strategy 1: V1 saruman.json with embedded codebook field
        if (TryMigrateFromV1Saruman(slug, out migrated, out legacyPath))
            return true;

        // Strategy 2: Intermediate wop-discovery.json + wop-spent.json split
        if (TryMigrateFromSplitFiles(slug, out migrated, out legacyPath))
            return true;

        return false;
    }

    private bool TryMigrateFromV1Saruman(string slug, out SarumanCodebook migrated, out string legacyPath)
    {
        migrated = new SarumanCodebook();
        legacyPath = "";

        var path = Path.Combine(_charactersRootDir, slug, "saruman.json");
        var v1State = TryReadV1Saruman(path);
        if (v1State?.Codebook is null || v1State.Codebook.Count == 0)
            return false;

        foreach (var (code, entry) in v1State.Codebook)
        {
            DateTimeOffset? lastSpent = null;
            if (entry.State == 1 && entry.SpentAt is not null)
                lastSpent = entry.SpentAt.Value;

            migrated.Entries[code] = new SarumanCodebook.CodebookEntry
            {
                Code = entry.Code ?? code,
                Effect = entry.EffectName ?? "",
                Description = entry.Description,
                DiscoveredAt = entry.FirstDiscoveredAt,
                LastSpentAt = lastSpent,
            };
        }

        // Don't offer saruman.json for cleanup — PerCharacterStore<SarumanState> still uses it.
        return true;
    }

    private bool TryMigrateFromSplitFiles(string slug, out SarumanCodebook migrated, out string legacyPath)
    {
        migrated = new SarumanCodebook();
        legacyPath = "";

        var charDir = Path.Combine(_charactersRootDir, slug);
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

    private static LegacyV1SarumanState? TryReadV1Saruman(string? path)
    {
        if (string.IsNullOrEmpty(path) || !File.Exists(path)) return null;
        try
        {
            using var stream = File.OpenRead(path);
            var state = JsonSerializer.Deserialize(stream, LegacyV1SarumanJsonContext.Default.LegacyV1SarumanState);
            if (state is null || state.SchemaVersion > 1) return null;
            return state;
        }
        catch { return null; }
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

/// <summary>Pre-#603 saruman.json with embedded codebook.</summary>
internal sealed class LegacyV1SarumanState
{
    public int SchemaVersion { get; set; }
    public Dictionary<string, LegacyV1CodebookEntry>? Codebook { get; set; }
}

internal sealed class LegacyV1CodebookEntry
{
    public string? Code { get; set; }
    public string? EffectName { get; set; }
    public string? Description { get; set; }
    public DateTimeOffset FirstDiscoveredAt { get; set; }
    public int DiscoveryCount { get; set; }
    public int State { get; set; }
    public DateTimeOffset? SpentAt { get; set; }
}

/// <summary>Intermediate #603 split: wop-discovery.json.</summary>
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

/// <summary>Intermediate #603 split: wop-spent.json.</summary>
internal sealed class LegacySpentState
{
    public int SchemaVersion { get; set; }
    public Dictionary<string, DateTime> SpentAt { get; set; } = new(StringComparer.Ordinal);
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(LegacyV1SarumanState))]
internal partial class LegacyV1SarumanJsonContext : JsonSerializerContext;

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(LegacyDiscoveryState))]
internal partial class LegacyDiscoveryJsonContext : JsonSerializerContext;

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(LegacySpentState))]
internal partial class LegacySpentJsonContext : JsonSerializerContext;
