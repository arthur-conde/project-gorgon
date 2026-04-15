using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Samwise.Config;

public sealed class LearnedAliasesFile
{
    public Dictionary<string, string> Aliases { get; set; } = new();
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, WriteIndented = true)]
[JsonSerializable(typeof(LearnedAliasesFile))]
public partial class LearnedAliasesJsonContext : JsonSerializerContext { }

/// <summary>
/// Thread-safe persistent map of in-game model name → resolved crop name
/// (e.g. "Flower6" → "Pansy"). Learned entries are merged into
/// <see cref="CropConfig.ModelAliasToCrop"/> so subsequent plantings of the
/// same model resolve at plant-time without waiting for UpdateDescription.
/// </summary>
public sealed class LearnedAliasesStore
{
    private readonly string _path;
    private readonly object _gate = new();
    private Dictionary<string, string> _aliases;

    public LearnedAliasesStore(string path)
    {
        _path = path ?? throw new ArgumentNullException(nameof(path));
        _aliases = Load();
    }

    public event EventHandler? Changed;

    public IReadOnlyDictionary<string, string> Snapshot()
    {
        lock (_gate) return new Dictionary<string, string>(_aliases, StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Records model → crop. Returns true if this is a new or changed mapping,
    /// false if we already had the same pairing (no-op, no save, no event).
    /// </summary>
    public bool Learn(string modelName, string cropName)
    {
        if (string.IsNullOrWhiteSpace(modelName) || string.IsNullOrWhiteSpace(cropName)) return false;
        lock (_gate)
        {
            if (_aliases.TryGetValue(modelName, out var existing)
                && string.Equals(existing, cropName, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }
            _aliases[modelName] = cropName;
            TrySave();
        }
        Changed?.Invoke(this, EventArgs.Empty);
        return true;
    }

    private Dictionary<string, string> Load()
    {
        if (!File.Exists(_path)) return new(StringComparer.OrdinalIgnoreCase);
        try
        {
            using var fs = File.OpenRead(_path);
            var file = JsonSerializer.Deserialize(fs, LearnedAliasesJsonContext.Default.LearnedAliasesFile);
            return new Dictionary<string, string>(
                file?.Aliases ?? new(),
                StringComparer.OrdinalIgnoreCase);
        }
        catch (JsonException) { return new(StringComparer.OrdinalIgnoreCase); }
        catch (IOException) { return new(StringComparer.OrdinalIgnoreCase); }
    }

    private void TrySave()
    {
        try
        {
            var dir = Path.GetDirectoryName(_path);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir)) Directory.CreateDirectory(dir);
            var file = new LearnedAliasesFile
            {
                Aliases = new Dictionary<string, string>(_aliases, StringComparer.OrdinalIgnoreCase),
            };
            var tmp = _path + ".tmp";
            using (var stream = File.Create(tmp))
            {
                JsonSerializer.Serialize(stream, file, LearnedAliasesJsonContext.Default.LearnedAliasesFile);
            }
            File.Move(tmp, _path, overwrite: true);
        }
        catch { /* best-effort; next learn will retry */ }
    }
}
