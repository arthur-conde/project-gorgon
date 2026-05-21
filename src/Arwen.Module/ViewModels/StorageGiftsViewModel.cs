using System.Collections.ObjectModel;
using Arwen.Domain;
using Arwen.State;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Mithril.Shared.Character;
using Mithril.Shared.Reference;
using Mithril.GameReports;
using FavorScale = Mithril.Reference.Models.Npcs.FavorScale;
using FavorTier = Mithril.Reference.Models.Npcs.FavorTier;

namespace Arwen.ViewModels;

/// <summary>
/// "Storage Gifts" tab: cross-reference everything in the active character's
/// storage with the NPCs who would accept it as a gift. Master pane shows
/// items grouped by vault; detail pane shows recipients grouped by NPC area
/// with calibrated favor projections per stack.
/// </summary>
public sealed partial class StorageGiftsViewModel : ObservableObject
{
    private readonly GiftIndex _giftIndex;
    private readonly IActiveCharacterService _activeChar;
    private readonly IReferenceDataService _refData;
    private readonly CalibrationService _calibration;
    private readonly FavorStateService _state;
    private readonly IFavorViewNavigator _navigator;

    /// <summary>Backing list for all giftable items, before search/love-only filtering.</summary>
    private List<StorageItemCard> _allItems = [];

    public StorageGiftsViewModel(
        GiftIndex giftIndex,
        IActiveCharacterService activeChar,
        IReferenceDataService refData,
        CalibrationService calibration,
        FavorStateService state,
        IFavorViewNavigator navigator)
    {
        _giftIndex = giftIndex;
        _activeChar = activeChar;
        _refData = refData;
        _calibration = calibration;
        _state = state;
        _navigator = navigator;

        _activeChar.ActiveCharacterChanged += (_, _) => DispatchReload();
        _activeChar.StorageReportsChanged += (_, _) => DispatchReload();
        _giftIndex.Rebuilt += (_, _) => DispatchReload();
        _state.StateChanged += (_, _) => DispatchReload();

        Reload();
    }

    /// <summary>Flat, post-filter list of giftable items. WPF groups by Vault for the master pane.</summary>
    public ObservableCollection<StorageItemCard> Items { get; } = [];

    [ObservableProperty]
    private StorageItemCard? _selectedItem;

    /// <summary>Recipients of <see cref="SelectedItem"/>, regrouped by NPC area for the detail pane.</summary>
    [ObservableProperty]
    private IReadOnlyList<RecipientAreaGroup> _recipients = [];

    [ObservableProperty]
    private string _searchText = "";

    [ObservableProperty]
    private bool _loveOnly;

    [ObservableProperty]
    private bool _hasStorage;

    [ObservableProperty]
    private string _statusMessage = "";

    partial void OnSelectedItemChanged(StorageItemCard? value) => RefreshRecipients();

    partial void OnSearchTextChanged(string value) => ApplyFilter();

    partial void OnLoveOnlyChanged(bool value) => ApplyFilter();

    [RelayCommand]
    private void OpenInGiftScanner(string? npcKey)
    {
        if (!string.IsNullOrEmpty(npcKey))
            _navigator.OpenInGiftScanner(npcKey);
    }

    private void DispatchReload()
    {
        var d = System.Windows.Application.Current?.Dispatcher;
        if (d is null || d.CheckAccess()) Reload();
        else d.InvokeAsync(Reload);
    }

    private void Reload()
    {
        var report = _activeChar.ActiveStorageContents;
        if (report is null)
        {
            _allItems = [];
            HasStorage = false;
            StatusMessage = "No storage export for the active character — run /exportstorage in-game.";
            ApplyFilter();
            return;
        }

        HasStorage = true;
        var built = new List<StorageItemCard>(report.Items.Count);

        foreach (var item in report.Items)
        {
            if (item.StackSize <= 0) continue;

            var npcMatches = _giftIndex.GetNpcsForItem(item.TypeID);
            if (npcMatches.Count == 0) continue;

            var recipients = new List<RecipientCard>(npcMatches.Count);
            foreach (var nm in npcMatches)
            {
                if (nm.Match.Desire is "Hate" or "Dislike") continue;

                // Verify rarity/value/crafted filters against the actual inventory item.
                var allPrefs = _giftIndex.MatchAllPreferencesForItem(item.TypeID, nm.NpcKey);
                if (!GiftFilter.Passes(item, allPrefs)) continue;

                recipients.Add(BuildRecipient(nm, item.StackSize));
            }

            if (recipients.Count == 0) continue;

            // Sort recipients within the item: Love before Like, then by score desc.
            recipients.Sort(RecipientComparer);

            var vault = StorageReportLoader.NormalizeLocation(item.StorageVault, item.IsInInventory);
            var hasLove = recipients.Any(r => r.Desire == "Love");

            built.Add(new StorageItemCard(
                TypeId: item.TypeID,
                IconId: TryGetIconId(item.TypeID),
                Name: item.Name,
                StackSize: item.StackSize,
                Vault: vault,
                RecipientCount: recipients.Count,
                HasLove: hasLove,
                AllRecipients: recipients));
        }

        // Order: vault asc, then item name asc — ListBox grouping preserves insertion order.
        built.Sort((a, b) =>
        {
            var v = string.Compare(a.Vault, b.Vault, StringComparison.OrdinalIgnoreCase);
            if (v != 0) return v;
            return string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase);
        });

