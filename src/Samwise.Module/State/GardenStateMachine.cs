using Gorgon.Shared.Character;
using Gorgon.Shared.Diagnostics;
using Gorgon.Shared.Reference;
using Samwise.Config;
using Samwise.Parsing;

namespace Samwise.State;

public sealed record PlotChangedArgs(Plot Plot, PlotStage? OldStage, PlotStage NewStage);

public sealed record SlotCapObservedArgs(string CharName, string Family, int ObservedCap, DateTime Timestamp);

/// <summary>
/// Single-threaded state machine. Plant-time crop identification is itemId-driven:
/// each <see cref="SetPetOwner"/> is followed within milliseconds by a
/// <see cref="UpdateItemCode"/> carrying the seed's per-character inventory id,
/// which we map to a crop name via <see cref="ProcessAddItem"/> events seen
/// earlier in the session (resolved through crops.json prefixes and items.json).
/// Harvest detection retains the four-tier waterfall mirrored from the JS in
/// GorgonHelper.html (lines 2820–2900).
/// </summary>
public sealed class GardenStateMachine
{
    private static readonly TimeSpan PlantCropResolveWindow = TimeSpan.FromMilliseconds(500);

    private readonly ICropConfigStore _config;
    private readonly TimeProvider _time;
    private readonly IDiagnosticsSink? _diag;
    private readonly Alarms.SamwiseSettings? _settings;
    private readonly IReferenceDataService? _referenceData;
    private readonly IActiveCharacterService? _activeChar;

    private readonly Dictionary<string, Dictionary<string, Plot>> _plotsByChar = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _playerOwnedPetIds = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _itemIdToCrop = new(StringComparer.Ordinal);

    /// <summary>
    /// Seed item InternalName → crop name, built from items.json. Populated at
    /// construction from every item with a <c>Seed=N</c> / <c>Seedling=N</c> /
    /// <c>Leafling=N</c> / <c>Sprout=N</c> keyword, with the crop name derived
    /// by stripping the suffix from the item's display Name.
    /// </summary>
    private Dictionary<string, string> _seedToCrop = new(StringComparer.Ordinal);

    private string? _pendingHarvestPlotId;
    private string? _lastUpdateItemCropType;
    private (string PlotId, string CharName, DateTimeOffset At)? _pendingPlant;

    /// <summary>Read the active character from the shared service; null-safe for tests.</summary>
    private string? _currentChar => _activeChar?.ActiveCharacterName;

    public GardenStateMachine(
        ICropConfigStore config,
        TimeProvider? time = null,
        IDiagnosticsSink? diag = null,
        Alarms.SamwiseSettings? settings = null,
        IReferenceDataService? referenceData = null,
        IActiveCharacterService? activeChar = null)
    {
        _config = config;
        _time = time ?? TimeProvider.System;
        _diag = diag;
        _settings = settings;
        _referenceData = referenceData;
        _activeChar = activeChar;

        BuildSeedMap();
        if (_referenceData is not null)
            _referenceData.FileUpdated += OnReferenceDataUpdated;
    }

    private void OnReferenceDataUpdated(object? sender, string key)
    {
        if (string.Equals(key, "items", StringComparison.Ordinal))
            BuildSeedMap();
    }

    private static readonly HashSet<string> SeedKeywordTags =
        new(StringComparer.Ordinal) { "Seed", "Seedling", "Leafling", "Sprout" };

    private void BuildSeedMap()
    {
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        if (_referenceData is not null)
        {
            foreach (var (internalName, entry) in _referenceData.ItemsByInternalName)
            {
                foreach (var kw in entry.Keywords)
                {
                    if (!SeedKeywordTags.Contains(kw.Tag)) continue;
                    map[internalName] = TrimSeedSuffix(entry.Name);
                    break;
                }
            }
        }
        _seedToCrop = map;
        _diag?.Trace("Samwise.SeedMap", $"Built seed→crop map with {map.Count} entries");
    }

    public string? CurrentCharacter => _currentChar;

