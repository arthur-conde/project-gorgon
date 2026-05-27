using Microsoft.Extensions.Logging;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using Gandalf.Domain;
using Mithril.Shared.Character;
using Microsoft.Extensions.Hosting;

namespace Gandalf.Services;

/// <summary>
/// One-shot startup importer for the pre-#718 per-character <c>quests.json</c>
/// completion-history map. For each character on disk with a non-empty
/// <c>completionHistory</c>, opens that character's <c>gandalf-derived.json</c>
/// directly via <see cref="PerCharacterStore{T}"/> and stamps a
/// <c>quest:&lt;internalName&gt;</c> row under <see cref="QuestSource.Id"/> with
/// the historic completion timestamp.
///
/// <para>The pre-#718 <c>PlayerQuestJournalService</c> owned both current-session
/// active-quest state and cross-session completion history. #718 narrows it to
/// the active half (log-derivable, no persistence) and moves cross-session
/// cooldown anchors to <see cref="DerivedTimerProgressService"/> — already
/// Gandalf's per-character cooldown ledger. This importer bridges any
/// pre-existing history into the new home so timer rows survive the upgrade.</para>
///
/// <para>Writes go through <see cref="PerCharacterStore{T}"/> directly rather
/// than <see cref="DerivedTimerProgressService.Start"/> so we don't have to
/// switch the active character (which would fire side effects across every
/// per-character consumer in the shell). Idempotent: a re-import overwrites
/// the same anchor with the same <c>StartedAt</c>.</para>
///
/// <para>Once imported, the source <c>quests.json</c> is renamed to
/// <c>quests.json.migrated</c> — one release cycle of safety net before a
/// later cleanup pass deletes the trailers. A subsequent startup with no
/// <c>quests.json</c> is the steady state and the importer no-ops.</para>
///
/// <para>Runs during <see cref="IHostedService.StartAsync"/>, before any
/// module gate opens, so the first <see cref="QuestSource"/> catalog build for
/// the active character sees the imported rows. The migration belongs at the
/// destination (Gandalf), not the source (Mithril.GameState) — foundation
/// shouldn't reach into a module's ledger.</para>
/// </summary>
public sealed class QuestCompletionImportService : IHostedService
{
    private const string LegacyFileName = "quests.json";
    private const string RetiredSuffix = ".migrated";

    private readonly string _charactersRootDir;
    private readonly PerCharacterStore<DerivedProgress> _derivedStore;
    private readonly PerCharacterView<DerivedProgress> _derivedView;
    private readonly IActiveCharacterService _active;
    private readonly ILogger? _logger;

