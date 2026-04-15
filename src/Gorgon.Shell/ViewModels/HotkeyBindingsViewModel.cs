using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows.Data;
using CommunityToolkit.Mvvm.ComponentModel;
using Gorgon.Shared.Hotkeys;
using Gorgon.Shared.Hotkeys.Controls;

namespace Gorgon.Shell.ViewModels;

public sealed partial class HotkeyRowViewModel : HotkeyRowViewModelBase, INotifyPropertyChanged
{
    private readonly ShellSettings _settings;
    private readonly Func<IEnumerable<HotkeyBinding>> _allBindings;
    private readonly IHotkeyService _service;
    private HotkeyBinding? _binding;
    private string? _conflictWith;
    private string? _error;

    public HotkeyRowViewModel(IHotkeyCommand command, ShellSettings settings, IHotkeyService service, Func<IEnumerable<HotkeyBinding>> allBindings)
    {
        Command = command;
        _settings = settings;
        _service = service;
        _allBindings = allBindings;
        _binding = settings.HotkeyBindings.GetValueOrDefault(command.Id) ?? command.DefaultBinding;
    }

    public IHotkeyCommand Command { get; }
    public override string CommandId => Command.Id;
    public string DisplayName => Command.DisplayName;
    public string Category => Command.Category ?? "(General)";

    public HotkeyBinding? Binding
    {
        get => _binding;
        set { _binding = value; OnPropertyChanged(nameof(Binding)); }
    }

    public string? ConflictWith
    {
        get => _conflictWith;
        set { _conflictWith = value; OnPropertyChanged(nameof(ConflictWith)); OnPropertyChanged(nameof(HasConflict)); }
    }

    public bool HasConflict => _conflictWith is not null;

    public string? Error
    {
        get => _error;
        set { _error = value; OnPropertyChanged(nameof(Error)); OnPropertyChanged(nameof(HasError)); }
    }

    public bool HasError => _error is not null;

    public void Commit(HotkeyBinding? newBinding)
    {
        if (newBinding is null) _settings.HotkeyBindings.Remove(Command.Id);
        else _settings.HotkeyBindings[Command.Id] = newBinding with { CommandId = Command.Id };
        Binding = newBinding is null ? null : newBinding with { CommandId = Command.Id };
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnPropertyChanged(string n) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(n));
}

public sealed partial class HotkeyBindingsViewModel : ObservableObject
{
    private readonly ShellSettings _settings;
    private readonly IHotkeyService _service;
    private readonly HotkeyRegistry _registry;

    public HotkeyBindingsViewModel(ShellSettings settings, IHotkeyService service, HotkeyRegistry registry)
    {
        _settings = settings;
        _service = service;
        _registry = registry;
        BuildRows();
        RowsView = (ListCollectionView)CollectionViewSource.GetDefaultView(Rows);
        RowsView.GroupDescriptions.Add(new PropertyGroupDescription(nameof(HotkeyRowViewModel.Category)));
        RowsView.SortDescriptions.Add(new SortDescription(nameof(HotkeyRowViewModel.Category), ListSortDirection.Ascending));
        RowsView.SortDescriptions.Add(new SortDescription(nameof(HotkeyRowViewModel.DisplayName), ListSortDirection.Ascending));
        RowsView.Filter = FilterRow;
        RecheckConflicts();
    }

    public ObservableCollection<HotkeyRowViewModel> Rows { get; } = new();
    public ListCollectionView RowsView { get; }

    [ObservableProperty] private string _searchText = "";

    public IHotkeyService HotkeyService => _service;

    partial void OnSearchTextChanged(string value) => RowsView.Refresh();

    private bool FilterRow(object o)
    {
        if (string.IsNullOrWhiteSpace(SearchText)) return true;
        var r = (HotkeyRowViewModel)o;
        var q = SearchText.Trim();
        if (r.DisplayName.Contains(q, StringComparison.OrdinalIgnoreCase)) return true;
        if (r.Category.Contains(q, StringComparison.OrdinalIgnoreCase)) return true;
        if (r.Binding is { } b && HotkeyChipControl.Format(b).Contains(q, StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    private void BuildRows()
    {
        Rows.Clear();
        foreach (var cmd in _registry.Commands)
        {
            var row = new HotkeyRowViewModel(cmd, _settings, _service, () => Rows.Where(r => r.Binding is not null).Select(r => r.Binding!));
            row.PropertyChanged += OnRowPropertyChanged;
            Rows.Add(row);
        }
    }

    private void OnRowPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(HotkeyRowViewModel.Binding) && sender is HotkeyRowViewModel row)
        {
            // Push to settings + reapply globally
            if (row.Binding is null) _settings.HotkeyBindings.Remove(row.Command.Id);
            else _settings.HotkeyBindings[row.Command.Id] = row.Binding with { CommandId = row.Command.Id };
            ApplyAndRecheck();
        }
    }

    public void OnRowCommitted(HotkeyRowViewModel row, HotkeyBinding? newBinding)
    {
        row.Commit(newBinding);
        ApplyAndRecheck();
    }

    private void ApplyAndRecheck()
    {
        var report = _service.ReloadFromBindings(Rows.Where(r => r.Binding is not null).Select(r => r.Binding!));
        foreach (var row in Rows)
            row.Error = report.TryGetValue(row.Command.Id, out var e) ? e : null;
        RecheckConflicts();
    }

    private void RecheckConflicts()
    {
        var bindings = Rows.Where(r => r.Binding is not null).Select(r => r.Binding!).ToList();
        var conflicts = HotkeyConflictDetector.Detect(bindings);
        var nameById = Rows.ToDictionary(r => r.Command.Id, r => r.DisplayName);
        foreach (var row in Rows)
            row.ConflictWith = conflicts.TryGetValue(row.Command.Id, out var c) && nameById.TryGetValue(c.ConflictingCommandId, out var n) ? n : null;
    }
}
