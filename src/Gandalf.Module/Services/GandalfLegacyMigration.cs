using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using Gandalf.Domain;
using Gorgon.Shared.Character;

namespace Gandalf.Services;

/// <summary>
/// One-shot migration from the pre-per-character flat layout
/// (<c>%LocalAppData%/Gorgon/Gandalf/state.json</c>) to
/// <c>%LocalAppData%/Gorgon/characters/{slug}/gandalf.json</c>. Attributes the whole
/// legacy blob to whichever character resolves as active first — the only defensible
/// choice, since the legacy file had no character attribution.
/// </summary>
public sealed class GandalfLegacyMigration : ILegacyMigration<GandalfState>
{
    private readonly string _legacyPath;
    private readonly JsonTypeInfo<GandalfState> _typeInfo;

    public GandalfLegacyMigration(string legacyDir, JsonTypeInfo<GandalfState> typeInfo)
    {
        _legacyPath = Path.Combine(legacyDir, "state.json");
        _typeInfo = typeInfo;
    }

    public bool TryMigrate(string character, string server, out GandalfState migrated, out string legacyPath)
    {
        migrated = new GandalfState();
        legacyPath = _legacyPath;

        if (!File.Exists(_legacyPath)) return false;

        try
        {
            using var stream = File.OpenRead(_legacyPath);
            var loaded = JsonSerializer.Deserialize(stream, _typeInfo);
            if (loaded is null) return false;
            migrated = loaded;
            return true;
        }
        catch
        {
            return false;
        }
    }
}
