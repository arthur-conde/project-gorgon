using System.Collections.ObjectModel;
using Arwen.Domain;
using Arwen.State;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Mithril.Shared.Reference;

namespace Arwen.ViewModels;

public sealed partial class FavorCalculatorViewModel : ObservableObject
{
    private readonly FavorStateService _state;
    private readonly GiftIndex _giftIndex;
    private readonly CalibrationService _calibration;

    public FavorCalculatorViewModel(FavorStateService state, GiftIndex giftIndex, CalibrationService calibration)
    {
        _state = state;
        _giftIndex = giftIndex;
        _calibration = calibration;
        _state.StateChanged += (_, _) => RefreshNpcList();
        _giftIndex.Rebuilt += (_, _) => OnSelectedNpcChanged(SelectedNpc);
        _calibration.DataChanged += (_, _) => Recalculate();
        RefreshNpcList();
    }

    // ── NPC selection ───────────────────────────────────────────────────

    [ObservableProperty]
    private ObservableCollection<NpcFavorEntry> _npcList = [];

    [ObservableProperty]
    private NpcFavorEntry? _selectedNpc;

    [ObservableProperty]
    private string _npcSearchText = "";

    [ObservableProperty]
    private ObservableCollection<NpcPreference> _npcPreferences = [];

    // ── Item selection & matching ────────────────────────────────────────

    [ObservableProperty]
    private string _itemSearchText = "";

    [ObservableProperty]
    private ObservableCollection<GiftMatch> _matchingItems = [];

    [ObservableProperty]
    private GiftMatch? _selectedItem;

    [ObservableProperty]
    private string _favorPerGiftText = "";

    // ── Calculation ─────────────────────────────────────────────────────

    [ObservableProperty]
    private FavorTier _targetTier = FavorTier.SoulMates;

    [ObservableProperty]
    private string _resultText = "";

    [ObservableProperty]
    private string _breakdownText = "";

    // ── Change handlers ─────────────────────────────────────────────────

    partial void OnNpcSearchTextChanged(string value) => FilterNpcList();

    partial void OnSelectedNpcChanged(NpcFavorEntry? value)
    {
        if (value is null)
        {
            NpcPreferences = [];
            MatchingItems = [];
            ResultText = "";
            BreakdownText = "";
            return;
        }

        NpcPreferences = new ObservableCollection<NpcPreference>(
            value.Preferences.OrderBy(p => p.Desire switch { "Love" => 0, "Like" => 1, "Dislike" => 2, "Hate" => 3, _ => 4 })
                             .ThenByDescending(p => p.Pref));

        RefreshGiftList();
    }

    partial void OnItemSearchTextChanged(string value) => RefreshGiftList();

    partial void OnSelectedItemChanged(GiftMatch? value)
    {
        if (value is null)
        {
            FavorPerGiftText = "";
        }
        else
        {
            var estimated = _calibration.EstimateFavor(value, SelectedNpc?.NpcKey);
            FavorPerGiftText = estimated is not null
                ? $"~{estimated.Value:F1} favor (calibrated: {estimated.Tier})"
                : $"pref {value.Pref:+0.#;-0.#} × value {value.ItemValue:N0} (not yet calibrated)";
        }
        Recalculate();
    }

    partial void OnTargetTierChanged(FavorTier value) => Recalculate();

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
        FilterNpcList();

        if (SelectedNpc is null && previousKey is not null)
            SelectedNpc = NpcList.FirstOrDefault(e => e.NpcKey == previousKey);
    }

    private void FilterNpcList()
    {
        // Filtering happens in the view's ComboBox or via the search text binding.
        // Here we just refresh the matching items when the NPC list changes.
    }

    private void RefreshGiftList()
    {
        if (SelectedNpc is null) { MatchingItems = []; return; }

        var gifts = _giftIndex.GetGiftsForNpc(SelectedNpc.NpcKey);
        IEnumerable<GiftMatch> filtered = gifts.Where(g => g.Desire is "Love" or "Like");

        if (!string.IsNullOrWhiteSpace(ItemSearchText))
        {
            var q = ItemSearchText;
            filtered = filtered.Where(g => g.ItemName.Contains(q, StringComparison.OrdinalIgnoreCase));
        }

        MatchingItems = new ObservableCollection<GiftMatch>(filtered.Take(200));
        SelectedItem = null;
    }

    private void Recalculate()
    {
        if (SelectedNpc is null || SelectedItem is null)
        {
            ResultText = "Select an NPC and item to calculate.";
            BreakdownText = "";
            return;
        }

        double currentFavor;

        if (SelectedNpc.ExactFavor.HasValue)
        {
            currentFavor = SelectedNpc.ExactFavor.Value;
        }
        else
        {
            // Use floor of current tier as worst-case estimate
            currentFavor = FavorTiers.FloorOf(SelectedNpc.CurrentTier);
        }

        var targetFloor = FavorTiers.FloorOf(TargetTier);
        var remaining = Math.Max(0, targetFloor - currentFavor);

        if (remaining <= 0)
        {
            ResultText = $"Already at or above {FavorTiers.DisplayName(TargetTier)}!";
            BreakdownText = "";
            return;
        }

        var estimated = _calibration.EstimateFavor(SelectedItem, SelectedNpc.NpcKey);
        if (estimated is not null && estimated.Value > 0)
        {
            var itemsNeeded = (int)Math.Ceiling(remaining / estimated.Value);
            ResultText = $"~{itemsNeeded:N0} items needed (~{estimated.Value:F1} favor each, calibrated)";
        }
        else
        {
            ResultText = $"{remaining:F0} favor remaining to {FavorTiers.DisplayName(TargetTier)}. " +
                         $"Gift this item in-game to calibrate the rate for \"{SelectedItem.MatchedKeyword}\".";
        }

        // Tier breakdown
        var breakdown = FavorTiers.TierBreakdown(currentFavor);
        var parts = breakdown
            .Where(b => FavorTiers.FloorOf(b.Tier) < targetFloor || b.Tier == TargetTier)
            .Select(b => $"{FavorTiers.DisplayName(b.Tier)}: {b.Remaining:N0}");
        BreakdownText = string.Join(" → ", parts);
    }
}
