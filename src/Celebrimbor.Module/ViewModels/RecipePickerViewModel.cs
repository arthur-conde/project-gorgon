using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows;
using Celebrimbor.Domain;
using Celebrimbor.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Gorgon.Shared.Character;
using Gorgon.Shared.Reference;
using Gorgon.Shared.Wpf;

namespace Celebrimbor.ViewModels;

public sealed partial class RecipePickerViewModel : ObservableObject
{
    private readonly CelebrimborSettings _settings;
    private readonly IActiveCharacterService _activeChar;
    private readonly IReferenceDataService _refData;
    private readonly RecipeSearchIndex _search;
    private readonly IItemDetailPresenter _itemDetail;
    private Dictionary<string, RecipeRowViewModel> _rowByName = new(StringComparer.Ordinal);
    private bool _syncing;

    public event EventHandler? FinalizeRequested;
    public event EventHandler? CraftListChanged;

    public RecipePickerViewModel(
        CelebrimborSettings settings,
        IActiveCharacterService activeChar,
        IReferenceDataService refData,
        RecipeSearchIndex search,
        IItemDetailPresenter itemDetail)
    {
        _settings = settings;
        _activeChar = activeChar;
        _refData = refData;
        _search = search;
        _itemDetail = itemDetail;

        BuildRows();
        ApplyInitialQuantities();
        RebuildCraftListItems();

        _activeChar.ActiveCharacterChanged += (_, _) => DispatchOnUi(RefreshCharacterFlags);
        _settings.PropertyChanged += OnSettingsChanged;
    }

    [ObservableProperty]
    private string _statusMessage = "";

    [ObservableProperty]
    private string _queryText = "";

    public ObservableCollection<RecipeRowViewModel> AllRows { get; } = [];
    public ObservableCollection<CraftListItemViewModel> CraftListItems { get; } = [];

    public bool KnownRecipesOnly
    {
        get => _settings.KnownRecipesOnly;
        set { _settings.KnownRecipesOnly = value; OnPropertyChanged(); }
    }

    public bool EnforceSkillLevel
    {
        get => _settings.EnforceSkillLevel;
        set { _settings.EnforceSkillLevel = value; OnPropertyChanged(); }
    }

    public bool HasActiveCharacter => _activeChar.ActiveCharacter is not null;
    public int TooltipDelayMs => _settings.TooltipDelayMs;
    public bool HasCraftList => CraftListItems.Count > 0;
    public int CraftListCount => CraftListItems.Count;
    public int CraftListTotalQuantity => CraftListItems.Sum(i => i.Quantity);

    [RelayCommand]
    private void Commit()
    {
        PersistCraftList();
        FinalizeRequested?.Invoke(this, EventArgs.Empty);
    }

    [RelayCommand]
    private void AddRow(RecipeRowViewModel? row)
    {
        if (row is null) return;
        _syncing = true;
        try { row.Quantity = row.Quantity <= 0 ? 1 : row.Quantity + 1; }
        finally { _syncing = false; }

        OnCraftListMutated();
    }

    [RelayCommand]
    private void Clear()
    {
        _syncing = true;
        try
        {
            foreach (var row in AllRows) row.Quantity = 0;
        }
        finally { _syncing = false; }

        OnCraftListMutated();
    }

    [RelayCommand]
    private void RemoveFromList(CraftListItemViewModel? entry)
    {
        if (entry is null) return;
        if (!_rowByName.TryGetValue(entry.RecipeInternalName, out var row)) return;
        row.Quantity = 0;
        OnCraftListMutated();
    }

    [RelayCommand]
    private void CopyList()
    {
        var list = _settings.CraftList.Where(e => e.Quantity > 0).ToList();
        if (list.Count == 0)
        {
            StatusMessage = "No recipes to copy.";
            return;
        }
        try
        {
            var text = CraftListFormat.Serialize(list);
            Clipboard.SetText(text);
            StatusMessage = $"Copied {list.Count} recipes to clipboard.";
        }
        catch (Exception ex)
        {
            StatusMessage = $"Copy failed: {ex.Message}";
        }
    }

    [RelayCommand]
    private void PasteList()
    {
        string text;
        try { text = Clipboard.GetText(); }
        catch (Exception ex) { StatusMessage = $"Paste failed: {ex.Message}"; return; }

        if (string.IsNullOrWhiteSpace(text))
        {
            StatusMessage = "Clipboard is empty.";
            return;
        }

        var result = CraftListFormat.Parse(text, _refData);
        if (result.Entries.Count == 0)
        {
            StatusMessage = result.Warnings.Count == 0
                ? "No recipes found in clipboard text."
                : $"No recipes parsed. {result.Warnings.Count} warnings.";
            return;
        }

        var choice = MessageBox.Show(
            $"Paste {result.Entries.Count} recipes into your craft list?\n\n" +
            "Yes — Append (sum quantities for duplicates)\n" +
            "No — Replace the current list\n" +
            "Cancel — Do nothing",
            "Celebrimbor · Paste list",
            MessageBoxButton.YesNoCancel,
            MessageBoxImage.Question);

        if (choice == MessageBoxResult.Cancel) return;

        var merged = choice == MessageBoxResult.Yes
            ? CraftListFormat.MergeAppend(_settings.CraftList, result.Entries)
            : result.Entries.Select(e => new CraftListEntry { RecipeInternalName = e.RecipeInternalName, Quantity = e.Quantity }).ToList();

        _settings.CraftList = merged;
        _settings.Touch(nameof(CelebrimborSettings.CraftList));
        ApplyInitialQuantities();
        OnCraftListMutated();

        var skipped = result.Warnings.Count;
        StatusMessage = skipped == 0
            ? $"Imported {result.Entries.Count} recipes."
            : $"Imported {result.Entries.Count} recipes, skipped {skipped}.";
    }

