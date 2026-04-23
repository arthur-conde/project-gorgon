using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using Gorgon.Shared.Character;
using Pippin.Domain;

namespace Pippin.State;

/// <summary>
/// One-shot migration from the pre-per-character flat layout
/// (<c>%LocalAppData%/Gorgon/Pippin/gourmand-state.json</c>) to the per-character layout
/// (<c>%LocalAppData%/Gorgon/characters/{slug}/pippin.json</c>).
///
/// Only fires for a single character — whichever one is first resolved as active. That's
/// the only defensible assumption: the legacy file was written without any character
/// attribution, so we attribute it to the user's first post-upgrade active character.
/// The migration deletes the legacy file and its empty parent dir after a successful
/// new-path write, so subsequent characters start clean.
/// </summary>
public sealed class GourmandLegacyMigration : ILegacyMigration<GourmandState>
{
    private readonly string _legacyPath;
    private readonly JsonTypeInfo<GourmandState> _typeInfo;

    public GourmandLegacyMigration(string legacyDir, JsonTypeInfo<GourmandState> typeInfo)
    {
        _legacyPath = Path.Combine(legacyDir, "gourmand-state.json");
        _typeInfo = typeInfo;
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
            migrated = loaded;
            return true;
        }
        catch
        {
            return false;
        }
    }
}
