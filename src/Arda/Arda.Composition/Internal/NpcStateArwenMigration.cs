using System.Text.Json;
using Microsoft.Extensions.Logging;
using Mithril.Shared.Character;

namespace Arda.Composition.Internal;

/// <summary>
/// Seeds the <see cref="NpcStateSnapshot"/> from Arwen's legacy <c>arwen.json</c>
/// favor dictionary on first load. Does not delete the source file — Arwen may still
/// reference it during the transition period.
/// </summary>
internal sealed class NpcStateArwenMigration(string charactersRootDir, ILogger? logger = null)
    : ILegacyMigration<NpcStateSnapshot>
{
    public bool TryMigrate(string character, string server, out NpcStateSnapshot migrated, out string legacyPath)
    {
        migrated = new NpcStateSnapshot();
        legacyPath = "";

        var slug = PerCharacterStore<NpcStateSnapshot>.Slug(character, server);
        var arwenPath = Path.Combine(charactersRootDir, slug, "arwen.json");

        if (!File.Exists(arwenPath))
            return false;

        try
        {
            using var stream = File.OpenRead(arwenPath);
            using var doc = JsonDocument.Parse(stream);

            if (!doc.RootElement.TryGetProperty("favor", out var favorElement))
                return false;

            foreach (var prop in favorElement.EnumerateObject())
            {
                var npcKey = prop.Name;
                var entry = prop.Value;

                double? exactFavor = null;
                DateTimeOffset? timestamp = null;

                if (entry.TryGetProperty("exactFavor", out var favorProp))
                    exactFavor = favorProp.GetDouble();

                if (entry.TryGetProperty("timestamp", out var tsProp) &&
                    tsProp.ValueKind == JsonValueKind.String)
                {
                    if (DateTimeOffset.TryParse(tsProp.GetString(), out var parsed))
                        timestamp = parsed;
                }

                if (exactFavor.HasValue)
                {
                    migrated.Npcs[npcKey] = new NpcStateSnapshot.PersistedNpc
                    {
                        AbsoluteFavor = exactFavor.Value,
                        FavorUpdatedAt = timestamp,
                        LastSeenAt = timestamp ?? DateTimeOffset.MinValue
                    };
                }
            }
        }
        catch (JsonException ex)
        {
            logger?.LogWarning(ex,
                "NPC state Arwen migration failed for {Character}/{Server}: invalid JSON at {LegacyPath}",
                character,
                server,
                arwenPath);
            return false;
        }
        catch (IOException ex)
        {
            logger?.LogWarning(ex,
                "NPC state Arwen migration failed for {Character}/{Server}: IO error at {LegacyPath}",
                character,
                server,
                arwenPath);
            return false;
        }

        if (migrated.Npcs.Count > 0)
        {
            logger?.LogInformation(
                "NPC state Arwen migration loaded {NpcCount} NPCs for {Character}/{Server} from {LegacyPath}",
                migrated.Npcs.Count,
                character,
                server,
                arwenPath);
        }

        return migrated.Npcs.Count > 0;
    }
}
