using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using Mithril.Shared.Character;
using Saruman.Settings;

namespace Saruman.Services;

/// <summary>
/// One-shot migration from the pre-per-character flat file
/// (<c>%LocalAppData%/Mithril/Saruman/settings.json</c>) into
/// <c>characters/{slug}/saruman.json</c>. Attributes the whole legacy codebook to
/// whichever character resolves as active first.
/// </summary>
public sealed class SarumanLegacyMigration : ILegacyMigration<SarumanState>
{
    private readonly string _legacyPath;
    private readonly JsonTypeInfo<SarumanState> _typeInfo;

    public SarumanLegacyMigration(string legacyDir, JsonTypeInfo<SarumanState> typeInfo)
    {
        _legacyPath = Path.Combine(legacyDir, "settings.json");
        _typeInfo = typeInfo;
    }

    public bool TryMigrate(string character, string server, out SarumanState migrated, out string legacyPath)
    {
        migrated = new SarumanState();
        legacyPath = _legacyPath;

        if (!File.Exists(_legacyPath)) return false;

        try
        {
            using var stream = File.OpenRead(_legacyPath);
            var loaded = JsonSerializer.Deserialize(stream, _typeInfo);
            if (loaded is null) return false;
            // The legacy flat file is by definition pre-#603 and carried the old
            // Codebook field that STJ silently drops during deserialization here
            // (the field no longer exists on the type). Surface the same one-time
            // hint as the schema 1→2 in-place migration so the user knows their
            // previously-marked-spent codes are gone and how to recover them.
            loaded.ShowPreSplitMigrationHint = true;
            migrated = loaded;
            return true;
        }
        catch
        {
            return false;
        }
    }
}
