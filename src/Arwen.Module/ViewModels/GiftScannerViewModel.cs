using System.Collections.ObjectModel;
using Arwen.Domain;
using Arwen.State;
using CommunityToolkit.Mvvm.ComponentModel;
using Gorgon.Shared.Character;
using Gorgon.Shared.Game;
using Gorgon.Shared.Storage;

namespace Arwen.ViewModels;

/// <summary>One row in the gift scanner results grid.</summary>
public sealed record GiftScannerRow(
    string ItemName,
    int StackSize,
    string Desire,
    double Pref,
    decimal ItemValue,
    double? EstimatedFavor,
    string? EstimateSource,
    int EstimateSamples,
    double RelativeScore,
    string Location,
    double? CurrentFavor,
    double? ProjectedFavor,
    FavorTier CurrentTier,
    FavorTier ProjectedTier,
    double CurrentTierCeiling,
    double CurrentProgressFraction,
    double ProjectedProgressFraction,
    long ItemId,
    int IconId);

public sealed partial class GiftScannerViewModel : ObservableObject
{
    private readonly FavorStateService _state;
    private readonly GiftIndex _giftIndex;
    private readonly GameConfig _gameConfig;
    private readonly IActiveCharacterService _activeChar;

    private readonly CalibrationService _calibration;

    public GiftScannerViewModel(
        FavorStateService state,
        GiftIndex giftIndex,
        CalibrationService calibration,
        GameConfig gameConfig,
        IActiveCharacterService activeChar)
    {
        _state = state;
        _giftIndex = giftIndex;
        _calibration = calibration;
        _gameConfig = gameConfig;
        _activeChar = activeChar;
        _state.StateChanged += (_, _) => RefreshNpcList();
        _activeChar.ActiveCharacterChanged += (_, _) => DispatchRescan();
        _activeChar.StorageReportsChanged += (_, _) => DispatchRescan();
        RefreshNpcList();
        Scan();
    }

    private void DispatchRescan()
    {
        var d = System.Windows.Application.Current?.Dispatcher;
        if (d is null || d.CheckAccess()) Scan();
        else d.InvokeAsync(Scan);
    }

    // ── NPC selection ───────────────────────────────────────────────────

    [ObservableProperty]
    private ObservableCollection<NpcFavorEntry> _npcList = [];

    [ObservableProperty]
    private NpcFavorEntry? _selectedNpc;

    // ── Results ─────────────────────────────────────────────────────────

    [ObservableProperty]
    private ObservableCollection<GiftScannerRow> _results = [];

    [ObservableProperty]
    private string _statusMessage = "Select an NPC to scan the active character's storage.";

    // ── Change handlers ─────────────────────────────────────────────────

    partial void OnSelectedNpcChanged(NpcFavorEntry? value) => Scan();

    // ── Helpers ─────────────────────────────────────────────────────────

