using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using Microsoft.Extensions.Logging;
using Mithril.Shared.Character;
using Pippin.Domain;

namespace Pippin.State;

/// <summary>
/// One-shot migration from the pre-per-character flat layout
/// (<c>%LocalAppData%/Mithril/Pippin/gourmand-state.json</c>) to the per-character layout
/// (<c>%LocalAppData%/Mithril/characters/{slug}/pippin.json</c>).
/// </summary>
public sealed class GourmandLegacyMigration : ILegacyMigration<GourmandState>
{
    private readonly string _legacyPath;
    private readonly JsonTypeInfo<GourmandState> _typeInfo;
    private readonly ILogger? _logger;

    public GourmandLegacyMigration(
        string legacyDir,
        JsonTypeInfo<GourmandState> typeInfo,
        ILogger? logger = null)
    {
        _legacyPath = Path.Combine(legacyDir, "gourmand-state.json");
        _typeInfo = typeInfo;
        _logger = logger;
    }

    public bool TryMigrate(string character, string server, out GourmandState migrated, out string legacyPath)
    {
        migrated = new GourmandState();
        legacyPath = _legacyPath;

        if (!File.Exists(_legacyPath)) return false;

        try
        {
            using var stream = File.OpenRead(_legacyPath);
            var loaded = JsonSerializer.Deserialize(stream, _typeInfo);
            if (loaded is null) return false;

            migrated = GourmandState.Migrate(loaded);
            _logger?.LogInformation(
                "Gourmand legacy migration succeeded for {Character}/{Server} from {LegacyPath}",
                character,
                server,
                _legacyPath);
            return true;
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex,
                "Gourmand legacy migration failed for {Character}/{Server} from {LegacyPath}",
                character,
                server,
                _legacyPath);
            return false;
        }
    }
}
