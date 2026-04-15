using System.Collections.ObjectModel;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Samwise.Alarms;
using Samwise.Config;
using Samwise.State;

namespace Samwise.ViewModels;

public sealed partial class GardenViewModel : ObservableObject
{
    private readonly GardenStateMachine _state;
    private readonly ICropConfigStore _config;
    private readonly SamwiseSettings _settings;
    private readonly DispatcherTimer _refreshTimer;
    private readonly Dictionary<string, PlotViewModel> _plotVms = new(StringComparer.Ordinal);

    public GardenViewModel(GardenStateMachine state, ICropConfigStore config, SamwiseSettings settings)
    {
        _state = state;
        _config = config;
        _settings = settings;
        _state.PlotChanged += OnPlotChanged;
        _refreshTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _refreshTimer.Tick += (_, __) => Tick();
        _refreshTimer.Start();
        SyncFromState();
    }

    public ObservableCollection<PlotViewModel> Plots { get; } = new();

    [ObservableProperty] private string _statusText = "";
    [ObservableProperty] private bool _sessionActive;

    [RelayCommand]
    private void StartSession()
    {
        SessionActive = true;
        _state.SessionActive = true;
        _settings.SessionActive = true;
        StatusText = "Listening for garden events…";
    }

    [RelayCommand]
    private void StopSession()
    {
        SessionActive = false;
        _state.SessionActive = false;
        _settings.SessionActive = false;
        StatusText = "Session stopped.";
    }

    [RelayCommand]
    private void MarkHarvested(PlotViewModel? vm)
    {
        if (vm is null) return;
        var snap = _state.Snapshot();
        if (!snap.TryGetValue(vm.CharName, out var plots)) return;
        if (!plots.TryGetValue(vm.PlotId, out var plot)) return;
        plot.Stage = PlotStage.Harvested;
        plot.UpdatedAt = DateTimeOffset.UtcNow;
        vm.Refresh();
    }

    private void OnPlotChanged(object? sender, PlotChangedArgs e)
    {
        var key = $"{e.Plot.CharName}|{e.Plot.PlotId}";
        if (!_plotVms.TryGetValue(key, out var vm))
        {
            vm = new PlotViewModel(e.Plot, _config);
            _plotVms[key] = vm;
            Plots.Add(vm);
        }
        else
        {
            vm.Refresh();
        }
    }

    private void SyncFromState()
    {
        Plots.Clear();
        _plotVms.Clear();
        SessionActive = _state.SessionActive;
        foreach (var (charName, plots) in _state.Snapshot())
        {
            foreach (var (id, p) in plots)
            {
                var vm = new PlotViewModel(p, _config);
                _plotVms[$"{charName}|{id}"] = vm;
                Plots.Add(vm);
            }
        }
    }

    private void Tick()
    {
        foreach (var vm in Plots) vm.Refresh();
    }
}
