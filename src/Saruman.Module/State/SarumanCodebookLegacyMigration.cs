using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Hosting;

namespace Saruman.State;

/// <summary>
/// Recovers the Word-of-Power codebook from legacy per-character data.
/// Scans all character directories for v1 <c>saruman.json</c> files (which
/// embedded the codebook) or the intermediate <c>wop-discovery.json</c> +
/// <c>wop-spent.json</c> split. Extracts the server name from the directory
/// slug (<c>{character}_{server}</c>).
///
/// <para>Results are seeded into <see cref="SarumanCodebookService"/> at startup
/// via <see cref="SarumanCodebookLegacyMigrationHost"/>.</para>
/// </summary>
public sealed class SarumanCodebookLegacyMigration
{
    private readonly string _charactersRootDir;

    public SarumanCodebookLegacyMigration(string charactersRootDir)
    {
        _charactersRootDir = charactersRootDir;
    }

    /// <summary>
    /// Scan all character directories and return any recoverable codebook entries.
    /// </summary>
    public List<SarumanCodebook.CodebookEntry> RecoverAll()
    {
        var entries = new List<SarumanCodebook.CodebookEntry>();

        if (!Directory.Exists(_charactersRootDir))
            return entries;

        foreach (var charDir in Directory.EnumerateDirectories(_charactersRootDir))
        {
            var slug = Path.GetFileName(charDir);
            var server = ExtractServer(slug);
            if (server is null) continue;

            if (TryRecoverFromV1(charDir, server, entries))
                continue;

            TryRecoverFromSplitFiles(charDir, server, entries);
        }

        return entries;
    }

    /// <summary>
    /// Directory slug format is <c>{character}_{server}</c>. Server is the
    /// portion after the last underscore.
    /// </summary>
    private static string? ExtractServer(string slug)
    {
        var idx = slug.LastIndexOf('_');
        return idx > 0 ? slug[(idx + 1)..] : null;
    }

    private static bool TryRecoverFromV1(string charDir, string server, List<SarumanCodebook.CodebookEntry> entries)
    {
        var path = Path.Combine(charDir, "saruman.json");
        var v1 = TryReadV1Saruman(path);
        if (v1?.Codebook is null || v1.Codebook.Count == 0)
            return false;

        foreach (var (code, record) in v1.Codebook)
        {
            DateTimeOffset? lastSpent = null;
            if (record.State == 1 && record.SpentAt is not null)
                lastSpent = record.SpentAt.Value;

            entries.Add(new SarumanCodebook.CodebookEntry
            {
                Server = server,
                Code = record.Code ?? code,
                Effect = record.EffectName ?? "",
                Description = record.Description,
                DiscoveredAt = record.FirstDiscoveredAt,
                LastSpentAt = lastSpent,
            });
        }

        return true;
    }

    private static void TryRecoverFromSplitFiles(string charDir, string server, List<SarumanCodebook.CodebookEntry> entries)
    {
        var discoveryPath = Path.Combine(charDir, "wop-discovery.json");
        var spentPath = Path.Combine(charDir, "wop-spent.json");

        if (!File.Exists(discoveryPath) && !File.Exists(spentPath))
            return;

        var discoveries = TryReadDiscovery(discoveryPath);
        var spent = TryReadSpent(spentPath);

        if (discoveries is null && spent is null)
            return;

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        if (discoveries is not null)
        {
            foreach (var (code, record) in discoveries.Discoveries)
            {
                DateTimeOffset? lastSpent = null;
                if (spent is not null && spent.SpentAt.TryGetValue(code, out var spentAt) && spentAt != default)
                    lastSpent = new DateTimeOffset(spentAt, TimeSpan.Zero);

                entries.Add(new SarumanCodebook.CodebookEntry
                {
                    Server = server,
                    Code = code,
                    Effect = record.EffectName ?? "",
                    Description = record.Description,
                    DiscoveredAt = record.DiscoveredAt,
                    LastSpentAt = lastSpent,
                });
                seen.Add(code);
            }
        }

        if (spent is not null)
        {
            foreach (var (code, spentAt) in spent.SpentAt)
            {
                if (seen.Contains(code)) continue;
                entries.Add(new SarumanCodebook.CodebookEntry
                {
                    Server = server,
                    Code = code,
                    Effect = "(unknown)",
                    Description = null,
                    DiscoveredAt = new DateTimeOffset(spentAt, TimeSpan.Zero),
                    LastSpentAt = new DateTimeOffset(spentAt, TimeSpan.Zero),
                });
            }
        }
    }

    private static LegacyV1SarumanState? TryReadV1Saruman(string path)
    {
        if (!File.Exists(path)) return null;
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

/// <summary>
/// Runs the legacy codebook migration once at startup and seeds recovered
/// entries into <see cref="SarumanCodebookService"/>.
/// </summary>
public sealed class SarumanCodebookLegacyMigrationHost(
    SarumanCodebookLegacyMigration migration,
    SarumanCodebookService codebookService) : BackgroundService
{
    protected override Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var entries = migration.RecoverAll();
        if (entries.Count > 0)
            codebookService.SeedFromLegacy(entries);
        return Task.CompletedTask;
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
