using System.IO;
using System.Text.Json;
using Gorgon.Shared.Diagnostics;
using Gorgon.Shared.Reference;
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
    private readonly ICommunityCalibrationService? _community;
    private readonly CalibrationSettings? _calibrationSettings;
    private readonly IDiagnosticsSink? _diag;
    private readonly string _dataPath;

    /// <summary>
    /// In-flight phase tracking for plots observed live from Planted.
    /// Key: "{charName}|{plotId}". Cleaned up on Harvested or PlotsRemoved.
    /// </summary>
    private readonly Dictionary<string, List<PhaseRecord>> _activeTracking = new(StringComparer.Ordinal);

    private GrowthCalibrationData _data = new();

    // Resolved (local ⊕ community) lookup tables. Rebuilt on every RecomputeRates() and on
    // community FileUpdated / CalibrationSettings.Source change. Persistence still uses _data.*.
    private IReadOnlyDictionary<string, CropGrowthRate> _resolvedRates = new Dictionary<string, CropGrowthRate>(StringComparer.Ordinal);
    private IReadOnlyDictionary<string, PhaseTransitionRate> _resolvedPhaseRates = new Dictionary<string, PhaseTransitionRate>(StringComparer.Ordinal);
    private IReadOnlyDictionary<string, SlotCapRate> _resolvedSlotCapRates = new Dictionary<string, SlotCapRate>(StringComparer.Ordinal);

    public GrowthCalibrationData Data => _data;

    /// <summary>Effective per-crop rates: local observations blended/overridden per the configured <see cref="CalibrationSource"/>.</summary>
    public IReadOnlyDictionary<string, CropGrowthRate> EffectiveRates => _resolvedRates;
    public IReadOnlyDictionary<string, PhaseTransitionRate> EffectivePhaseRates => _resolvedPhaseRates;
    public IReadOnlyDictionary<string, SlotCapRate> EffectiveSlotCapRates => _resolvedSlotCapRates;

    public event EventHandler? DataChanged;

    public GrowthCalibrationService(
        GardenStateMachine state,
        ICropConfigStore config,
        string dataDir,
        ICommunityCalibrationService? community = null,
        CalibrationSettings? calibrationSettings = null,
        IDiagnosticsSink? diag = null)
    {
        _state = state;
        _config = config;
        _community = community;
        _calibrationSettings = calibrationSettings;
        _diag = diag;
        _dataPath = Path.Combine(dataDir, "growth-calibration.json");

        Load();

        _state.PlotChanged += OnPlotChanged;
        _state.PlotsRemoved += OnPlotsRemoved;
        _state.SlotCapObserved += OnSlotCapObserved;

        if (_community is not null)
            _community.FileUpdated += OnCommunityFileUpdated;
        if (_calibrationSettings is not null)
            _calibrationSettings.PropertyChanged += OnCalibrationSettingsChanged;
    }

    private void OnCommunityFileUpdated(object? sender, string key)
    {
        if (key != "samwise") return;
        RebuildResolvedTables();
        DataChanged?.Invoke(this, EventArgs.Empty);
    }

    private void OnCalibrationSettingsChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(CalibrationSettings.Source)) return;
        RebuildResolvedTables();
        DataChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// Dedup window for slot-cap errors. The game emits the same error
    /// repeatedly when the player spam-clicks a seed against a full family.
    /// Each burst counts as one observation.
    /// </summary>
    private static readonly TimeSpan SlotCapDedupWindow = TimeSpan.FromSeconds(2);

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

        // Emit a per-phase observation for the just-closed phase. Even if the
        // plot never reaches Ripe, partial-cycle phase data is useful.
        RecordPhaseTransition(e.Plot, last.Stage, e.NewStage, last.DurationSeconds, now);

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
            _activeTracking.Remove(key);
            return;
        }

        // Append the new phase.
        phases.Add(new PhaseRecord { Stage = e.NewStage, EnteredAt = now });
    }

    private void RecordPhaseTransition(Plot plot, PlotStage from, PlotStage to, double durationSeconds, DateTimeOffset at)
    {
        if (string.IsNullOrEmpty(plot.CropType)) return;
        // Drop sub-second durations as noise — log timestamps are second-precision,
        // so anything under 1s is either a genuinely rapid transition (not worth
        // measuring) or collapsed timestamps from backfill. Same for absurd upper bounds.
        if (durationSeconds < 1 || durationSeconds > 86_400) return;

        _data.PhaseTransitions.Add(new PhaseTransitionObservation
        {
            CropType = plot.CropType,
            CharName = plot.CharName,
            FromStage = from,
            ToStage = to,
            DurationSeconds = durationSeconds,
            Timestamp = at,
        });
        RecomputePhaseRates();
        Save();
        DataChanged?.Invoke(this, EventArgs.Empty);
    }

    private void OnSlotCapObserved(object? sender, SlotCapObservedArgs e)
    {
        // Dedup: the game emits the same cap error multiple times when a
        // player spam-clicks a seed. Collapse identical (family, cap) events
        // within SlotCapDedupWindow into one observation.
        var cutoff = e.Timestamp - SlotCapDedupWindow;
        var recent = _data.SlotCapObservations
            .Where(o => o.Family == e.Family && o.ObservedCap == e.ObservedCap
                        && o.CharName == e.CharName && o.Timestamp >= cutoff)
            .Any();
        if (recent) return;

        _data.SlotCapObservations.Add(new SlotCapObservation
        {
            CharName = e.CharName,
            Family = e.Family,
            ObservedCap = e.ObservedCap,
            Timestamp = e.Timestamp,
        });
        RecomputeSlotCapRates();
        Save();

        _diag?.Info("Samwise.Calibration",
            $"Slot cap observed: family={e.Family} cap={e.ObservedCap} char={e.CharName}");

        DataChanged?.Invoke(this, EventArgs.Empty);
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

        // Minimum 10s: the fastest real crop (Potato/Onion) is ~50s, so anything
        // under 10s is backfill-induced collapse or noise.
        if (effectiveSeconds < 10 || effectiveSeconds > 86_400)
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

        RecomputePhaseRates();
        RecomputeSlotCapRates();
        RebuildResolvedTables();
    }

    /// <summary>
    /// Rebuild the effective (local ⊕ community) lookup tables per the configured merge mode.
    /// Called after <see cref="RecomputeRates"/>, on community <c>FileUpdated</c>, and on
    /// <see cref="CalibrationSettings.Source"/> changes.
    /// </summary>
    private void RebuildResolvedTables()
    {
        var mode = _calibrationSettings?.Source ?? CalibrationSource.PreferLocal;
        var community = _community?.SamwiseRates;

        // Crop rates
        var resolvedRates = new Dictionary<string, CropGrowthRate>(StringComparer.Ordinal);
        var cropKeys = new HashSet<string>(_data.Rates.Keys, StringComparer.Ordinal);
        if (community is not null) foreach (var k in community.Rates.Keys) cropKeys.Add(k);
        foreach (var k in cropKeys)
        {
            _data.Rates.TryGetValue(k, out var local);
            community?.Rates.TryGetValue(k, out var wire);
            var wirePayload = community is not null && community.Rates.TryGetValue(k, out var w) ? w : null;
            var merged = CommunityRatesMerger.ResolveCropRate(local, wirePayload, mode);
            if (merged is not null) { merged.CropType = k; resolvedRates[k] = merged; }
        }
        _resolvedRates = resolvedRates;

        // Phase rates
        var resolvedPhaseRates = new Dictionary<string, PhaseTransitionRate>(StringComparer.Ordinal);
        var phaseKeys = new HashSet<string>(_data.PhaseRates.Keys, StringComparer.Ordinal);
        if (community is not null) foreach (var k in community.PhaseRates.Keys) phaseKeys.Add(k);
        foreach (var k in phaseKeys)
        {
            _data.PhaseRates.TryGetValue(k, out var local);
            var wirePayload = community is not null && community.PhaseRates.TryGetValue(k, out var w) ? w : null;
            var merged = CommunityRatesMerger.ResolvePhaseRate(local, wirePayload, mode);
            if (merged is not null) resolvedPhaseRates[k] = merged;
        }
        _resolvedPhaseRates = resolvedPhaseRates;

        // Slot caps
        var resolvedSlotCaps = new Dictionary<string, SlotCapRate>(StringComparer.Ordinal);
        var slotKeys = new HashSet<string>(_data.SlotCapRates.Keys, StringComparer.Ordinal);
        if (community is not null) foreach (var k in community.SlotCapRates.Keys) slotKeys.Add(k);
        foreach (var k in slotKeys)
        {
            _data.SlotCapRates.TryGetValue(k, out var local);
            var wirePayload = community is not null && community.SlotCapRates.TryGetValue(k, out var w) ? w : null;
            var merged = CommunityRatesMerger.ResolveSlotCap(local, wirePayload, mode);
            if (merged is not null) { merged.Family = k; resolvedSlotCaps[k] = merged; }
        }
        _resolvedSlotCapRates = resolvedSlotCaps;
    }

    private void RecomputePhaseRates()
    {
        var rates = new Dictionary<string, PhaseTransitionRate>(StringComparer.Ordinal);
        // Exclude player-reaction transitions from rate aggregation. Raw
        // observations are still stored for transparency.
        foreach (var group in _data.PhaseTransitions
                     .Where(IsGrowthTransition)
                     .GroupBy(o => PhaseRateKey(o.CropType, o.FromStage, o.ToStage)))
        {
            var durs = group.Select(o => o.DurationSeconds).ToList();
            var first = group.First();
            rates[group.Key] = new PhaseTransitionRate
            {
                CropType = first.CropType,
                FromStage = first.FromStage,
                ToStage = first.ToStage,
                AvgSeconds = durs.Average(),
                SampleCount = durs.Count,
                MinSeconds = durs.Min(),
                MaxSeconds = durs.Max(),
            };
        }
        _data.PhaseRates = rates;
    }

    private void RecomputeSlotCapRates()
    {
        var rates = new Dictionary<string, SlotCapRate>(StringComparer.Ordinal);
        foreach (var group in _data.SlotCapObservations.GroupBy(o => o.Family, StringComparer.Ordinal))
        {
            var caps = group.Select(o => o.ObservedCap).ToList();
            var configMax = _config.Current.SlotFamilies.TryGetValue(group.Key, out var fam)
                ? (int?)fam.Max : null;
            rates[group.Key] = new SlotCapRate
            {
                Family = group.Key,
                ObservedMax = caps.Max(),
                SampleCount = caps.Count,
                ConfigMax = configMax,
            };
        }
        _data.SlotCapRates = rates;
    }

    /// <summary>
    /// True for transitions that measure actual crop growth time. Excludes
    /// Thirsty→Growing and NeedsFertilizer→Growing — those measure player
    /// reaction time (how long before they watered/fertilized).
    /// </summary>
    private static bool IsGrowthTransition(PhaseTransitionObservation o) => o.FromStage switch
    {
        PlotStage.Thirsty or PlotStage.NeedsFertilizer => false,
        _ => true,
    };

    private static string PhaseRateKey(string cropType, PlotStage from, PlotStage to)
        => $"{cropType}|{from}→{to}";

    /// <summary>Get the effective (local ⊕ community) rate for a crop, or null if not yet observed.</summary>
    public CropGrowthRate? GetRate(string cropType) =>
        _resolvedRates.TryGetValue(cropType, out var r) ? r : null;

    /// <summary>Get the effective average growth seconds for a crop, falling back to null.</summary>
    public double? GetCalibratedGrowthSeconds(string cropType) =>
        _resolvedRates.TryGetValue(cropType, out var r) ? r.AvgSeconds : null;

    /// <summary>Get the effective max plot count for a slot family, or null if not yet observed.</summary>
    public int? GetCalibratedSlotMax(string family) =>
        _resolvedSlotCapRates.TryGetValue(family, out var r) ? r.ObservedMax : null;

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
            PhaseTransitions = _data.PhaseTransitions,
            PhaseRates = _data.PhaseRates,
            SlotCapObservations = _data.SlotCapObservations,
            SlotCapRates = _data.SlotCapRates,
        };
        return JsonSerializer.Serialize(export, GrowthCalibrationJsonContext.Default.GrowthCalibrationData);
    }

    /// <summary>
    /// Sanitized export for community sharing: only aggregated rates + sample counts.
    /// No raw observations (which carry character names and timestamps), no slot-cap observations.
    /// </summary>
    public string ExportCommunityJson(string? contributorNote = null)
    {
        var payload = new GrowthRatesPayload
        {
            SchemaVersion = 1,
            Module = "samwise",
            ExportedAt = DateTimeOffset.UtcNow,
            ContributorNote = contributorNote,
            Rates = _data.Rates.ToDictionary(
                kv => kv.Key,
                kv => new GrowthRatePayload
                {
                    AvgSeconds = kv.Value.AvgSeconds,
                    SampleCount = kv.Value.SampleCount,
                    MinSeconds = kv.Value.MinSeconds,
                    MaxSeconds = kv.Value.MaxSeconds,
                },
                StringComparer.Ordinal),
            PhaseRates = _data.PhaseRates.ToDictionary(
                kv => kv.Key,
                kv => new GrowthRatePayload
                {
                    AvgSeconds = kv.Value.AvgSeconds,
                    SampleCount = kv.Value.SampleCount,
                    MinSeconds = kv.Value.MinSeconds,
                    MaxSeconds = kv.Value.MaxSeconds,
                },
                StringComparer.Ordinal),
            SlotCapRates = _data.SlotCapRates.ToDictionary(
                kv => kv.Key,
                kv => new SlotCapRatePayload
                {
                    ObservedMax = kv.Value.ObservedMax,
                    SampleCount = kv.Value.SampleCount,
                },
                StringComparer.Ordinal),
        };
        return JsonSerializer.Serialize(payload, CommunityCalibrationJsonContext.Default.GrowthRatesPayload);
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

        var existingObsKeys = new HashSet<string>(_data.Observations.Select(ObservationKey));
        var existingPhaseKeys = new HashSet<string>(_data.PhaseTransitions.Select(PhaseTransitionKey));
        var existingSlotCapKeys = new HashSet<string>(_data.SlotCapObservations.Select(SlotCapKey));
        var added = 0;

        foreach (var obs in incoming.Observations)
            if (existingObsKeys.Add(ObservationKey(obs))) { _data.Observations.Add(obs); added++; }
        foreach (var pt in incoming.PhaseTransitions)
            if (existingPhaseKeys.Add(PhaseTransitionKey(pt))) { _data.PhaseTransitions.Add(pt); added++; }
        foreach (var sc in incoming.SlotCapObservations)
            if (existingSlotCapKeys.Add(SlotCapKey(sc))) { _data.SlotCapObservations.Add(sc); added++; }

        if (added > 0)
        {
            RecomputeRates();
            Save();
            DataChanged?.Invoke(this, EventArgs.Empty);
        }

        var incomingTotal = incoming.Observations.Count + incoming.PhaseTransitions.Count + incoming.SlotCapObservations.Count;
        _diag?.Info("Samwise.Calibration",
            $"Imported {added} new observations ({incomingTotal - added} duplicates skipped)");
        return added;
    }

    private static string ObservationKey(GrowthObservation o) =>
        $"{o.CropType}|{o.CharName}|{o.EffectiveSeconds:F1}|{o.Timestamp:O}";

    private static string PhaseTransitionKey(PhaseTransitionObservation o) =>
        $"{o.CropType}|{o.CharName}|{o.FromStage}→{o.ToStage}|{o.DurationSeconds:F1}|{o.Timestamp:O}";

    private static string SlotCapKey(SlotCapObservation o) =>
        $"{o.Family}|{o.CharName}|{o.ObservedCap}|{o.Timestamp:O}";

    // ── Test support ────────────────────────────────────────────────

    /// <summary>Number of plots currently being phase-tracked.</summary>
    public int ActiveTrackingCount => _activeTracking.Count;
}