    public QuestCompletionImportService(
        PerCharacterStoreOptions options,
        PerCharacterStore<DerivedProgress> derivedStore,
        PerCharacterView<DerivedProgress> derivedView,
        IActiveCharacterService active,
        ILogger? logger = null)
    {
        _charactersRootDir = options.CharactersRootDir;
        _derivedStore = derivedStore;
        _derivedView = derivedView;
        _active = active;
        _logger = logger;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        try { Run(); }
        catch (Exception ex) { _logger?.LogWarning(ex, "Failed"); }
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    private void Run()
    {
        if (!Directory.Exists(_charactersRootDir)) return;

        var activeName = _active.ActiveCharacterName;
        var activeServer = _active.ActiveServer;

        var totalImported = 0;
        var charactersTouched = 0;
        var touchedActiveCharacter = false;

        foreach (var charDir in Directory.EnumerateDirectories(_charactersRootDir))
        {
            var legacyPath = Path.Combine(charDir, LegacyFileName);
            if (!File.Exists(legacyPath)) continue;

            var (character, server) = ParseSlug(Path.GetFileName(charDir));
            if (string.IsNullOrEmpty(character) || string.IsNullOrEmpty(server))
            {
                _logger?.LogWarning($"Could not parse character/server from slug {Path.GetFileName(charDir)}; skipping.");
                continue;
            }

            var legacy = TryRead(legacyPath);
            if (legacy is null) continue;

            if (legacy.CompletionHistory.Count == 0)
            {
                Retire(legacyPath);
                continue;
            }

            // Load (or create) this character's derived-progress file directly.
            // The store handles SchemaVersion + the new-file path; we never
            // touch IActiveCharacterService so the shell's runtime view of the
            // active character is untouched.
            var derived = _derivedStore.Load(character, server);
            if (!derived.BySource.TryGetValue(QuestSource.Id, out var inner))
            {
                inner = new Dictionary<string, DerivedTimerProgress>(StringComparer.Ordinal);
                derived.BySource[QuestSource.Id] = inner;
            }

            var imported = 0;
            foreach (var (internalName, entry) in legacy.CompletionHistory)
            {
                if (string.IsNullOrEmpty(internalName) || entry is null) continue;
                var key = QuestSource.QuestKey(internalName);
                if (inner.TryGetValue(key, out var existing) &&
                    existing.StartedAt == entry.LastCompletedAt)
                {
                    // Already imported on a prior run that didn't rename the
                    // source file (or the user manually restored quests.json).
                    // Re-stamping is harmless but doesn't count toward the
                    // tally.
                    continue;
                }

                inner[key] = new DerivedTimerProgress
                {
                    StartedAt = entry.LastCompletedAt,
                    DismissedAt = null,
                };
                imported++;
            }

            if (imported > 0)
            {
                _derivedStore.Save(character, server, derived);
                totalImported += imported;
                charactersTouched++;
                _logger?.LogInformation($"Imported {imported} quest-completion anchors for {character} ({server}) → DerivedTimerProgressService.");

                if (string.Equals(character, activeName, StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(server, activeServer, StringComparison.OrdinalIgnoreCase))
                {
                    touchedActiveCharacter = true;
                }
            }

            Retire(legacyPath);
        }

        // If we rewrote the active character's file out-of-band (PerCharacterView
        // may have cached an older snapshot via DerivedTimerProgressService's
        // construction-time Current access), invalidate so the running service
        // reloads from disk on the next read. PerCharacterView.Invalidate is
        // documented for exactly this case.
        if (touchedActiveCharacter)
        {
            _derivedView.Invalidate();
        }

        if (charactersTouched > 0)
        {
            _logger?.LogInformation($"Migration complete: {totalImported} anchors across {charactersTouched} character(s); quests.json files retired.");
        }
    }

    private LegacyPlayerQuestJournalState? TryRead(string path)
    {
        try
        {
            using var stream = File.OpenRead(path);
            return JsonSerializer.Deserialize(stream, LegacyQuestJsonContext.Default.LegacyPlayerQuestJournalState);
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Read failed for {Path}", path);
            return null;
        }
    }

    private void Retire(string path)
    {
        try
        {
            var target = path + RetiredSuffix;
            if (File.Exists(target)) File.Delete(target);
            File.Move(path, target);
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Retire failed for {Path}", path);
        }
    }

    private static (string Character, string Server) ParseSlug(string slug)
    {
        // Slug format: "{character}_{server}". Mirrors PerCharacterStore.Slug —
        // the underscore separator is preserved; last underscore splits because
        // PG server names are well-known (no underscore) but character names
        // theoretically can contain them.
        var idx = slug.LastIndexOf('_');
        if (idx <= 0 || idx >= slug.Length - 1) return ("", "");
        return (slug[..idx], slug[(idx + 1)..]);
    }
}

// ── Legacy pre-#718 quests.json shape readers (local so the rest of the module stays clean) ─

/// <summary>Legacy pre-#718 quests.json shape — read-only DTO.</summary>
internal sealed class LegacyPlayerQuestJournalState
{
    [JsonPropertyName("schemaVersion")]
    public int SchemaVersion { get; set; }

    [JsonPropertyName("activeQuests")]
    public Dictionary<string, LegacyQuestJournalEntry> ActiveQuests { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    [JsonPropertyName("completionHistory")]
    public Dictionary<string, LegacyQuestCompletionState> CompletionHistory { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}

internal sealed class LegacyQuestJournalEntry
{
    [JsonPropertyName("internalName")]
    public string InternalName { get; set; } = "";

    [JsonPropertyName("acceptedAt")]
    public DateTimeOffset AcceptedAt { get; set; }
}

internal sealed class LegacyQuestCompletionState
{
    [JsonPropertyName("internalName")]
    public string InternalName { get; set; } = "";

    [JsonPropertyName("lastCompletedAt")]
    public DateTimeOffset LastCompletedAt { get; set; }
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(LegacyPlayerQuestJournalState))]
internal partial class LegacyQuestJsonContext : JsonSerializerContext { }
