using Samwise.Config;
using Samwise.Parsing;

namespace Samwise.State;

public sealed record PlotChangedArgs(Plot Plot, PlotStage? OldStage, PlotStage NewStage);

/// <summary>
/// Single-threaded state machine. Mirrors the JS parse loop in
/// GorgonHelper.html (lines 2680–2900) — including the four-tier harvest
/// detection, the 5-second appearance-loop freshness window, and the
/// pendingPlantForCrop fallback for cached crop ordering.
/// </summary>
public sealed class GardenStateMachine
{
    private readonly ICropConfigStore _config;
    private readonly TimeProvider _time;

    private readonly Dictionary<string, Dictionary<string, Plot>> _plotsByChar = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _playerOwnedPetIds = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _itemIdToCrop = new(StringComparer.Ordinal);

    private string? _currentChar;
    private string? _pendingHarvestPlotId;
    private string? _lastUpdateItemCropType;
    private string? _lastCropAsset;
    private DateTimeOffset _lastCropAssetTime;
    private bool _lastCropAssetUsed;
    private (string PlotId, string CharName)? _pendingPlantForCrop;
    private bool _sessionActive;

    public GardenStateMachine(ICropConfigStore config, TimeProvider? time = null)
    {
        _config = config;
        _time = time ?? TimeProvider.System;
    }

    public bool SessionActive
    {
        get => _sessionActive;
        set => _sessionActive = value;
    }

    public string? CurrentCharacter => _currentChar;

    public IReadOnlyDictionary<string, IReadOnlyDictionary<string, Plot>> Snapshot()
    {
        var result = new Dictionary<string, IReadOnlyDictionary<string, Plot>>(StringComparer.OrdinalIgnoreCase);
        foreach (var (k, v) in _plotsByChar) result[k] = v;
        return result;
    }

    public event EventHandler<PlotChangedArgs>? PlotChanged;

    public void Apply(GardenEvent evt)
    {
        switch (evt)
        {
            case PlayerLogin pl:
                _currentChar = pl.CharName;
                break;

            case SetPetOwner spo:
                _playerOwnedPetIds.Add(spo.EntityId);
                if (_sessionActive) HandleSetPetOwnerSession(spo.EntityId);
                break;

            case AppearanceLoop al:
                HandleAppearance(al);
                break;

            case UpdateDescription ud when _sessionActive:
                HandleUpdateDescription(ud);
                break;

            case StartInteraction si when _sessionActive:
                HandleStartInteraction(si);
                break;

            case ScreenTextError when _sessionActive:
                _pendingHarvestPlotId = null;
                _lastUpdateItemCropType = null;
                break;

            case AddItem ai when _sessionActive:
                HandleAddItem(ai);
                break;

            case UpdateItemCode uic when _sessionActive:
                if (_itemIdToCrop.TryGetValue(uic.ItemId, out var ct)) _lastUpdateItemCropType = ct;
                break;

            case GardeningXp when _sessionActive:
                HandleGardeningXp();
                break;
        }
    }

    private void HandleSetPetOwnerSession(string plotId)
    {
        if (_currentChar is null) return;
        EnsureCharBucket();
        var plots = _plotsByChar[_currentChar];
        if (plots.ContainsKey(plotId)) return;

        var fresh = _lastCropAsset is not null
                    && !_lastCropAssetUsed
                    && (_time.GetUtcNow() - _lastCropAssetTime) < TimeSpan.FromSeconds(5);
        var rawCrop = fresh ? _lastCropAsset : null;
        var crop = (rawCrop is not null && !IsSlotFull(rawCrop, _currentChar)) ? rawCrop : null;

        var plot = new Plot
        {
            PlotId = plotId,
            CharName = _currentChar,
            CropType = crop,
            Stage = PlotStage.Planted,
            PlantedAt = _time.GetUtcNow(),
            UpdatedAt = _time.GetUtcNow(),
        };
        plots[plotId] = plot;

        _pendingPlantForCrop = crop is null ? (plotId, _currentChar) : null;
        _lastCropAsset = null;
        _lastCropAssetUsed = false;

        RaisePlotChanged(plot, null, PlotStage.Planted);
    }