    private void OnRowPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (sender is not RecipeRowViewModel row) return;
        if (e.PropertyName != nameof(RecipeRowViewModel.Quantity)) return;
        if (_syncing) return;
        OnCraftListMutated();
    }

    private void OnCraftListMutated()
    {
        PersistCraftList();
        RebuildCraftListItems();
        CraftListChanged?.Invoke(this, EventArgs.Empty);
    }

    private void OnSettingsChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(CelebrimborSettings.KnownRecipesOnly):
                OnPropertyChanged(nameof(KnownRecipesOnly));
                break;
            case nameof(CelebrimborSettings.EnforceSkillLevel):
                OnPropertyChanged(nameof(EnforceSkillLevel));
                break;
            case nameof(CelebrimborSettings.TooltipDelayMs):
                OnPropertyChanged(nameof(TooltipDelayMs));
                break;
        }
    }

    private void BuildRows()
    {
        AllRows.Clear();
        var dict = new Dictionary<string, RecipeRowViewModel>(StringComparer.Ordinal);
        foreach (var recipe in _search.AllRecipes)
        {
            var row = new RecipeRowViewModel(recipe, _refData, _itemDetail);
            row.PropertyChanged += OnRowPropertyChanged;
            AllRows.Add(row);
            dict[recipe.InternalName] = row;
        }
        _rowByName = dict;
        RefreshCharacterFlags();
    }

    private void ApplyInitialQuantities()
    {
        _syncing = true;
        try
        {
            foreach (var row in AllRows) row.Quantity = 0;
            foreach (var entry in _settings.CraftList)
            {
                if (entry.Quantity <= 0) continue;
                if (_rowByName.TryGetValue(entry.RecipeInternalName, out var row))
                    row.Quantity = entry.Quantity;
            }
        }
        finally { _syncing = false; }
    }

    private void PersistCraftList()
    {
        _settings.CraftList = AllRows
            .Where(r => r.Quantity > 0)
            .Select(r => new CraftListEntry { RecipeInternalName = r.InternalName, Quantity = r.Quantity })
            .ToList();
        _settings.Touch(nameof(CelebrimborSettings.CraftList));
    }

    private void RebuildCraftListItems()
    {
        // Detach existing change handlers.
        foreach (var item in CraftListItems) item.PropertyChanged -= OnCraftListItemPropertyChanged;

        CraftListItems.Clear();
        foreach (var row in AllRows.Where(r => r.Quantity > 0))
        {
            var item = new CraftListItemViewModel(
                row.InternalName,
                row.Name,
                row.IconId,
                row.Quantity,
                row.Skill,
                row.SkillLevelReq,
                row.Ingredients,
                row.Results,
                row.CraftedOutputs,
                _itemDetail);
            item.PropertyChanged += OnCraftListItemPropertyChanged;
            CraftListItems.Add(item);
        }
        OnPropertyChanged(nameof(HasCraftList));
        OnPropertyChanged(nameof(CraftListCount));
        OnPropertyChanged(nameof(CraftListTotalQuantity));
        UpdateStatus();
    }

    private void OnCraftListItemPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (sender is not CraftListItemViewModel item) return;
        if (e.PropertyName != nameof(CraftListItemViewModel.Quantity)) return;
        if (!_rowByName.TryGetValue(item.RecipeInternalName, out var row)) return;
        if (row.Quantity == item.Quantity) return;

        _syncing = true;
        try { row.Quantity = Math.Max(0, item.Quantity); }
        finally { _syncing = false; }

        if (row.Quantity == 0)
        {
            // Entry removed from list — rebuild so the drawer collection matches.
            PersistCraftList();
            RebuildCraftListItems();
            CraftListChanged?.Invoke(this, EventArgs.Empty);
        }
        else
        {
            PersistCraftList();
            OnPropertyChanged(nameof(CraftListTotalQuantity));
            CraftListChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    private void RefreshCharacterFlags()
    {
        var character = _activeChar.ActiveCharacter;
        foreach (var row in AllRows)
        {
            row.IsKnown = character is not null
                && character.RecipeCompletions.TryGetValue(row.InternalName, out var n)
                && n > 0;

            var effective = 0;
            if (character is not null && character.Skills.TryGetValue(row.Recipe.Skill, out var skill))
                effective = skill.Level + skill.BonusLevels;
            row.MeetsSkill = effective >= row.Recipe.SkillLevelReq;
        }
        OnPropertyChanged(nameof(HasActiveCharacter));
    }

    private void UpdateStatus()
    {
        var selected = CraftListItems.Count;
        StatusMessage = selected == 0
            ? $"{AllRows.Count:N0} recipes"
            : $"{selected} in list · {CraftListTotalQuantity} total batches";
    }

    private static void DispatchOnUi(Action action)
    {
        var d = System.Windows.Application.Current?.Dispatcher;
        if (d is null || d.CheckAccess()) action();
        else d.InvokeAsync(action);
    }
}