    private void RefreshNpcList()
    {
        var previousKey = SelectedNpc?.NpcKey;

        var newEntries = _state.Entries
            .Where(e => e.Preferences.Count > 0)
            .OrderBy(e => e.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();

        // Sync NpcList in place so ComboBox.SelectedItem isn't invalidated by an ItemsSource swap.
        var newKeys = new HashSet<string>(newEntries.Select(e => e.NpcKey), StringComparer.Ordinal);
        for (var i = NpcList.Count - 1; i >= 0; i--)
        {
            if (!newKeys.Contains(NpcList[i].NpcKey))
                NpcList.RemoveAt(i);
        }
        var existingKeys = new HashSet<string>(NpcList.Select(e => e.NpcKey), StringComparer.Ordinal);
        for (var i = 0; i < newEntries.Count; i++)
        {
            if (!existingKeys.Contains(newEntries[i].NpcKey))
                NpcList.Insert(i, newEntries[i]);
        }

        if (SelectedNpc is null && previousKey is not null)
            SelectedNpc = NpcList.FirstOrDefault(e => e.NpcKey == previousKey);
    }

    private void Scan()
    {
        var report = _activeChar.ActiveStorageContents;
        if (SelectedNpc is null || report is null)
        {
            Results = [];
            StatusMessage = report is null
                ? "No storage export for the active character — run /exportstorage in-game."
                : "Select an NPC to scan.";
            return;
        }

        var giftMatches = _giftIndex.GetGiftsForNpc(SelectedNpc.NpcKey);
        if (giftMatches.Count == 0)
        {
            Results = [];
            StatusMessage = $"{SelectedNpc.Name} has no known gift preferences.";
            return;
        }

        // Build fast lookup: itemId → GiftMatch
        var matchByItemId = new Dictionary<long, GiftMatch>(giftMatches.Count);
        foreach (var gm in giftMatches)
            matchByItemId.TryAdd(gm.ItemId, gm);

        // Current favor snapshot — prefer exact value from Player.log, fall back to tier floor
        var currentFavor = SelectedNpc.ExactFavor
                           ?? (double?)FavorTiers.FloorOf(SelectedNpc.CurrentTier);

        var rows = new List<GiftScannerRow>();
        foreach (var item in report.Items)
        {
            if (!matchByItemId.TryGetValue(item.TypeID, out var match)) continue;
            // Skip items from Hate/Dislike preferences
            if (match.Desire is "Hate" or "Dislike") continue;

            // Verify rarity/value filters against the actual inventory item. An item may have matched
            // multiple NPC preferences; each preference's filter-type keywords (MinRarity, Rarity,
            // MinValue, Crafted) must pass for the item to qualify.
            var allPrefs = _giftIndex.MatchAllPreferencesForItem(match.ItemId, SelectedNpc.NpcKey);
            if (!PassesStorageFilters(item, allPrefs)) continue;

            var location = StorageReportLoader.NormalizeLocation(item.StorageVault, item.IsInInventory);
            var estimate = _calibration.EstimateFavor(match, SelectedNpc.NpcKey);
            var estimatedTotal = estimate is not null ? estimate.Value * item.StackSize : (double?)null;

            double? projectedFavor = null;
            var currentTier = SelectedNpc.CurrentTier;
            var projectedTier = currentTier;
            var currentFrac = double.NaN;
            var projectedFrac = double.NaN;
            var tierCeiling = (double)(FavorTiers.CeilingOf(currentTier) ?? FavorTiers.FloorOf(currentTier));

            if (currentFavor.HasValue)
            {
                currentTier = FavorTiers.TierForFavor(currentFavor.Value);
                tierCeiling = (double)(FavorTiers.CeilingOf(currentTier) ?? currentFavor.Value);
                currentFrac = FavorTiers.ProgressInTier(currentFavor.Value, currentTier);

                if (estimatedTotal.HasValue)
                {
                    var proj = currentFavor.Value + estimatedTotal.Value;
                    projectedFavor = proj;
                    projectedTier = FavorTiers.TierForFavor(proj);
                    projectedFrac = projectedTier == currentTier
                        ? FavorTiers.ProgressInTier(proj, currentTier)
                        : 1.0; // projection crosses into a higher tier — show full bar
                }
            }

            rows.Add(new GiftScannerRow(
                item.Name,
                item.StackSize,
                match.Desire,
                match.Pref,
                item.Value,
                estimatedTotal,
                estimate?.Tier,
                estimate?.SampleCount ?? 0,
                match.Pref * (double)Math.Max(item.Value, 1) * item.StackSize,
                location,
                currentFavor,
                projectedFavor,
                currentTier,
                projectedTier,
                tierCeiling,
                double.IsNaN(currentFrac) ? 0 : currentFrac,
                double.IsNaN(projectedFrac) ? 0 : projectedFrac,
                item.TypeID,
                match.IconId));
        }

        rows.Sort((a, b) => b.RelativeScore.CompareTo(a.RelativeScore));
        Results = new ObservableCollection<GiftScannerRow>(rows);

        var calibratedCount = rows.Count(r => r.EstimatedFavor.HasValue);
        var calibratedSuffix = calibratedCount > 0
            ? $" ({calibratedCount} with calibrated estimates)"
            : " (gift items in-game to calibrate rates)";
        StatusMessage = $"Found {rows.Count:N0} giftable items{calibratedSuffix}";
    }

    /// <summary>
    /// Checks whether a real inventory item passes every filter-style keyword across all
    /// matched NPC preferences. CDN template matching is over-inclusive for filter keywords
    /// like MinRarity/MinValue/Rarity — the scanner verifies against actual inventory item
    /// properties. All filter checks must pass.
    /// </summary>
    private static bool PassesStorageFilters(StorageItem item, IReadOnlyList<MatchedPreference> allPrefs)
    {
        foreach (var pref in allPrefs)
        {
            foreach (var kw in pref.Keywords)
            {
                if (!PassesSingleFilter(item, kw)) return false;
            }
        }
        return true;
    }

    private static bool PassesSingleFilter(StorageItem item, string kw)
    {
        if (kw.StartsWith("MinRarity:", StringComparison.Ordinal))
        {
            var required = kw["MinRarity:".Length..];
            return RarityRank(item.Rarity) >= RarityRank(required);
        }

        if (kw.StartsWith("Rarity:", StringComparison.Ordinal))
        {
            var required = kw["Rarity:".Length..];
            if (required == "Common")
                return item.Rarity is null;
            return string.Equals(item.Rarity, required, StringComparison.OrdinalIgnoreCase);
        }

        if (kw.StartsWith("MinValue:", StringComparison.Ordinal))
        {
            if (int.TryParse(kw.AsSpan("MinValue:".Length), out var minVal))
                return item.Value >= minVal;
        }

        if (kw == "Crafted:y")
            return item.IsCrafted;

        return true;
    }

    private static int RarityRank(string? rarity) => rarity switch
    {
        null => 0,           // Common (no rarity field)
        "Uncommon" => 1,
        "Rare" => 2,
        "Exceptional" => 3,
        "Epic" => 4,
        "Legendary" => 5,
        _ => 0,
    };
}