    private void HandleAppearance(AppearanceLoop al)
    {
        var model = al.ModelName;
        var hasDigit = false;
        foreach (var c in model) if (c >= '0' && c <= '9') { hasDigit = true; break; }
        var alias = _config.Current.ModelAliasToCrop;

        if (hasDigit && !alias.ContainsKey(model))
        {
            // Digit-bearing model with no alias mapping — can't decode crop name
            _lastCropAsset = null;
            _pendingPlantForCrop = null;
            return;
        }

        if (_lastCropAssetUsed)
        {
            // "is done" follow-up for a crop already consumed by a pending fill
            _lastCropAsset = null;
            _lastCropAssetUsed = false;
            return;
        }

        var resolved = alias.TryGetValue(model, out var cropName) ? cropName : model;
        _lastCropAsset = resolved;
        _lastCropAssetTime = _time.GetUtcNow();

        if (_pendingPlantForCrop is { } pending
            && _plotsByChar.TryGetValue(pending.CharName, out var plots)
            && plots.TryGetValue(pending.PlotId, out var plot))
        {
            plot.CropType = resolved;
            plot.UpdatedAt = _time.GetUtcNow();
            _pendingPlantForCrop = null;
            _lastCropAssetUsed = true;
            RaisePlotChanged(plot, plot.Stage, plot.Stage);
        }
    }

    private void HandleUpdateDescription(UpdateDescription ud)
    {
        // SetPetOwner without a UpdateDescription = handled separately below as fresh plant
        // First branch of JS code: SetPetOwner adds to gardenData immediately. We do that on
        // the SetPetOwner event path:
        EnsureCharBucket();
        if (_currentChar is null) return;
        var plots = _plotsByChar[_currentChar];

        var alreadyKnown = plots.ContainsKey(ud.PlotId);
        if (!alreadyKnown && !_playerOwnedPetIds.Contains(ud.PlotId)) return;

        // Strip leading verb: "Water Onion" → "Onion"
        var space = ud.Action.IndexOf(' ');
        var crop = space < 0 ? ud.Action : ud.Action[(space + 1)..];

        var newStage = DetectStage(ud.Action, crop);
        if (!plots.TryGetValue(ud.PlotId, out var plot))
        {
            plot = new Plot
            {
                PlotId = ud.PlotId,
                CharName = _currentChar,
                PlantedAt = _time.GetUtcNow(),
            };
            plots[ud.PlotId] = plot;
        }

        var oldStage = plot.Stage;
        plot.CropType = crop;
        plot.Title = ud.Title;
        plot.Description = ud.Description;
        plot.Action = ud.Action;
        plot.Scale = ud.Scale;
        plot.Stage = newStage;
        plot.UpdatedAt = _time.GetUtcNow();
        RaisePlotChanged(plot, oldStage, newStage);
    }

    private void HandleStartInteraction(StartInteraction si)
    {
        if (_currentChar is null) return;
        if (!_plotsByChar.TryGetValue(_currentChar, out var plots)) return;
        _lastUpdateItemCropType = null;

        if (plots.TryGetValue(si.PlotId, out var plot))
        {
            if (plot.Stage == PlotStage.Ripe)
            {
                MarkHarvested(plot);
                _pendingHarvestPlotId = null;
            }
            else
            {
                _pendingHarvestPlotId = si.PlotId;
            }
        }
        else
        {
            _pendingHarvestPlotId = null;
        }
    }

