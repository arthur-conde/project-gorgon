using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows.Data;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Mithril.Shared.Diagnostics;

namespace Mithril.Shell.ViewModels;

public sealed partial class CategoryToggle : ObservableObject
{
    public CategoryToggle(string name, Action onChanged)
    {
        Name = name;
        _onChanged = onChanged;
    }

    public string Name { get; }
    private readonly Action _onChanged;
    [ObservableProperty] private bool _isEnabled = true;

    partial void OnIsEnabledChanged(bool value) => _onChanged();
}

public sealed partial class DiagnosticsViewModel : ObservableObject
{
    private readonly IDiagnosticsSink _sink;
    private readonly Dictionary<string, CategoryToggle> _categoryIndex = new(StringComparer.Ordinal);

    public DiagnosticsViewModel(IDiagnosticsSink sink)
    {
        _sink = sink;
        foreach (var e in _sink.Snapshot()) { Entries.Add(e); TrackCategory(e.Category); }
        _sink.EntryAdded += OnEntryAdded;
        View = (ListCollectionView)CollectionViewSource.GetDefaultView(Entries);
        View.Filter = Filter;
    }

    public ObservableCollection<DiagnosticEntry> Entries { get; } = new();
    public ObservableCollection<CategoryToggle> Categories { get; } = new();
    public ListCollectionView View { get; }

    [ObservableProperty] private bool _paused;
    [ObservableProperty] private bool _autoScroll = true;
    [ObservableProperty] private string _filterText = "";
    [ObservableProperty] private bool _showTrace = true;
    [ObservableProperty] private bool _showInfo = true;
    [ObservableProperty] private bool _showWarn = true;
    [ObservableProperty] private bool _showError = true;

    partial void OnFilterTextChanged(string value) => View.Refresh();
    partial void OnShowTraceChanged(bool value) => View.Refresh();
    partial void OnShowInfoChanged(bool value) => View.Refresh();
    partial void OnShowWarnChanged(bool value) => View.Refresh();
    partial void OnShowErrorChanged(bool value) => View.Refresh();

    private bool Filter(object o)
    {
        var e = (DiagnosticEntry)o;
        var levelOk = e.Level switch
        {
            DiagnosticLevel.Trace => ShowTrace,
            DiagnosticLevel.Info => ShowInfo,
            DiagnosticLevel.Warn => ShowWarn,
            DiagnosticLevel.Error => ShowError,
            _ => true,
        };
        if (!levelOk) return false;
        if (_categoryIndex.TryGetValue(e.Category, out var toggle) && !toggle.IsEnabled) return false;
        if (string.IsNullOrWhiteSpace(FilterText)) return true;
        return e.Category.Contains(FilterText, StringComparison.OrdinalIgnoreCase)
            || e.Message.Contains(FilterText, StringComparison.OrdinalIgnoreCase);
    }

    [RelayCommand]
    private void Clear() => Entries.Clear();

    [RelayCommand]
    private void CopyAll()
    {
        var text = string.Join('\n', View.OfType<DiagnosticEntry>()
            .Select(e => $"{e.Timestamp:HH:mm:ss.fff} [{e.Level}] {e.Category}: {e.Message}"));
        try { System.Windows.Clipboard.SetText(text); } catch { }
    }

    [RelayCommand]
    private void CategoriesAll()
    {
        foreach (var c in Categories) c.IsEnabled = true;
    }

    [RelayCommand]
    private void CategoriesNone()
    {
        foreach (var c in Categories) c.IsEnabled = false;
    }

    [RelayCommand]
    private void CategoriesOnly(CategoryToggle? only)
    {
        if (only is null) return;
        foreach (var c in Categories) c.IsEnabled = ReferenceEquals(c, only);
    }

    private void TrackCategory(string name)
    {
        if (_categoryIndex.ContainsKey(name)) return;
        var toggle = new CategoryToggle(name, () => View.Refresh());
        _categoryIndex[name] = toggle;
        Categories.Add(toggle);
    }

    private void OnEntryAdded(object? sender, DiagnosticEntry e)
    {
        if (Paused) return;
        var d = System.Windows.Application.Current?.Dispatcher;
        if (d is null || d.CheckAccess()) Append(e);
        else d.InvokeAsync(() => Append(e), DispatcherPriority.Background);
    }

    private void Append(DiagnosticEntry e)
    {
        TrackCategory(e.Category);
        Entries.Add(e);
        while (Entries.Count > 2000) Entries.RemoveAt(0);
    }
}
