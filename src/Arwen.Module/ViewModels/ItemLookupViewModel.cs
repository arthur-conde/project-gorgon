using System.Collections.ObjectModel;
using Arwen.Domain;
using Arwen.State;
using CommunityToolkit.Mvvm.ComponentModel;
using Mithril.Shared.Reference;

namespace Arwen.ViewModels;

/// <summary>One row in the "who wants this item?" results.</summary>
public sealed class NpcWantsRow
{
    public required string NpcName { get; init; }
    public required string NpcKey { get; init; }
    public required string Area { get; init; }
    public required string Desire { get; init; }
    public required double Pref { get; init; }
    public required string MatchedKeyword { get; init; }
    public required string CurrentTier { get; init; }
    public required bool HasMet { get; init; }
    public double? EstimatedFavor { get; init; }
}

public sealed partial class ItemLookupViewModel : ObservableObject
{
    private readonly IReferenceDataService _refData;
    private readonly GiftIndex _giftIndex;
    private readonly CalibrationService _calibration;
    private readonly FavorStateService _state;

    public ItemLookupViewModel(
        IReferenceDataService refData,
        GiftIndex giftIndex,
        CalibrationService calibration,
        FavorStateService state)
    {
        _refData = refData;
        _giftIndex = giftIndex;
        _calibration = calibration;
        _state = state;
    }

    [ObservableProperty]
    private string _itemSearchText = "";

    [ObservableProperty]
    private ObservableCollection<ItemEntry> _matchingItems = [];

    [ObservableProperty]
    private ItemEntry? _selectedItem;

    [ObservableProperty]
    private ObservableCollection<NpcWantsRow> _results = [];

    [ObservableProperty]
    private string _statusMessage = "Search for an item to see which NPCs want it.";

    [ObservableProperty]
    private bool _showAllNpcs;

    partial void OnItemSearchTextChanged(string value) => SearchItems();

    partial void OnSelectedItemChanged(ItemEntry? value) => LookupNpcs();

    partial void OnShowAllNpcsChanged(bool value) => LookupNpcs();

    private void SearchItems()
    {
        if (string.IsNullOrWhiteSpace(ItemSearchText) || ItemSearchText.Length < 2)
        {
            MatchingItems = [];
            return;
        }

        var q = ItemSearchText;
        var matches = _refData.Items.Values
            .Where(i => i.Name.Contains(q, StringComparison.OrdinalIgnoreCase))
            .OrderBy(i => i.Name, StringComparer.OrdinalIgnoreCase)
            .Take(100)
            .ToList();
        MatchingItems = new ObservableCollection<ItemEntry>(matches);
    }

    private void LookupNpcs()
    {
        if (SelectedItem is null)
        {
            Results = [];
            StatusMessage = "Search for an item to see which NPCs want it.";
            return;
        }

        var npcMatches = _giftIndex.GetNpcsForItem(SelectedItem.Id);
        if (npcMatches.Count == 0)
        {
            Results = [];
            StatusMessage = $"No NPCs have a gift preference matching \"{SelectedItem.Name}\".";
            return;
        }

        var allRows = new List<NpcWantsRow>();
        foreach (var nm in npcMatches)
        {
            if (nm.Match.Desire is "Hate" or "Dislike") continue;

            var npcEntry = _refData.Npcs.TryGetValue(nm.NpcKey, out var npc) ? npc : null;
            var favorEntry = _state.Entries.FirstOrDefault(e => e.NpcKey == nm.NpcKey);
            var hasMet = favorEntry?.IsKnown == true;

            allRows.Add(new NpcWantsRow
            {
                NpcName = npcEntry?.Name ?? nm.NpcKey.Replace("NPC_", ""),
                NpcKey = nm.NpcKey,
                Area = npcEntry?.Area ?? "",
                Desire = nm.Match.Desire,
                Pref = nm.Match.Pref,
                MatchedKeyword = nm.Match.MatchedKeyword,
                CurrentTier = favorEntry is not null ? FavorTiers.DisplayName(favorEntry.CurrentTier) : "",
                HasMet = hasMet,
                EstimatedFavor = _calibration.EstimateFavor(nm.Match, nm.NpcKey)?.Value,
            });
        }

        // Sort: met NPCs first, then by pref descending
        allRows.Sort((a, b) =>
        {
            if (a.HasMet != b.HasMet) return a.HasMet ? -1 : 1;
            return b.Pref.CompareTo(a.Pref);
        });

        var filtered = ShowAllNpcs ? allRows : allRows.Where(r => r.HasMet).ToList();
        Results = new ObservableCollection<NpcWantsRow>(filtered);

        var metCount = allRows.Count(r => r.HasMet);
        var unmetCount = allRows.Count - metCount;
        StatusMessage = ShowAllNpcs
            ? $"{allRows.Count} NPC(s) want \"{SelectedItem.Name}\" ({metCount} met, {unmetCount} not yet met)"
            : $"{filtered.Count} met NPC(s) want \"{SelectedItem.Name}\" ({unmetCount} more hidden)";
    }
}