    private void HandleAddItem(AddItem ai)
    {
        // Tier 2: record itemId → cropType using config's prefix map
        if (_config.Current.ItemPrefixToCrop is { } prefixMap)
        {
            foreach (var (prefix, cropName) in prefixMap)
            {
                if (ai.ItemName.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                {
                    _itemIdToCrop[ai.ItemId] = cropName;
                    break;
                }
            }
        }

        if (_pendingHarvestPlotId is null || _currentChar is null) return;
        if (!_plotsByChar.TryGetValue(_currentChar, out var plots)) return;
        if (!plots.TryGetValue(_pendingHarvestPlotId, out var plot)) return;
        if (plot.CropType is null) return;

        var firstWord = plot.CropType.Split(' ')[0];
        if (ai.ItemName.StartsWith(firstWord, StringComparison.OrdinalIgnoreCase))
        {
            MarkHarvested(plot);
            _pendingHarvestPlotId = null;
            _lastUpdateItemCropType = null;
        }
    }

    private void HandleGardeningXp()
    {
        if (_pendingHarvestPlotId is not null && _currentChar is not null)
        {
            if (_plotsByChar.TryGetValue(_currentChar, out var plots)
                && plots.TryGetValue(_pendingHarvestPlotId, out var plot))
            {
                MarkHarvested(plot);
            }
            _pendingHarvestPlotId = null;
            _lastUpdateItemCropType = null;
            return;
        }
        if (_lastUpdateItemCropType is not null && _currentChar is not null)
        {
            MarkOldestRipeOfType(_currentChar, _lastUpdateItemCropType);
            _lastUpdateItemCropType = null;
        }
    }

    private void MarkOldestRipeOfType(string charName, string cropType)
    {
        if (!_plotsByChar.TryGetValue(charName, out var plots)) return;
        Plot? oldest = null;
        foreach (var p in plots.Values)
        {
            if (p.Stage != PlotStage.Ripe) continue;
            if (!string.Equals(p.CropType, cropType, StringComparison.OrdinalIgnoreCase)) continue;
            if (oldest is null || p.UpdatedAt < oldest.UpdatedAt) oldest = p;
        }
        if (oldest is not null) MarkHarvested(oldest);
    }

    private void MarkHarvested(Plot plot)
    {
        if (plot.Stage == PlotStage.Harvested) return;
        var old = plot.Stage;
        plot.Stage = PlotStage.Harvested;
        plot.UpdatedAt = _time.GetUtcNow();
        RaisePlotChanged(plot, old, PlotStage.Harvested);
    }

    private PlotStage DetectStage(string action, string cropName)
    {
        if (action.StartsWith("Water", StringComparison.OrdinalIgnoreCase)) return PlotStage.Thirsty;
        if (action.StartsWith("Fertilize", StringComparison.OrdinalIgnoreCase)) return PlotStage.NeedsFertilizer;
        if (action.StartsWith("Harvest", StringComparison.OrdinalIgnoreCase)
            || action.StartsWith("Pick", StringComparison.OrdinalIgnoreCase)) return PlotStage.Ripe;
        return PlotStage.Growing;
    }

    private void EnsureCharBucket()
    {
        if (_currentChar is null) return;
        if (!_plotsByChar.ContainsKey(_currentChar))
            _plotsByChar[_currentChar] = new Dictionary<string, Plot>(StringComparer.Ordinal);
    }

    public bool IsSlotFull(string cropType, string charName)
    {
        if (!_config.Current.Crops.TryGetValue(cropType, out var def)) return false;
        if (!_plotsByChar.TryGetValue(charName, out var plots)) return false;
        var count = 0;
        foreach (var p in plots.Values)
        {
            if (p.Stage == PlotStage.Harvested) continue;
            if (p.CropType is null) continue;
            if (_config.Current.Crops.TryGetValue(p.CropType, out var pdef)
                && pdef.SlotFamily == def.SlotFamily) count++;
        }
        if (!_config.Current.SlotFamilies.TryGetValue(def.SlotFamily, out var fam)) return false;
        return count >= fam.Max;
    }

    private void RaisePlotChanged(Plot plot, PlotStage? oldStage, PlotStage newStage)
        => PlotChanged?.Invoke(this, new PlotChangedArgs(plot, oldStage, newStage));

    public void Hydrate(GardenState loaded)
    {
        _plotsByChar.Clear();
        foreach (var (charName, plots) in loaded.PlotsByChar)
        {
            var bucket = new Dictionary<string, Plot>(StringComparer.Ordinal);
            foreach (var (id, pp) in plots)
            {
                var plot = new Plot
                {
                    PlotId = id,
                    CharName = charName,
                    CropType = pp.CropType,
                    Stage = pp.Stage,
                    Title = pp.Title,
                    Description = pp.Description,
                    Action = pp.Action,
                    Scale = pp.Scale,
                    PlantedAt = pp.PlantedAt,
                    UpdatedAt = pp.UpdatedAt,
                };
                bucket[id] = plot;
                RaisePlotChanged(plot, null, plot.Stage); // so the VM can render restored plots
            }
            _plotsByChar[charName] = bucket;
        }
    }

    /// <summary>
    /// Drops plots whose in-game entity has almost certainly been garbage-collected.
    /// TTL is derived from the crop's expected lifetime: growthSeconds × 2 + 10m for
    /// known crops, 5m for unknown. Harvested plots are not auto-pruned — the user
    /// clears those explicitly.
    /// </summary>
    public void PruneWithered()
    {
        var now = _time.GetUtcNow();
        foreach (var plots in _plotsByChar.Values)
        {
            var toRemove = new List<string>();
            foreach (var (id, p) in plots)
            {
                if (p.Stage == PlotStage.Harvested) continue;
                if (now - p.UpdatedAt > ExpectedEntityLifetime(p)) toRemove.Add(id);
            }
            foreach (var id in toRemove) plots.Remove(id);
        }
    }

    /// <summary>
    /// The window after planting during which the game is still likely to hold the
    /// plot entity. Used by both the pruner and AlarmService to decide whether a
    /// state transition reflects a still-interactable entity.
    /// </summary>
    public TimeSpan ExpectedEntityLifetime(Plot plot)
    {
        if (plot.CropType is null) return TimeSpan.FromMinutes(5);
        if (!_config.Current.Crops.TryGetValue(plot.CropType, out var def)) return TimeSpan.FromMinutes(5);
        if (def.GrowthSeconds is not int s || s <= 0) return TimeSpan.FromMinutes(5);
        return TimeSpan.FromSeconds(s * 2) + TimeSpan.FromMinutes(10);
    }

    public bool IsLikelyGarbageCollected(Plot plot)
    {
        var age = _time.GetUtcNow() - plot.PlantedAt;
        return age > ExpectedEntityLifetime(plot);
    }
}
