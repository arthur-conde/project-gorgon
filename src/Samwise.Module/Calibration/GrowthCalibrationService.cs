using System.IO;
using System.Text.Json;
using Gorgon.Shared.Diagnostics;
using Samwise.Config;
using Samwise.State;

namespace Samwise.Calibration;

/// <summary>
/// Observes garden phase transitions and records per-crop growth durations.
/// Subscribes to <see cref="GardenStateMachine.PlotChanged"/> to shadow-track
/// per-phase timestamps (which the <see cref="Plot"/> object does not store),
/// and records a <see cref="GrowthObservation"/> when a plot reaches Ripe.
/// </summary>
public sealed class GrowthCalibrationService
{
    private readonly GardenStateMachine _state;
    private readonly ICropConfigStore _config;
    private readonly IDiagnosticsSink? _diag;
    private readonly string _dataPath;

    /// <summary>
    /// In-flight phase tracking for plots observed live from Planted.
    /// Key: "{charName}|{plotId}". Cleaned up on Harvested or PlotsRemoved.
    /// </summary>
    private readonly Dictionary<string, List<PhaseRecord>> _activeTracking = new(StringComparer.Ordinal);

    private GrowthCalibrationData _data = new();

    public GrowthCalibrationData Data => _data;

    public event EventHandler? DataChanged;

    public GrowthCalibrationService(
        GardenStateMachine state,
        ICropConfigStore config,
        string dataDir,
        IDiagnosticsSink? diag = null)
    {
        _state = state;
        _config = config;
        _diag = diag;
        _dataPath = Path.Combine(dataDir, "growth-calibration.json");

        Load();

        _state.PlotChanged += OnPlotChanged;
        _state.PlotsRemoved += OnPlotsRemoved;
    }

    private static string TrackingKey(string charName, string plotId) => $"{charName}|{plotId}";

    private void OnPlotChanged(object? sender, PlotChangedArgs e)
    {
        var key = TrackingKey(e.Plot.CharName, e.Plot.PlotId);

        // Only start tracking for fresh Planted events (OldStage null = first time seeing this plot).
        // Hydrated plots fire PlotChanged with OldStage=null but NewStage != Planted; skip those.
        if (e.OldStage is null && e.NewStage == PlotStage.Planted)
        {
            _activeTracking[key] = [new PhaseRecord { Stage = PlotStage.Planted, EnteredAt = e.Plot.PlantedAt }];
            return;
        }

        // Not tracking this plot — skip.
        if (!_activeTracking.TryGetValue(key, out var phases))
            return;

        // Same stage re-fired (e.g. crop type resolved) — ignore.
        if (e.OldStage == e.NewStage)
            return;

        // Close the previous phase.
        var now = e.Plot.UpdatedAt;
        var last = phases[^1];
        last.DurationSeconds = (now - last.EnteredAt).TotalSeconds;

        if (e.NewStage == PlotStage.Ripe)
        {
            // Record the Ripe phase entry (with 0 duration — it's the terminal state).
            phases.Add(new PhaseRecord { Stage = PlotStage.Ripe, EnteredAt = now, DurationSeconds = 0 });
            RecordObservation(e.Plot, phases);
            _activeTracking.Remove(key);
            return;
        }

        if (e.NewStage == PlotStage.Harvested)
        {
            // Harvested without us seeing Ripe (e.g. Tier-1 immediate harvest on ripe plot
            // where UpdateDescription + StartInteraction fire in the same batch).
            // Still try to record if we have enough data.
            if (e.OldStage == PlotStage.Ripe || phases.Any(p => p.Stage == PlotStage.Ripe))
            {
                // Already recorded at Ripe — just clean up.
            }
            _activeTracking.Remove(key);
            return;
        }

        // Append the new phase.
        phases.Add(new PhaseRecord { Stage = e.NewStage, EnteredAt = now });
    }

    private void OnPlotsRemoved(object? sender, EventArgs e)
    {
        // Clean up tracking entries for plots that no longer exist.
        var snapshot = _state.Snapshot();
        var stale = new List<string>();
        foreach (var key in _activeTracking.Keys)
        {
            var parts = key.Split('|', 2);
            if (parts.Length != 2) { stale.Add(key); continue; }
            var (charName, plotId) = (parts[0], parts[1]);
            if (!snapshot.TryGetValue(charName, out var plots) || !plots.ContainsKey(plotId))
                stale.Add(key);
        }
        foreach (var key in stale)
            _activeTracking.Remove(key);
    }

    private void RecordObservation(Plot plot, List<PhaseRecord> phases)
    {
        if (string.IsNullOrEmpty(plot.CropType))
        {
            _diag?.Trace("Samwise.Calibration", $"Plot {plot.PlotId} has no crop type — skipping");
            return;
        }

        var ripeAt = plot.UpdatedAt;
        var totalPausedSeconds = phases
            .Where(p => p.Stage is PlotStage.Thirsty or PlotStage.NeedsFertilizer)
            .Sum(p => p.DurationSeconds);
        var wallSeconds = (ripeAt - plot.PlantedAt).TotalSeconds;
        var effectiveSeconds = wallSeconds - totalPausedSeconds;

        if (effectiveSeconds <= 0 || effectiveSeconds > 86_400)
        {
            _diag?.Trace("Samwise.Calibration",
                $"Plot {plot.PlotId} effective={effectiveSeconds:F1}s — out of range, skipping");
            return;
        }

        var observation = new GrowthObservation
        {
            CropType = plot.CropType,
            CharName = plot.CharName,
            PlantedAt = plot.PlantedAt,
            RipeAt = ripeAt,
            EffectiveSeconds = effectiveSeconds,
            TotalPausedSeconds = totalPausedSeconds,
            Phases = phases.ToList(),
            Timestamp = ripeAt,
        };

        _data.Observations.Add(observation);
        RecomputeRates();
        Save();

        _diag?.Info("Samwise.Calibration",
            $"Growth observed: {plot.CropType} effective={effectiveSeconds:F1}s paused={totalPausedSeconds:F1}s ({phases.Count} phases)");

        DataChanged?.Invoke(this, EventArgs.Empty);
    }

