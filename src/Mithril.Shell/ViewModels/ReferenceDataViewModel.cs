using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Mithril.Shared.Reference;

namespace Mithril.Shell.ViewModels;

public sealed partial class ReferenceFileViewModel : ObservableObject
{
    private readonly IReferenceDataService _service;

    public ReferenceFileViewModel(IReferenceDataService service, string key)
    {
        _service = service;
        Key = key;
        Refresh();
    }

    public string Key { get; }

    [ObservableProperty] private string _source = "";
    [ObservableProperty] private string _cdnVersion = "";
    [ObservableProperty] private string _fetchedAt = "";
    [ObservableProperty] private int _entryCount;
    [ObservableProperty] private bool _isRefreshing;
    [ObservableProperty] private string? _lastError;

    public void Refresh()
    {
        var snap = _service.GetSnapshot(Key);
        Source = snap.Source.ToString();
        CdnVersion = snap.CdnVersion;
        FetchedAt = snap.FetchedAtUtc?.ToLocalTime().ToString("yyyy-MM-dd HH:mm") ?? "—";
        EntryCount = snap.EntryCount;
    }

    [RelayCommand]
    private async Task RefreshFromCdnAsync()
    {
        if (IsRefreshing) return;
        IsRefreshing = true;
        LastError = null;
        try
        {
            await _service.RefreshAsync(Key);
            Refresh();
        }
        catch (Exception ex)
        {
            LastError = ex.Message;
        }
        finally
        {
            IsRefreshing = false;
        }
    }
}

public sealed partial class ReferenceDataViewModel : ObservableObject
{
    private readonly IReferenceDataService _service;

    public ReferenceDataViewModel(IReferenceDataService service)
    {
        _service = service;
        foreach (var key in service.Keys)
            Files.Add(new ReferenceFileViewModel(service, key));

        service.FileUpdated += (_, key) =>
        {
            var match = Files.FirstOrDefault(f => f.Key == key);
            match?.Refresh();
        };
    }

    public ObservableCollection<ReferenceFileViewModel> Files { get; } = new();

    [ObservableProperty] private bool _isRefreshing;

    [RelayCommand]
    private async Task RefreshAllAsync()
    {
        if (IsRefreshing) return;
        IsRefreshing = true;
        try
        {
            await _service.RefreshAllAsync();
            foreach (var f in Files) f.Refresh();
        }
        finally
        {
            IsRefreshing = false;
        }
    }
}