        _allItems = built;
        ApplyFilter();
    }

    private void ApplyFilter()
    {
        var prevKey = SelectedItem is { } s ? (s.TypeId, s.Vault) : ((int, string)?)null;
        var search = SearchText?.Trim() ?? "";
        var loveOnly = LoveOnly;

        var filtered = _allItems.Where(card =>
        {
            if (loveOnly && !card.HasLove) return false;
            if (search.Length > 0 && !card.Name.Contains(search, StringComparison.OrdinalIgnoreCase)) return false;
            return true;
        }).ToList();

        // Mutate the ObservableCollection in place so CollectionViewSource grouping in XAML stays stable.
        Items.Clear();
        foreach (var c in filtered) Items.Add(c);

        // Restore selection if the previously-selected item is still visible.
        SelectedItem = prevKey is { } pk
            ? filtered.FirstOrDefault(c => c.TypeId == pk.Item1 && c.Vault == pk.Item2)
            : null;

        StatusMessage = HasStorage
            ? $"{filtered.Count:N0} giftable item(s) across {_allItems.Select(c => c.Vault).Distinct().Count()} vault(s)"
            : StatusMessage;
    }

    private void RefreshRecipients()
    {
        if (SelectedItem is null)
        {
            Recipients = [];
            return;
        }

        Recipients = SelectedItem.AllRecipients
            .GroupBy(r => string.IsNullOrEmpty(r.NpcArea) ? "Unknown" : r.NpcArea, StringComparer.OrdinalIgnoreCase)
            .OrderBy(g => g.Key, StringComparer.OrdinalIgnoreCase)
            .Select(g => new RecipientAreaGroup(g.Key, g.OrderBy(r => r, RecipientComparer).ToList()))
            .ToList();
    }

    private RecipientCard BuildRecipient(NpcGiftMatch nm, int stackSize)
    {
        var npcEntry = _refData.Npcs.TryGetValue(nm.NpcKey, out var npc) ? npc : null;
        var name = npcEntry?.Name ?? nm.NpcKey.Replace("NPC_", "");
        var area = npcEntry?.Area ?? "";

        var favorEntry = _state.Entries.FirstOrDefault(e => e.NpcKey == nm.NpcKey);
        var currentFavor = favorEntry?.ExactFavor
            ?? (favorEntry?.IsKnown == true ? FavorTiers.RepresentativeFavor(favorEntry.CurrentTier) : (double?)null);

        var estimate = _calibration.EstimateFavor(nm.Match, nm.NpcKey);
        var estimatedTotal = estimate is not null ? estimate.Value * stackSize : (double?)null;

        // Mirror GiftScannerViewModel's projection math so the same XAML cell template renders both.
        double? projectedFavor = null;
        var currentTier = favorEntry?.CurrentTier ?? FavorTier.Neutral;
        var projectedTier = currentTier;
        var currentFrac = 0.0;
        var projectedFrac = 0.0;
        var tierCeiling = FavorScale.CeilingOf(currentTier) ?? FavorScale.FloorOf(currentTier) ?? 0;

        if (currentFavor.HasValue)
        {
            currentTier = FavorScale.TierForFavor(currentFavor.Value);
            tierCeiling = FavorScale.CeilingOf(currentTier) ?? currentFavor.Value;
            var cFrac = FavorScale.ProgressInTier(currentFavor.Value, currentTier);
            currentFrac = double.IsNaN(cFrac) ? 0.0 : cFrac;

            if (estimatedTotal.HasValue)
            {
                var proj = currentFavor.Value + estimatedTotal.Value;
                projectedFavor = proj;
                projectedTier = FavorScale.TierForFavor(proj);
                var pFrac = projectedTier == currentTier
                    ? FavorScale.ProgressInTier(proj, currentTier)
                    : 1.0;
                projectedFrac = double.IsNaN(pFrac) ? 0.0 : pFrac;
            }
        }

        return new RecipientCard(
            NpcKey: nm.NpcKey,
            NpcName: name,
            NpcArea: area,
            Desire: nm.Match.Desire,
            RelativeScore: nm.Match.RelativeScore,
            EstimatedFavor: estimatedTotal,
            EstimateSource: estimate?.Tier,
            EstimateSamples: estimate?.SampleCount ?? 0,
            CurrentFavor: currentFavor,
            ProjectedFavor: projectedFavor,
            CurrentTier: currentTier,
            ProjectedTier: projectedTier,
            CurrentTierCeiling: tierCeiling,
            CurrentProgressFraction: currentFrac,
            ProjectedProgressFraction: projectedFrac);
    }

    private int TryGetIconId(int itemTypeId) =>
        _refData.Items.TryGetValue(itemTypeId, out var entry) ? entry.IconId : 0;

    private static readonly IComparer<RecipientCard> RecipientComparer =
        Comparer<RecipientCard>.Create((a, b) =>
        {
            var d = DesireRank(a.Desire).CompareTo(DesireRank(b.Desire));
            if (d != 0) return d;
            return b.RelativeScore.CompareTo(a.RelativeScore);
        });

    private static int DesireRank(string desire) => desire switch
    {
        "Love" => 0,
        "Like" => 1,
        _ => 2,
    };
}
