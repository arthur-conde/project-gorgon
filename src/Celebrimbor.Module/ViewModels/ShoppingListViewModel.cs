using System.Collections.ObjectModel;
using System.ComponentModel;
using Celebrimbor.Domain;
using Celebrimbor.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Gorgon.Shared.Character;
using Gorgon.Shared.Reference;
using Gorgon.Shared.Wpf;

namespace Celebrimbor.ViewModels;

public sealed partial class ShoppingListViewModel : ObservableObject
{
    private readonly CelebrimborSettings _settings;
    private readonly RecipeAggregator _aggregator;
    private readonly OnHandInventoryQuery _onHand;
    private readonly IReferenceDataService _refData;
    private readonly IActiveCharacterService _activeChar;
    private readonly IItemDetailPresenter _itemDetail;

    public event EventHandler? BackRequested;

    public ShoppingListViewModel(
        CelebrimborSettings settings,
        RecipeAggregator aggregator,
        OnHandInventoryQuery onHand,
        IReferenceDataService refData,
        IActiveCharacterService activeChar,
        IItemDetailPresenter itemDetail)
    {
        _settings = settings;
        _aggregator = aggregator;
        _onHand = onHand;
        _refData = refData;
        _activeChar = activeChar;
        _itemDetail = itemDetail;

        _settings.PropertyChanged += OnSettingsChanged;
        _activeChar.StorageReportsChanged += (_, _) => DispatchOnUi(Rebuild);
        _activeChar.ActiveCharacterChanged += (_, _) => DispatchOnUi(Rebuild);
    }

    [ObservableProperty]
    private ObservableCollection<IngredientRowViewModel> _rows = [];

    [ObservableProperty]
    private ObservableCollection<CraftStepViewModel> _steps = [];

    [ObservableProperty]
    private ObservableCollection<CraftListItemViewModel> _makingItems = [];

    [ObservableProperty]
    private string _headerStatus = "";

    [ObservableProperty]
    private int _totalItems;

    [ObservableProperty]
    private int _craftReadyItems;

    public int ExpansionDepth
    {
        get => _settings.ExpansionDepth;
        set { _settings.ExpansionDepth = value; OnPropertyChanged(); }
    }

    public int TooltipDelayMs => _settings.TooltipDelayMs;

    [RelayCommand]
    private void Back() => BackRequested?.Invoke(this, EventArgs.Empty);

    [RelayCommand]
    private void Refresh()
    {
        _activeChar.Refresh();
        Rebuild();
    }

