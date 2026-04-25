using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using Gandalf.Domain;
using Mithril.Shared.Character;
using Mithril.Shared.Diagnostics;
using Mithril.Shared.Settings;
using Microsoft.Extensions.Hosting;

namespace Gandalf.Services;

/// <summary>
/// One-shot startup migration that splits the pre-commit v1 per-character <c>gandalf.json</c>
/// blobs (where each character had its own list of full timers mixing definition + progress)
/// into the new split shape: a global <see cref="GandalfDefinitions"/> file plus per-character
/// <see cref="GandalfProgress"/> files.
///
/// Runs during <see cref="IHostedService.StartAsync"/> — the host awaits that before any
/// module gate opens, so the ingestion/list views see the new shape on first read.
///
/// Idempotent: on a second run (defs file present + every per-char file already v2) we
/// early-return without touching disk.
/// </summary>
public sealed class GandalfSplitMigration : IHostedService
{
    private readonly string _charactersRootDir;
    private readonly ISettingsStore<GandalfDefinitions> _defStore;
    private readonly PerCharacterStore<GandalfProgress> _progressStore;
    private readonly PerCharacterView<GandalfProgress> _progressView;
    private readonly IDiagnosticsSink? _diag;

    public GandalfSplitMigration(
        PerCharacterStoreOptions options,
        ISettingsStore<GandalfDefinitions> defStore,
        PerCharacterStore<GandalfProgress> progressStore,
        PerCharacterView<GandalfProgress> progressView,
        IDiagnosticsSink? diag = null)
    {
        _charactersRootDir = options.CharactersRootDir;
        _defStore = defStore;
        _progressStore = progressStore;
        _progressView = progressView;
        _diag = diag;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        try { Run(); }
        catch (Exception ex) { _diag?.Warn("Gandalf.SplitMigration", $"Failed: {ex.Message}"); }
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    private void Run()
    {
        if (!Directory.Exists(_charactersRootDir)) return;

        var candidates = CollectCandidates();
        if (candidates.Count == 0) return;

        var allV2 = candidates.All(c => c.IsV2);
        var defsExist = _defStore.Load().Timers.Count > 0;
        if (allV2 && defsExist)
        {
            // Nothing to do — the split already landed on a prior run.
            return;
        }

        // Step 1: union every v1 blob's timers into a global GandalfDefinitions.
        var defs = _defStore.Load();
        var byId = defs.Timers.ToDictionary(d => d.Id, StringComparer.Ordinal);
        foreach (var candidate in candidates.Where(c => !c.IsV2).OrderByDescending(c => c.LastWriteUtc))
        {
            if (candidate.Legacy is null) continue;
            foreach (var timer in candidate.Legacy.Timers)
            {
                if (!byId.TryAdd(timer.Id, LegacyToDef(timer)))
                {
                    // Earlier iteration already owns this id (newer mtime wins). Log if names
                    // or durations disagree so the user can tidy up later.
                    var existing = byId[timer.Id];
                    if (!string.Equals(existing.Name, timer.Name, StringComparison.Ordinal) ||
                        existing.Duration != timer.Duration)
                    {
                        _diag?.Info("Gandalf.SplitMigration",
                            $"Timer id {timer.Id} diverged across characters; kept newer '{existing.Name}' ({existing.Duration}), discarded '{timer.Name}' ({timer.Duration}).");
                    }
                }
            }
        }

        defs.Timers.Clear();
        foreach (var def in byId.Values) defs.Timers.Add(def);
        _defStore.Save(defs);

        // Step 2: rewrite each candidate's gandalf.json as GandalfProgress v2.
        foreach (var candidate in candidates.Where(c => !c.IsV2))
        {
            if (candidate.Legacy is null) continue;
            var progress = new GandalfProgress();
            foreach (var timer in candidate.Legacy.Timers)
            {
                if (timer.StartedAt is null && timer.CompletedAt is null) continue;
                progress.ByTimerId[timer.Id] = new TimerProgress
                {
                    StartedAt = timer.StartedAt,
                    CompletedAt = timer.CompletedAt,
                };
            }
            _progressStore.Save(candidate.Character, candidate.Server, progress);
            _diag?.Info("Gandalf.SplitMigration",
                $"Rewrote {candidate.Character}/{candidate.Server} as v2 progress ({progress.ByTimerId.Count} entries).");
        }

        // Step 3: invalidate any already-cached progress so live readers pick up the new shape.
        _progressView.Invalidate();

        _diag?.Info("Gandalf.SplitMigration",
            $"Split complete: {defs.Timers.Count} definitions across {candidates.Count(c => !c.IsV2)} migrated characters.");
    }

    private List<Candidate> CollectCandidates()
    {
        var list = new List<Candidate>();
        foreach (var charDir in Directory.EnumerateDirectories(_charactersRootDir))
        {
            var path = Path.Combine(charDir, "gandalf.json");
            if (!File.Exists(path)) continue;

            var (character, server) = ParseSlug(Path.GetFileName(charDir));
            if (string.IsNullOrEmpty(character) || string.IsNullOrEmpty(server)) continue;

            var (isV2, legacy) = PeekShape(path);
            list.Add(new Candidate(
                Character: character,
                Server: server,
                Path: path,
                IsV2: isV2,
                Legacy: legacy,
                LastWriteUtc: File.GetLastWriteTimeUtc(path)));
        }
        return list;
    }

    private static (string Character, string Server) ParseSlug(string slug)
    {
        // Slug format: "{character}_{server}". Both are sanitized with invalid-filename-char
        // replacement, but the underscore separator is preserved. First underscore splits.
        var idx = slug.LastIndexOf('_');
        if (idx <= 0 || idx >= slug.Length - 1) return ("", "");
        return (slug[..idx], slug[(idx + 1)..]);
    }

    private (bool IsV2, LegacyGandalfStateV1? Legacy) PeekShape(string path)
    {
        try
        {
            using var stream = File.OpenRead(path);
            using var doc = JsonDocument.Parse(stream);
            var root = doc.RootElement;
            // v2 has "byTimerId" at the root; v1 has "timers".
            if (root.TryGetProperty("byTimerId", out _)) return (true, null);
            if (root.TryGetProperty("schemaVersion", out var v) &&
                v.ValueKind == JsonValueKind.Number && v.GetInt32() >= 2) return (true, null);
        }
        catch { /* fall through to legacy read */ }

        try
        {
            using var stream = File.OpenRead(path);
            var legacy = JsonSerializer.Deserialize(stream, LegacyJsonContext.Default.LegacyGandalfStateV1);
            return (false, legacy);
        }
        catch (Exception ex)
        {
            _diag?.Warn("Gandalf.SplitMigration", $"Legacy read failed for {path}: {ex.Message}");
            return (false, null);
        }
    }

    private static GandalfTimerDef LegacyToDef(LegacyGandalfTimerV1 legacy) => new()
    {
        Id = legacy.Id,
        Name = legacy.Name,
        Duration = legacy.Duration,
        Region = legacy.Region,
        Map = legacy.Map,
    };

    private sealed record Candidate(
        string Character,
        string Server,
        string Path,
        bool IsV2,
        LegacyGandalfStateV1? Legacy,
        DateTime LastWriteUtc);
}

// ── Legacy v1 shape readers (local to the migration so the rest of the module stays clean) ─

internal sealed class LegacyGandalfStateV1
{
    public int SchemaVersion { get; set; }
    public List<LegacyGandalfTimerV1> Timers { get; set; } = [];
}

internal sealed class LegacyGandalfTimerV1
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public TimeSpan Duration { get; set; }
    public string Region { get; set; } = "";
    public string Map { get; set; } = "";
    public DateTimeOffset? StartedAt { get; set; }
    public DateTimeOffset? CompletedAt { get; set; }
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(LegacyGandalfStateV1))]
internal partial class LegacyJsonContext : JsonSerializerContext { }
