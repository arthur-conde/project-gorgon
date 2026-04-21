using System.Collections.ObjectModel;
using Arwen.Domain;
using Arwen.State;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
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
    double RelativeScore,
    string Location,
    long ItemId,
    int IconId);

public sealed partial class GiftScannerViewModel : ObservableObject
{
    private readonly FavorStateService _state;
    private readonly GiftIndex _giftIndex;
    private readonly GameConfig _gameConfig;
    private StorageReport? _loadedReport;

    private readonly CalibrationService _calibration;

    public GiftScannerViewModel(FavorStateService state, GiftIndex giftIndex, CalibrationService calibration, GameConfig gameConfig)
    {
        _state = state;
        _giftIndex = giftIndex;
        _calibration = calibration;
        _gameConfig = gameConfig;
        _state.StateChanged += (_, _) => RefreshNpcList();
        RefreshNpcList();
        RefreshReports();
    }

    // ── NPC selection ───────────────────────────────────────────────────

    [ObservableProperty]
    private ObservableCollection<NpcFavorEntry> _npcList = [];

    [ObservableProperty]
    private NpcFavorEntry? _selectedNpc;

    // ── Storage report selection ────────────────────────────────────────

    [ObservableProperty]
    private ObservableCollection<ReportFileInfo> _availableReports = [];

    [ObservableProperty]
    private ReportFileInfo? _selectedReport;

    // ── Results ─────────────────────────────────────────────────────────

    [ObservableProperty]
    private ObservableCollection<GiftScannerRow> _results = [];

    [ObservableProperty]
    private string _statusMessage = "Select an NPC and storage report to scan.";

    // ── Change handlers ─────────────────────────────────────────────────

    partial void OnSelectedNpcChanged(NpcFavorEntry? value) => Scan();

    partial void OnSelectedReportChanged(ReportFileInfo? value)
    {
        if (value is not null)
            LoadReport(value.FilePath);
        else
            _loadedReport = null;
        Scan();
    }

    // ── Commands ────────────────────────────────────────────────────────

    [RelayCommand]
    private void RefreshReports()
    {
        var dir = _gameConfig.ReportsDirectory;
        var reports = StorageReportLoader.ScanForReports(dir);
        AvailableReports = new ObservableCollection<ReportFileInfo>(reports);

        if (reports.Count > 0 && SelectedReport is null)
            SelectedReport = reports[0];
    }

    // ── Helpers ─────────────────────────────────────────────────────────

    private void RefreshNpcList()
    {
        var previousKey = SelectedNpc?.NpcKey;

        var entries = _state.Entries
            .Where(e => e.Preferences.Count > 0)
            .OrderBy(e => e.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
        NpcList = new ObservableCollection<NpcFavorEntry>(entries);

        if (previousKey is not null)
            SelectedNpc = NpcList.FirstOrDefault(e => e.NpcKey == previousKey);
    }

    private void LoadReport(string filePath)
    {
        try
        {
            _loadedReport = StorageReportLoader.Load(filePath);
        }
        catch (Exception ex)
        {
            _loadedReport = null;
            StatusMessage = $"Error loading report: {ex.Message}";
        }
    }

    private void Scan()
    {
        if (SelectedNpc is null || _loadedReport is null)
        {
            Results = [];
            StatusMessage = "Select an NPC and storage report to scan.";
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

        var rows = new List<GiftScannerRow>();
        foreach (var item in _loadedReport.Items)
        {
            if (!matchByItemId.TryGetValue(item.TypeID, out var match)) continue;
            // Skip items from Hate/Dislike preferences
            if (match.Desire is "Hate" or "Dislike") continue;
            // Verify rarity/value filters against the actual inventory item
            if (!PassesStorageFilters(item, match)) continue;

            var location = StorageReportLoader.NormalizeLocation(item.StorageVault, item.IsInInventory);
            var estimatedPerGift = _calibration.EstimateFavor(match, SelectedNpc.NpcKey);
            var estimatedTotal = estimatedPerGift.HasValue ? estimatedPerGift.Value * item.StackSize : (double?)null;
            rows.Add(new GiftScannerRow(
                item.Name,
                item.StackSize,
                match.Desire,
                match.Pref,
                item.Value,
                estimatedTotal,
                match.Pref * (double)Math.Max(item.Value, 1) * item.StackSize,
                location,
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
    /// Checks whether a real inventory item passes the filter criteria implied by the
    /// GiftMatch's matched keyword. CDN template matching is over-inclusive for filter
    /// keywords like MinRarity/MinValue/Rarity — the scanner can verify against actual
    /// inventory item properties.
    /// </summary>
    private static bool PassesStorageFilters(StorageItem item, GiftMatch match)
    {
        var kw = match.MatchedKeyword;

        // MinRarity:{tier} — the actual item must be at or above that rarity
        if (kw.StartsWith("MinRarity:", StringComparison.Ordinal))
        {
            var required = kw["MinRarity:".Length..];
            return RarityRank(item.Rarity) >= RarityRank(required);
        }

        // Rarity:{tier} — the actual item must be exactly that rarity
        if (kw.StartsWith("Rarity:", StringComparison.Ordinal))
        {
            var required = kw["Rarity:".Length..];
            if (required == "Common")
                return item.Rarity is null; // Common items have no Rarity field
            return string.Equals(item.Rarity, required, StringComparison.OrdinalIgnoreCase);
        }

        // MinValue:{amount} — the actual item's value must be at or above
        if (kw.StartsWith("MinValue:", StringComparison.Ordinal))
        {
            if (int.TryParse(kw.AsSpan("MinValue:".Length), out var minVal))
                return item.Value >= minVal;
        }

        // Crafted:y — the item must be player-crafted
        if (kw == "Crafted:y")
            return item.IsCrafted;

        return true; // all other keywords pass (already matched at template level)
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
