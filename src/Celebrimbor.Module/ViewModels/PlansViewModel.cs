using System.Collections.ObjectModel;
using System.IO;
using System.Text.Json;
using Celebrimbor.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Mithril.Planning;
using Mithril.Shared.Character;
using Mithril.Shared.Modules;
using Mithril.Shared.Reference;

namespace Celebrimbor.ViewModels;

/// <summary>
/// The Plans manager + host for the walker (#228 PR-B/B1, frames 01–02).
/// A peer area to the crafting wizard — not a wizard step: an independent,
/// id-keyed library of <see cref="SavedLevelingPlan"/> artifacts the user
/// walks. Plans arrive from outside (Elrond hand-off / file import) and feed
/// back into the wizard via the walker's "Send phase to craft list".
///
/// <para>Headless: file-pick and delete-confirm are injectable seams so the
/// VM is unit-testable without WPF dialogs.</para>
/// </summary>
public sealed partial class PlansViewModel : ObservableObject
{
    private readonly LevelingPlanStore _store;
    private readonly PlanExecutor _executor;
    private readonly OnHandInventoryQuery _onHand;
    private readonly IActiveCharacterService _activeChar;
    private readonly IReferenceDataService? _referenceData;
    private readonly IModuleActivator? _activator;
    private readonly Func<string?> _pickPlanFile;
    private readonly Func<SavedPlanRowViewModel, bool> _confirmDelete;

    public PlansViewModel(
        LevelingPlanStore store,
        PlanExecutor executor,
        OnHandInventoryQuery onHand,
        IActiveCharacterService activeChar,
        PlanWalkerViewModel walker,
        IReferenceDataService? referenceData = null,
        IModuleActivator? activator = null,
        Func<string?>? pickPlanFile = null,
        Func<SavedPlanRowViewModel, bool>? confirmDelete = null)
    {
        _store = store;
        _executor = executor;
        _onHand = onHand;
        _activeChar = activeChar;
        _referenceData = referenceData;
        _activator = activator;
        _pickPlanFile = pickPlanFile ?? DefaultPickPlanFile;
        _confirmDelete = confirmDelete ?? DefaultConfirmDelete;
        Walker = walker;

        Walker.BackRequested += (_, _) =>
        {
            IsWalking = false;
            Reload();
        };
        Walker.PlanChanged += (_, _) => Reload();

        _activeChar.ActiveCharacterChanged += (_, _) => DispatchOnUi(Reload);
        _activeChar.CharacterExportsChanged += (_, _) => DispatchOnUi(Reload);

        Reload();
    }

    public PlanWalkerViewModel Walker { get; }

    [ObservableProperty]
    private ObservableCollection<SavedPlanRowViewModel> _rows = [];

    [ObservableProperty]
    private SavedPlanRowViewModel? _selectedRow;

    [ObservableProperty]
    private string _searchText = "";

    [ObservableProperty]
    private PlanFilter _activeFilter = PlanFilter.All;

    /// <summary>True while the walker sub-view is showing instead of the library list.</summary>
    [ObservableProperty]
    private bool _isWalking;

    public bool IsEmpty => _store.All().Count == 0;

    public int AllCount { get; private set; }
    public int InProgressCount { get; private set; }
    public int StaleCount { get; private set; }
    public int DoneCount { get; private set; }

    partial void OnSearchTextChanged(string value) => ApplyView();
    partial void OnActiveFilterChanged(PlanFilter value) => ApplyView();

    /// <summary>Rebuild every row from the store (staleness vs the live character).</summary>
    public void Reload()
    {
        var live = _activeChar.ActiveCharacter;
        _allRows = _store.All()
            .OrderByDescending(p => p.CreatedAt)
            .Select(p => new SavedPlanRowViewModel(p, IsStale(p, live), SkillDisplay(p.Skill)))
            .ToList();

        AllCount = _allRows.Count;
        InProgressCount = _allRows.Count(r => r.Status == SavedPlanStatus.InProgress);
        StaleCount = _allRows.Count(r => r.Status == SavedPlanStatus.Stale);
        DoneCount = _allRows.Count(r => r.Status == SavedPlanStatus.Done);
        OnPropertyChanged(nameof(AllCount));
        OnPropertyChanged(nameof(InProgressCount));
        OnPropertyChanged(nameof(StaleCount));
        OnPropertyChanged(nameof(DoneCount));
        OnPropertyChanged(nameof(IsEmpty));

        ApplyView();
    }

    private List<SavedPlanRowViewModel> _allRows = [];

