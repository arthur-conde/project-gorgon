using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using Mithril.Shared.Diagnostics;
using Mithril.Shared.Settings;

namespace Mithril.Shared.Character;

/// <summary>
/// File-system backed store for per-character state. The store knows the shape of one
/// JSON file (via <paramref name="fileName"/>), writes it under
/// <c>{rootDir}/{slug(character, server)}/{fileName}</c>, and dispatches loaded state
/// through <typeparamref name="T"/>'s schema-version migration hook.
///
/// Two roles share the same implementation:
/// <list type="bullet">
///   <item><description>
///     Shell-level per-character file (<c>character.json</c>) — e.g. <c>CharacterPresence</c>.
///   </description></item>
///   <item><description>
///     Per-character/per-module file (<c>{moduleId}.json</c>) — e.g. <c>pippin.json</c>.
///   </description></item>
/// </list>
///
/// An optional <see cref="ILegacyMigration{T}"/> lets a module point the store at its
/// old flat-file layout the first time a new character's directory is populated. On a
/// successful legacy migration the store writes the new file and deletes the legacy
/// source + its empty parent directory.
/// </summary>
public sealed class PerCharacterStore<T>
    where T : class, IVersionedState<T>, new()
{
    private readonly string _rootDir;
    private readonly string _fileName;
    private readonly JsonTypeInfo<T> _typeInfo;
    private readonly ILegacyMigration<T>? _legacy;
    private readonly IDiagnosticsSink? _diag;

    public PerCharacterStore(
        string rootDir,
        string fileName,
        JsonTypeInfo<T> typeInfo,
        ILegacyMigration<T>? legacy = null,
        IDiagnosticsSink? diag = null)
    {
        if (string.IsNullOrEmpty(rootDir)) throw new ArgumentException("rootDir required", nameof(rootDir));
        if (string.IsNullOrEmpty(fileName)) throw new ArgumentException("fileName required", nameof(fileName));
        _rootDir = rootDir;
        _fileName = fileName;
        _typeInfo = typeInfo ?? throw new ArgumentNullException(nameof(typeInfo));
        _legacy = legacy;
        _diag = diag;
    }

    /// <summary>Sanitized <c>{character}_{server}</c> used as the directory name.</summary>
    public static string Slug(string character, string server)
    {
        if (string.IsNullOrEmpty(character)) throw new ArgumentException("character required", nameof(character));
        if (string.IsNullOrEmpty(server)) throw new ArgumentException("server required", nameof(server));
        return $"{Sanitize(character)}_{Sanitize(server)}";
    }

    /// <summary>Absolute file path for a given character.</summary>
    public string GetFilePath(string character, string server)
        => Path.Combine(_rootDir, Slug(character, server), _fileName);

    public T Load(string character, string server)
    {
        var path = GetFilePath(character, server);
        if (File.Exists(path))
        {
            var loaded = ReadFromDisk(path);
            return RunMigrate(loaded);
        }

        if (_legacy is not null && _legacy.TryMigrate(character, server, out var migrated, out var legacyPath))
        {
            migrated.SchemaVersion = T.CurrentVersion;
            AtomicJsonWriter.Write(path, migrated, _typeInfo);
            CleanupLegacy(legacyPath);
            _diag?.Info("PerCharacterStore",
                $"Migrated {typeof(T).Name} for {character} ({server}) from {legacyPath} → {path}");
            return migrated;
        }

        var fresh = new T { SchemaVersion = T.CurrentVersion };
        return fresh;
    }

    public async Task<T> LoadAsync(string character, string server, CancellationToken ct = default)
    {
        var path = GetFilePath(character, server);
        if (File.Exists(path))
        {
            var loaded = await ReadFromDiskAsync(path, ct).ConfigureAwait(false);
            return RunMigrate(loaded);
        }

        if (_legacy is not null && _legacy.TryMigrate(character, server, out var migrated, out var legacyPath))
        {
            migrated.SchemaVersion = T.CurrentVersion;
            await AtomicJsonWriter.WriteAsync(path, migrated, _typeInfo, ct).ConfigureAwait(false);
            CleanupLegacy(legacyPath);
            _diag?.Info("PerCharacterStore",
                $"Migrated {typeof(T).Name} for {character} ({server}) from {legacyPath} → {path}");
            return migrated;
        }

        return new T { SchemaVersion = T.CurrentVersion };
    }

    public void Save(string character, string server, T value)
    {
        value.SchemaVersion = T.CurrentVersion;
        AtomicJsonWriter.Write(GetFilePath(character, server), value, _typeInfo);
    }

    public Task SaveAsync(string character, string server, T value, CancellationToken ct = default)
    {
        value.SchemaVersion = T.CurrentVersion;
        return AtomicJsonWriter.WriteAsync(GetFilePath(character, server), value, _typeInfo, ct);
    }

    // ── Private ───────────────────────────────────────────────────────────

    private T ReadFromDisk(string path)
    {
        using var stream = File.OpenRead(path);
        var loaded = JsonSerializer.Deserialize(stream, _typeInfo);
        return loaded ?? new T { SchemaVersion = T.CurrentVersion };
    }

    private async Task<T> ReadFromDiskAsync(string path, CancellationToken ct)
    {
        await using var stream = File.OpenRead(path);
        var loaded = await JsonSerializer.DeserializeAsync(stream, _typeInfo, ct).ConfigureAwait(false);
        return loaded ?? new T { SchemaVersion = T.CurrentVersion };
    }

    private static T RunMigrate(T loaded)
    {
        if (loaded.SchemaVersion == T.CurrentVersion) return loaded;
        var migrated = T.Migrate(loaded);
        migrated.SchemaVersion = T.CurrentVersion;
        return migrated;
    }

    private void CleanupLegacy(string legacyPath)
    {
        if (string.IsNullOrEmpty(legacyPath)) return;
        try
        {
            if (File.Exists(legacyPath)) File.Delete(legacyPath);
            var dir = Path.GetDirectoryName(legacyPath);
            if (!string.IsNullOrEmpty(dir) && Directory.Exists(dir) &&
                !Directory.EnumerateFileSystemEntries(dir).Any())
            {
                Directory.Delete(dir);
            }
        }
        catch (Exception ex)
        {
            _diag?.Warn("PerCharacterStore", $"Legacy cleanup failed for {legacyPath}: {ex.Message}");
        }
    }

    private static string Sanitize(string s)
    {
        var invalid = Path.GetInvalidFileNameChars();
        Span<char> buf = stackalloc char[s.Length];
        for (var i = 0; i < s.Length; i++)
        {
            var c = s[i];
            buf[i] = Array.IndexOf(invalid, c) >= 0 ? '_' : c;
        }
        return new string(buf);
    }
}