    // ── Rate computation ────────────────────────────────────────────

    private void RecomputeRates()
    {
        var rates = new Dictionary<string, CropGrowthRate>(StringComparer.Ordinal);
        foreach (var group in _data.Observations.GroupBy(o => o.CropType, StringComparer.OrdinalIgnoreCase))
        {
            var effs = group.Select(o => o.EffectiveSeconds).ToList();
            var avg = effs.Average();
            var configSeconds = _config.Current.Crops.TryGetValue(group.Key, out var def) ? def.GrowthSeconds : null;
            double? deltaPct = configSeconds is int cfg && avg > 0
                ? (cfg - avg) / avg * 100.0
                : null;

            rates[group.Key] = new CropGrowthRate
            {
                CropType = group.Key,
                AvgSeconds = avg,
                SampleCount = effs.Count,
                MinSeconds = effs.Min(),
                MaxSeconds = effs.Max(),
                ConfigSeconds = configSeconds,
                DeltaPercent = deltaPct,
            };
        }
        _data.Rates = rates;
    }

    /// <summary>Get the calibrated growth rate for a crop, or null if not yet observed.</summary>
    public CropGrowthRate? GetRate(string cropType) =>
        _data.Rates.TryGetValue(cropType, out var r) ? r : null;

    /// <summary>Get the calibrated average growth seconds for a crop, falling back to null.</summary>
    public double? GetCalibratedGrowthSeconds(string cropType) =>
        _data.Rates.TryGetValue(cropType, out var r) ? r.AvgSeconds : null;

    // ── Persistence ─────────────────────────────────────────────────

    private void Load()
    {
        if (!File.Exists(_dataPath)) return;
        try
        {
            var json = File.ReadAllBytes(_dataPath);
            _data = JsonSerializer.Deserialize(json, GrowthCalibrationJsonContext.Default.GrowthCalibrationData) ?? new();
            RecomputeRates();
            _diag?.Info("Samwise.Calibration",
                $"Loaded {_data.Observations.Count} observations, {_data.Rates.Count} crop rates");
        }
        catch (Exception ex)
        {
            _diag?.Warn("Samwise.Calibration", $"Failed to load calibration: {ex.Message}");
            _data = new();
        }
    }

    private void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_dataPath)!);
            var tmp = _dataPath + ".tmp";
            using (var stream = File.Create(tmp))
                JsonSerializer.Serialize(stream, _data, GrowthCalibrationJsonContext.Default.GrowthCalibrationData);
            File.Move(tmp, _dataPath, overwrite: true);
        }
        catch (Exception ex)
        {
            _diag?.Warn("Samwise.Calibration", $"Failed to save calibration: {ex.Message}");
        }
    }

    // ── Export / Import ─────────────────────────────────────────────

    public string ExportJson(string? contributorNote = null)
    {
        var export = new GrowthCalibrationData
        {
            Version = 1,
            ContributorNote = contributorNote,
            ExportedAt = DateTimeOffset.UtcNow,
            Observations = _data.Observations,
            Rates = _data.Rates,
        };
        return JsonSerializer.Serialize(export, GrowthCalibrationJsonContext.Default.GrowthCalibrationData);
    }

    public void ExportToFile(string path, string? contributorNote = null)
    {
        var json = ExportJson(contributorNote);
        File.WriteAllText(path, json);
        _diag?.Info("Samwise.Calibration", $"Exported {_data.Observations.Count} observations to {path}");
    }

    public int ImportJson(string json, bool replaceExisting = false)
    {
        var imported = JsonSerializer.Deserialize(json, GrowthCalibrationJsonContext.Default.GrowthCalibrationData);
        if (imported is null) return 0;
        return MergeData(imported, replaceExisting);
    }

    public int ImportFromFile(string path, bool replaceExisting = false)
    {
        var json = File.ReadAllText(path);
        return ImportJson(json, replaceExisting);
    }

    private int MergeData(GrowthCalibrationData incoming, bool replaceExisting)
    {
        if (replaceExisting)
        {
            var count = incoming.Observations.Count;
            _data = incoming;
            RecomputeRates();
            Save();
            DataChanged?.Invoke(this, EventArgs.Empty);
            return count;
        }

        var existingKeys = new HashSet<string>(_data.Observations.Select(ObservationKey));
        var added = 0;
        foreach (var obs in incoming.Observations)
        {
            if (existingKeys.Add(ObservationKey(obs)))
            {
                _data.Observations.Add(obs);
                added++;
            }
        }

        if (added > 0)
        {
            RecomputeRates();
            Save();
            DataChanged?.Invoke(this, EventArgs.Empty);
        }

        _diag?.Info("Samwise.Calibration",
            $"Imported {added} new observations ({incoming.Observations.Count - added} duplicates skipped)");
        return added;
    }

    private static string ObservationKey(GrowthObservation o) =>
        $"{o.CropType}|{o.CharName}|{o.EffectiveSeconds:F1}|{o.Timestamp:O}";

    // ── Test support ────────────────────────────────────────────────

    /// <summary>Number of plots currently being phase-tracked.</summary>
    public int ActiveTrackingCount => _activeTracking.Count;
}