    public IReadOnlyDictionary<string, IReadOnlyDictionary<string, Plot>> Snapshot()
    {
        var result = new Dictionary<string, IReadOnlyDictionary<string, Plot>>(StringComparer.OrdinalIgnoreCase);
        foreach (var (k, v) in _plotsByChar) result[k] = v;
        return result;
    }

    public event EventHandler<PlotChangedArgs>? PlotChanged;
    public event EventHandler? PlotsRemoved;
    public event EventHandler<SlotCapObservedArgs>? SlotCapObserved;

    public void Apply(GardenEvent evt)
    {
        switch (evt)
        {
            case SetPetOwner spo:
                _playerOwnedPetIds.Add(spo.EntityId);
                HandlePlant(spo.EntityId, spo.Timestamp);
                break;

            case AppearanceLoop:
                // No-op. Plant-time identification is now itemId-driven.
                break;

            case UpdateDescription ud:
                HandleUpdateDescription(ud);
                break;

            case StartInteraction si:
                HandleStartInteraction(si);
                break;

            case ScreenTextError:
                _pendingHarvestPlotId = null;
                _lastUpdateItemCropType = null;
                break;

            case AddItem ai:
                HandleAddItem(ai);
                break;

            case UpdateItemCode uic:
                HandleItemIdentified(uic.ItemId, uic.Timestamp);
                break;

            case DeleteItem di:
                HandleItemIdentified(di.ItemId, di.Timestamp);
                break;

            case GardeningXp gxp:
                HandleGardeningXp(gxp.Timestamp);
                break;

            case PlantingCapReached pcr:
                HandlePlantingCap(pcr);
                break;
        }
    }

    private void HandlePlantingCap(PlantingCapReached pcr)
    {
        if (_currentChar is null) return;

        // Resolve seed display name → crop → slot family. The display name (e.g.
        // "Barley Seeds") is the game's user-facing string; map it via items.json
        // to an InternalName, then via the seed map to the crop.
        var crop = ResolveCropFromDisplayName(pcr.SeedDisplayName);
        if (crop is null)
        {
            _diag?.Trace("Samwise.Cap", $"Can't resolve crop from seed '{pcr.SeedDisplayName}'");
            return;
        }
        if (!_config.Current.Crops.TryGetValue(crop, out var def))
        {
            _diag?.Trace("Samwise.Cap", $"Crop '{crop}' not in config — slot family unknown");
            return;
        }
        var family = def.SlotFamily;
        if (string.IsNullOrEmpty(family)) return;

        var count = CountFamilyPlots(_currentChar, family);
        _diag?.Info("Samwise.Cap",
            $"Cap reached: family={family} char={_currentChar} count={count} (trigger={pcr.SeedDisplayName})");

        SlotCapObserved?.Invoke(this, new SlotCapObservedArgs(_currentChar, family, count, pcr.Timestamp));
    }

    /// <summary>
    /// Resolves a seed's display name (e.g. "Barley Seeds") to a crop name by
    /// looking it up in items.json by Name, then consulting the seed map or
    /// falling back to <see cref="TrimSeedSuffix"/>.
    /// </summary>
    private string? ResolveCropFromDisplayName(string displayName)
    {
        if (_referenceData is not null)
        {
            foreach (var (internalName, entry) in _referenceData.ItemsByInternalName)
            {
                if (!string.Equals(entry.Name, displayName, StringComparison.Ordinal)) continue;
                if (_seedToCrop.TryGetValue(internalName, out var crop)) return crop;
                return TrimSeedSuffix(entry.Name);
            }
        }
        // Fallback: try stripping seed suffixes from the display name itself.
        return TrimSeedSuffix(displayName);
    }

