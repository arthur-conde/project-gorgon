using System.IO;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace Mithril.MapCalibration.Internal;

/// <summary>
/// Per-user persistence for solved calibrations. Single global file at
/// <c>%LocalAppData%/Mithril/MapCalibration/refinements.json</c> &#8212; not
/// per-character, because the underlying anchors (NPCs + landmarks) don't
/// physically differ per character, so a calibration converged for area X on
/// one character is exactly as valid on another.
///
/// <para>Note: the in-game map pan/zoom that the user calibrated against
/// <em>can</em> differ across characters (different UI preferences). The
/// <see cref="AreaCalibration.CalibrationZoom"/> field captures the zoom; the
/// no-pan assumption is documented in <see cref="AreaCalibration.WorldToWindow(WorldCoord, double)"/>.
/// If a different character runs the game with a different pan, the projection
/// drifts and the user re-runs the walkthrough &#8212; an established Legolas UX
/// concern, not a data-shape concern.</para>
/// </summary>
internal sealed class UserRefinementStore
{
    private readonly string _filePath;
    private readonly ILogger? _logger;
    private readonly object _gate = new();
    private Dictionary<string, AreaCalibration> _refinements = new(StringComparer.Ordinal);

    public UserRefinementStore(string directory, ILogger? logger = null)
    {
        Directory.CreateDirectory(directory);
        _filePath = Path.Combine(directory, "refinements.json");
        _logger = logger;
        Load();
    }

    public IReadOnlyDictionary<string, AreaCalibration> All
    {
        get
        {
            lock (_gate) return new Dictionary<string, AreaCalibration>(_refinements, StringComparer.Ordinal);
        }
    }

    public bool TryGet(string areaKey, out AreaCalibration calibration)
    {
        lock (_gate)
        {
            return _refinements.TryGetValue(areaKey, out calibration!);
        }
    }

    public void Save(string areaKey, AreaCalibration calibration)
    {
        var stamped = calibration with { Source = CalibrationSource.UserRefinement };
        lock (_gate)
        {
            _refinements[areaKey] = stamped;
            Persist();
        }
    }

    public bool Remove(string areaKey)
    {
        lock (_gate)
        {
            if (!_refinements.Remove(areaKey)) return false;
            Persist();
            return true;
        }
    }

    /// <summary>
    /// One-time migration entry point: import each entry only if not already
    /// present. Used to copy <c>LegolasSettings.AreaCalibrations</c> in on
    /// first run. Returns the count actually imported.
    /// </summary>
    public int ImportIfAbsent(IEnumerable<KeyValuePair<string, AreaCalibration>> entries)
    {
        var imported = 0;
        lock (_gate)
        {
            foreach (var (key, cal) in entries)
            {
                if (_refinements.ContainsKey(key)) continue;
                _refinements[key] = cal with { Source = CalibrationSource.UserRefinement };
                imported++;
            }
            if (imported > 0) Persist();
        }
        return imported;
    }

    private void Load()
    {
        if (!File.Exists(_filePath)) return;
        try
        {
            using var stream = File.OpenRead(_filePath);
            var file = JsonSerializer.Deserialize(stream, MapCalibrationJsonContext.Default.UserRefinementFile);
            if (file?.Calibrations is null) return;
            // Stamp Source on every entry; lifted records from older shapes may
            // not carry it explicitly and the default on the record is
            // UserRefinement, which matches this store's contents — but be
            // defensive in case a future shape change reorders defaults.
            _refinements = new Dictionary<string, AreaCalibration>(file.Calibrations.Count, StringComparer.Ordinal);
            foreach (var (key, cal) in file.Calibrations)
            {
                _refinements[key] = cal with { Source = CalibrationSource.UserRefinement };
            }
        }
        catch (Exception ex) when (ex is IOException or JsonException)
        {
            _logger?.LogWarning(ex, "Failed to load user refinement store at {Path} — starting empty.", _filePath);
            _refinements = new Dictionary<string, AreaCalibration>(StringComparer.Ordinal);
        }
    }

    private void Persist()
    {
        try
        {
            var file = new UserRefinementFile(SchemaVersion: 1, Calibrations: _refinements);
            var json = JsonSerializer.Serialize(file, MapCalibrationJsonContext.Default.UserRefinementFile);
            // Atomic-ish write: temp file then move. Defends against a crash
            // mid-write turning the store into garbage.
            var tmp = _filePath + ".tmp";
            File.WriteAllText(tmp, json);
            if (File.Exists(_filePath)) File.Replace(tmp, _filePath, destinationBackupFileName: null);
            else File.Move(tmp, _filePath);
        }
        catch (IOException ex)
        {
            _logger?.LogWarning(ex, "Failed to persist user refinement store to {Path}.", _filePath);
        }
    }
}