    private void ApplyView()
    {
        var filtered = _allRows
            .Where(r => r.MatchesFilter(ActiveFilter) && r.MatchesSearch(SearchText))
            .ToList();
        Rows = new ObservableCollection<SavedPlanRowViewModel>(filtered);
        if (SelectedRow is null || filtered.All(r => !string.Equals(r.Id, SelectedRow.Id, StringComparison.Ordinal)))
            SelectedRow = filtered.FirstOrDefault();
    }

    private static bool IsStale(SavedLevelingPlan plan, CharacterSnapshot? live)
        => live is not null && plan.IsInitialStateStaleAgainst(live);

    /// <summary>
    /// Resolve a skill's id-shaped key to its human display name (convention:
    /// reverse-lookup internalName→display where reference data allows; the
    /// persisted plan still stores the key). Falls back to the key.
    /// </summary>
    private string SkillDisplay(string skillKey)
        => _referenceData is not null && _referenceData.Skills.TryGetValue(skillKey, out var e)
            ? e.DisplayName : skillKey;

    [RelayCommand]
    private void SetFilter(PlanFilter filter) => ActiveFilter = filter;

    [RelayCommand]
    private void OpenWalker(SavedPlanRowViewModel? row)
    {
        var target = row ?? SelectedRow;
        if (target is null) return;
        Walker.Load(target.Plan);
        IsWalking = true;
    }

    [RelayCommand]
    private void Replan(SavedPlanRowViewModel? row)
    {
        var target = row ?? SelectedRow;
        if (target is null) return;
        var refreshed = _executor.Replan(target.Plan, OnHandCounts());
        if (refreshed is null) return; // no viable path — leave the old plan untouched
        _store.Upsert(refreshed);
        Reload();
    }

    [RelayCommand]
    private void Delete(SavedPlanRowViewModel? row)
    {
        var target = row ?? SelectedRow;
        if (target is null || !_confirmDelete(target)) return;
        _store.Delete(target.Id);
        Reload();
    }

    [RelayCommand]
    private void ImportPlanFromFile()
    {
        var path = _pickPlanFile();
        if (string.IsNullOrEmpty(path) || !File.Exists(path)) return;

        SavedLevelingPlan? plan;
        try
        {
            plan = JsonSerializer.Deserialize(File.ReadAllText(path),
                SavedLevelingPlanJsonContext.Default.SavedLevelingPlan);
        }
        catch (Exception ex) when (ex is JsonException or IOException)
        {
            return;
        }
        if (plan is null || plan.Phases.Count == 0) return;

        _store.Upsert(plan);
        Reload();
        SelectedRow = Rows.FirstOrDefault(r => string.Equals(r.Id, plan.Id, StringComparison.Ordinal));
    }

    /// <summary>Empty-state CTA: the plan source is Elrond (the advisor), not the Picker.</summary>
    [RelayCommand]
    private void OpenPlanSource() => _activator?.Activate("elrond");

    /// <summary>
    /// Called by the import target after a hand-off lands: refresh the library
    /// and select the new plan so the user sees it arrive (stay in the manager,
    /// per spec frames 01–02 — the plan "lands here").
    /// </summary>
    public void SurfaceImported(string planId)
    {
        IsWalking = false;
        Reload();
        SelectedRow = Rows.FirstOrDefault(r => string.Equals(r.Id, planId, StringComparison.Ordinal));
    }

    private IReadOnlyDictionary<string, int> OnHandCounts()
        => _onHand.QueryActiveCharacter().Counts;

    private static string? DefaultPickPlanFile()
    {
        var dlg = new Microsoft.Win32.OpenFileDialog
        {
            Title = "Import leveling plan",
            Filter = "Mithril plan (*.plan;*.json)|*.plan;*.json|All files (*.*)|*.*",
            CheckFileExists = true,
        };
        return dlg.ShowDialog() == true ? dlg.FileName : null;
    }

    private static bool DefaultConfirmDelete(SavedPlanRowViewModel row)
        => System.Windows.MessageBox.Show(
               $"Delete the plan \"{row.Title}\"? This cannot be undone.",
               "Delete plan", System.Windows.MessageBoxButton.OKCancel,
               System.Windows.MessageBoxImage.Warning) == System.Windows.MessageBoxResult.OK;

    private static void DispatchOnUi(Action action)
    {
        var d = System.Windows.Application.Current?.Dispatcher;
        if (d is null || d.CheckAccess()) action();
        else d.InvokeAsync(action);
    }
}