    /// <summary>Counts non-harvested plots of the given slot family for a character.</summary>
    public int CountFamilyPlots(string charName, string family)
    {
        if (!_plotsByChar.TryGetValue(charName, out var plots)) return 0;
        var count = 0;
        foreach (var p in plots.Values)
        {
            if (p.Stage == PlotStage.Harvested) continue;
            if (p.CropType is null) continue;
            if (_config.Current.Crops.TryGetValue(p.CropType, out var pdef)
                && string.Equals(pdef.SlotFamily, family, StringComparison.Ordinal))
                count++;
        }
        return count;
    }

    private void HandlePlant(string plotId, DateTime timestamp)
    {
        if (_currentChar is null) return;
        EnsureCharBucket();
        var plots = _plotsByChar[_currentChar];
        if (plots.ContainsKey(plotId)) return;

        var now = new DateTimeOffset(timestamp, TimeSpan.Zero);
        var plot = new Plot
        {
            PlotId = plotId,
            CharName = _currentChar,
            CropType = null,
            Stage = PlotStage.Planted,
            PlantedAt = now,
            UpdatedAt = now,
        };
        plots[plotId] = plot;
        _pendingPlant = (plotId, _currentChar, now);

        _diag?.Info("Samwise.Plant", $"plot={plotId} char={_currentChar} cropGuess=(pending)");
        RaisePlotChanged(plot, null, PlotStage.Planted);
    }