    public void Rebuild()
    {
        var overrides = _settings.OnHandOverrides
            .Where(o => !string.IsNullOrEmpty(o.ItemInternalName))
            .GroupBy(o => o.ItemInternalName, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.Last().Quantity, StringComparer.Ordinal);

        var onHand = _onHand.QueryActiveCharacter();

        var aggregated = _aggregator.Aggregate(
            _settings.CraftList,
            _settings.ExpansionDepth,
            _refData,
            onHand.Counts,
            onHand.Locations,
            overrides);

        var rows = new ObservableCollection<IngredientRowViewModel>();
        foreach (var ingredient in aggregated)
        {
            int? current = overrides.TryGetValue(ingredient.ItemInternalName, out var ov) ? ov : null;
            rows.Add(new IngredientRowViewModel(ingredient, current, OnOverrideChanged));
        }

        foreach (var old in Steps) old.Detach();

        // Two-level grouping: step (dependency depth) outer → PrimaryTag inner.
        var rowsByDepth = rows
            .GroupBy(r => r.Model.Depth)
            .OrderBy(g => g.Key)
            .ToList();
        var maxDepth = rowsByDepth.Count > 0 ? rowsByDepth.Max(g => g.Key) : 0;
        var stepCount = rowsByDepth.Count;

        var steps = new ObservableCollection<CraftStepViewModel>();
        var stepNumber = 1;
        foreach (var depthBucket in rowsByDepth)
        {
            var groups = depthBucket
                .GroupBy(r => r.PrimaryTag, StringComparer.OrdinalIgnoreCase)
                .OrderBy(g => g.Key, StringComparer.OrdinalIgnoreCase)
                .Select(bucket => new IngredientGroupViewModel(bucket.Key, bucket))
                .ToList();

            // Hide the PrimaryTag header when the step has a single group — the step header
            // already conveys the context and the inner bar becomes redundant.
            if (groups.Count == 1) groups[0].IsHeaderVisible = false;

            var label = BuildStepLabel(depthBucket.Key, stepNumber, stepCount);
            steps.Add(new CraftStepViewModel(stepNumber, label, groups));
            stepNumber++;
        }

        Rows = rows;
        Steps = steps;
        TotalItems = rows.Count;
        CraftReadyItems = rows.Count(r => r.IsCraftReady);

        var making = new ObservableCollection<CraftListItemViewModel>();
        foreach (var entry in _settings.CraftList.Where(e => e.Quantity > 0))
        {
            if (!_refData.RecipesByInternalName.TryGetValue(entry.RecipeInternalName, out var recipe)) continue;
            var ingredients = recipe.Ingredients
                .Select(r => _refData.Items.TryGetValue(r.ItemCode, out var item)
                    ? new IngredientChip(item.Name, item.IconId, r.StackSize, r.ChanceToConsume, item.InternalName)
                    : null)
                .Where(c => c is not null).Select(c => c!)
                .ToList();
            var results = recipe.ResultItems
                .Select(r => _refData.Items.TryGetValue(r.ItemCode, out var item)
                    ? new IngredientChip(item.Name, item.IconId, r.StackSize, null, item.InternalName)
                    : null)
                .Where(c => c is not null).Select(c => c!)
                .ToList();
            if (results.Count == 0)
            {
                results = (recipe.ProtoResultItems ?? [])
                    .Select(r => _refData.Items.TryGetValue(r.ItemCode, out var item)
                        ? new IngredientChip(item.Name, item.IconId, r.StackSize, null, item.InternalName)
                        : null)
                    .Where(c => c is not null).Select(c => c!)
                    .ToList();
            }
            var craftedOutputs = ResultEffectsParser.ParseCraftedGear(recipe.ResultEffects, _refData);
            var augments = ResultEffectsParser.ParseAugments(recipe.ResultEffects, _refData);
            if (results.Count == 0 && craftedOutputs.Count == 0 && augments.Count == 0)
                results = [new IngredientChip(recipe.Name, recipe.IconId, 1, null)];
            making.Add(new CraftListItemViewModel(
                recipe.InternalName,
                recipe.Name,
                recipe.IconId,
                entry.Quantity,
                recipe.Skill,
                recipe.SkillLevelReq,
                ingredients,
                results,
                craftedOutputs,
                augments,
                _itemDetail));
        }
        MakingItems = making;

        HeaderStatus = _activeChar.ActiveCharacter is null
            ? "No active character — on-hand counts unavailable."
            : $"On-hand from {_activeChar.ActiveCharacterName}'s latest export.";
    }

    private static string BuildStepLabel(int depth, int stepNumber, int totalSteps)
    {
        // depth 0 is always the raw-materials step. The last step (deepest depth
        // present) is the intermediates fed straight into the user's target
        // recipes — label it "Ready to craft" so the user knows what follows.
        if (depth == 0) return $"Step {stepNumber} · Raw materials";
        if (stepNumber == totalSteps && totalSteps > 1) return $"Step {stepNumber} · Ready to craft";
        return $"Step {stepNumber} · Intermediate crafts";
    }

    private void OnOverrideChanged(string itemInternalName, int? quantity)
    {
        var list = _settings.OnHandOverrides
            .Where(o => !string.Equals(o.ItemInternalName, itemInternalName, StringComparison.Ordinal))
            .ToList();
        if (quantity is int q && q >= 0)
            list.Add(new ManualOnHandOverride { ItemInternalName = itemInternalName, Quantity = q });

        _settings.OnHandOverrides = list;
        _settings.Touch(nameof(CelebrimborSettings.OnHandOverrides));

        // Rebuild rather than a local recount: an override on an intermediate changes
        // its shortfall, which changes the raw ingredients the aggregator produces.
        // The TextBox commits via LostFocus so focus has already moved on — no flicker.
        Rebuild();
    }

    private void OnSettingsChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(CelebrimborSettings.ExpansionDepth):
                OnPropertyChanged(nameof(ExpansionDepth));
                Rebuild();
                break;
            case nameof(CelebrimborSettings.TooltipDelayMs):
                OnPropertyChanged(nameof(TooltipDelayMs));
                break;
        }
    }

    private static void DispatchOnUi(Action action)
    {
        var d = System.Windows.Application.Current?.Dispatcher;
        if (d is null || d.CheckAccess()) action();
        else d.InvokeAsync(action);
    }
}