    /// <summary>
    /// Called for both <see cref="UpdateItemCode"/> (stack count decremented) and
    /// <see cref="DeleteItem"/> (last seed consumed). Either is emitted
    /// immediately after the <see cref="SetPetOwner"/> of a fresh plant.
    /// </summary>
    private void HandleItemIdentified(string itemId, DateTime timestamp)
    {
        // Tier-3 harvest hint: remember the crop type the player most recently
        // received an item-code update for, in case GardeningXp later needs it.
        if (_itemIdToCrop.TryGetValue(itemId, out var ct)) _lastUpdateItemCropType = ct;

        if (_pendingPlant is not { } pending) return;
        var now = new DateTimeOffset(timestamp, TimeSpan.Zero);
        if (now - pending.At > PlantCropResolveWindow)
        {
            _pendingPlant = null;
            return;
        }
        if (!_itemIdToCrop.TryGetValue(itemId, out var crop)) return;
        if (!_plotsByChar.TryGetValue(pending.CharName, out var plots)) return;
        if (!plots.TryGetValue(pending.PlotId, out var plot)) return;
        if (plot.CropType is not null) return;
        if (IsSlotFull(crop, pending.CharName)) return;

        plot.CropType = crop;
        plot.UpdatedAt = now;
        _pendingPlant = null;
        _diag?.Info("Samwise.Plant",
            $"resolved plot={pending.PlotId} crop={crop} via itemId={itemId}");
        RaisePlotChanged(plot, plot.Stage, plot.Stage);
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

        var now = new DateTimeOffset(ud.Timestamp, TimeSpan.Zero);
        var newStage = DetectStage(ud.Action, crop);
        if (!plots.TryGetValue(ud.PlotId, out var plot))
        {
            plot = new Plot
            {
                PlotId = ud.PlotId,
                CharName = _currentChar,
                PlantedAt = now,
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
        UpdatePauseTracking(plot, oldStage, newStage, now);
        plot.UpdatedAt = now;

        if (_pendingPlant is { } pending && pending.PlotId == ud.PlotId) _pendingPlant = null;

        RaisePlotChanged(plot, oldStage, newStage);
    }

    private void UpdatePauseTracking(Plot plot, PlotStage oldStage, PlotStage newStage, DateTimeOffset now)
    {
        var wasPaused = IsPausedStage(oldStage);
        var isPaused = IsPausedStage(newStage);
        if (!wasPaused && isPaused)
        {
            plot.PausedSince = now;
        }
        else if (wasPaused && !isPaused && plot.PausedSince is { } since)
        {
            plot.PausedDuration += now - since;
            plot.PausedSince = null;
        }
    }

    private static bool IsPausedStage(PlotStage s) => s is PlotStage.Thirsty or PlotStage.NeedsFertilizer;

    private void HandleStartInteraction(StartInteraction si)
    {
        if (_currentChar is null) return;
        if (!_plotsByChar.TryGetValue(_currentChar, out var plots)) return;
        _lastUpdateItemCropType = null;

        if (plots.TryGetValue(si.PlotId, out var plot))
        {
            if (plot.Stage == PlotStage.Ripe)
            {
                MarkHarvested(plot, si.Timestamp);
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
        // Map per-character itemId → crop name. Drives both the Tier-2 harvest
        // confirmation below and the plant-time resolution in HandleUpdateItemCode.
        var resolved = ResolveCropFromItemName(ai.ItemName);
        if (resolved is not null) _itemIdToCrop[ai.ItemId] = resolved;

        if (_pendingHarvestPlotId is null || _currentChar is null) return;
        if (!_plotsByChar.TryGetValue(_currentChar, out var plots)) return;
        if (!plots.TryGetValue(_pendingHarvestPlotId, out var plot)) return;
        if (plot.CropType is null) return;

        var firstWord = plot.CropType.Split(' ')[0];
        if (ai.ItemName.StartsWith(firstWord, StringComparison.OrdinalIgnoreCase))
        {
            MarkHarvested(plot, ai.Timestamp);
            _pendingHarvestPlotId = null;
            _lastUpdateItemCropType = null;
        }
    }

    private void HandleGardeningXp(DateTime timestamp)
    {
        if (_pendingHarvestPlotId is not null && _currentChar is not null)
        {
            if (_plotsByChar.TryGetValue(_currentChar, out var plots)
                && plots.TryGetValue(_pendingHarvestPlotId, out var plot))
            {
                MarkHarvested(plot, timestamp);
            }
            _pendingHarvestPlotId = null;
            _lastUpdateItemCropType = null;
            return;
        }
        if (_lastUpdateItemCropType is not null && _currentChar is not null)
        {
            MarkOldestRipeOfType(_currentChar, _lastUpdateItemCropType, timestamp);
            _lastUpdateItemCropType = null;
        }
    }

    private void MarkOldestRipeOfType(string charName, string cropType, DateTime timestamp)
    {
        if (!_plotsByChar.TryGetValue(charName, out var plots)) return;
        Plot? oldest = null;
        foreach (var p in plots.Values)
        {
            if (p.Stage != PlotStage.Ripe) continue;
            if (!string.Equals(p.CropType, cropType, StringComparison.OrdinalIgnoreCase)) continue;
            if (oldest is null || p.UpdatedAt < oldest.UpdatedAt) oldest = p;
        }
        if (oldest is not null) MarkHarvested(oldest, timestamp);
    }

    private void MarkHarvested(Plot plot, DateTime timestamp)
    {
        if (plot.Stage == PlotStage.Harvested) return;
        var old = plot.Stage;
        plot.Stage = PlotStage.Harvested;
        plot.UpdatedAt = new DateTimeOffset(timestamp, TimeSpan.Zero);
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

    private string? ResolveCropFromItemName(string itemName)
    {
        // 1) Seed map — O(1) lookup for known seeds built from items.json.
        //    Handles opaque InternalNames like "FlowerSeeds6" → "Pansy" as
        //    well as conventional ones like "BarleySeeds" → "Barley".
        if (_seedToCrop.TryGetValue(itemName, out var seedCrop)) return seedCrop;

        // 2) Items.json fallback — required for harvested-produce events
        //    (e.g. AddItem("Carrot") fires when harvest lands in inventory)
        //    and anything not present in the seed map.
        if (_referenceData is not null
            && _referenceData.ItemsByInternalName.TryGetValue(itemName, out var entry))
        {
            return TrimSeedSuffix(entry.Name);
        }

        // 3) Last-resort prefix match against crops.json. Covers cases where
        //    items.json is unavailable (tests, missing CDN data) or a newly
        //    added crop isn't in the seed keyword set yet.
        foreach (var cropName in _config.Current.Crops.Keys)
        {
            var prefix = cropName.Split(' ')[0];
            if (itemName.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                return cropName;
        }
        return null;
    }

    private static readonly string[] SeedSuffixes = [" Seeds", " Seedling", " Leafling", " Sprout"];

    private static string TrimSeedSuffix(string itemName)
    {
        foreach (var suffix in SeedSuffixes)
        {
            if (itemName.EndsWith(suffix, StringComparison.Ordinal))
                return itemName[..^suffix.Length];
        }
        return itemName;
    }

    public bool IsSlotFull(string cropType, string charName)
    {
        if (!_config.Current.Crops.TryGetValue(cropType, out var def)) return false;
        if (!_config.Current.SlotFamilies.TryGetValue(def.SlotFamily, out var fam)) return false;
        return CountFamilyPlots(charName, def.SlotFamily) >= fam.Max;
    }

    private void RaisePlotChanged(Plot plot, PlotStage? oldStage, PlotStage newStage)
        => PlotChanged?.Invoke(this, new PlotChangedArgs(plot, oldStage, newStage));

    public void Hydrate(GardenState loaded)
    {
        _plotsByChar.Clear();
        foreach (var (charName, plots) in loaded.PlotsByChar)
            HydrateCharacter(charName, plots);
    }

    /// <summary>Replace (or add) a single character's plot bucket. Raises <see cref="PlotChanged"/>
    /// for each restored plot so VMs can render them.</summary>
    public void HydrateCharacter(string charName, IReadOnlyDictionary<string, PersistedPlot> plots)
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
                PausedSince = pp.PausedSince,
                PausedDuration = pp.PausedDuration,
            };
            bucket[id] = plot;
            RaisePlotChanged(plot, null, plot.Stage);
        }
        _plotsByChar[charName] = bucket;
    }

    private TimeSpan HarvestedTtl =>
        TimeSpan.FromMinutes(_settings?.HarvestedAutoClearMinutes ?? 10);

    /// <summary>
    /// Drops plots whose in-game entity has almost certainly been garbage-collected.
    /// TTL for growing plots is derived from the crop's expected lifetime
    /// (growthSeconds × 2 + 10m, 5m for unknown crops). Harvested plots are
    /// dropped 2 hours after harvest. A "Clear harvested" user action can
    /// remove them sooner.
    /// </summary>
    public void PruneWithered()
    {
        var now = _time.GetUtcNow();
        var removed = 0;
        foreach (var plots in _plotsByChar.Values)
        {
            var toRemove = new List<string>();
            foreach (var (id, p) in plots)
            {
                var ttl = p.Stage == PlotStage.Harvested ? HarvestedTtl : ExpectedEntityLifetime(p);
                if (now - p.UpdatedAt > ttl) toRemove.Add(id);
            }
            foreach (var id in toRemove) plots.Remove(id);
            removed += toRemove.Count;
        }
        if (removed > 0) PlotsRemoved?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Manually drop a single plot (e.g. a stale plot that was planted/harvested while the app was offline).</summary>
    public bool DeletePlot(string charName, string plotId)
    {
        if (!_plotsByChar.TryGetValue(charName, out var plots)) return false;
        if (!plots.Remove(plotId)) return false;
        if (_pendingHarvestPlotId == plotId) _pendingHarvestPlotId = null;
        if (_pendingPlant is { } p && p.PlotId == plotId && p.CharName == charName) _pendingPlant = null;
        PlotsRemoved?.Invoke(this, EventArgs.Empty);
        return true;
    }

    /// <summary>Manually drop every harvested plot for every character.</summary>
    public int ClearHarvested()
    {
        var dropped = 0;
        foreach (var plots in _plotsByChar.Values)
        {
            var toRemove = plots.Where(kv => kv.Value.Stage == PlotStage.Harvested).Select(kv => kv.Key).ToList();
            foreach (var id in toRemove) { plots.Remove(id); dropped++; }
        }
        if (dropped > 0) PlotsRemoved?.Invoke(this, EventArgs.Empty);
        return dropped;
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
