using System.Collections.ObjectModel;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Samwise.Config;
using Samwise.State;

namespace Samwise.ViewModels;

public sealed partial class GardenViewModel : ObservableObject
{
    private readonly GardenStateMachine _state;
    private readonly ICropConfigStore _config;
    private readonly DispatcherTimer _refreshTimer;
    private readonly Dictionary<string, PlotViewModel> _plotVms = new(StringComparer.Ordinal);

    public GardenViewModel(GardenStateMachine state, ICropConfigStore config)
    {
        _state = state;
        _config = config;
        _state.PlotChanged += OnPlotChanged;
        _refreshTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _refreshTimer.Tick += (_, __) => Tick();
        _refreshTimer.Start();
        SyncFromState();
    }

    public ObservableCollection<PlotViewModel> Plots { get; } = new();

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

    [RelayCommand]
    private void DeletePlot(PlotViewModel? vm)
    {
        if (vm is null) return;
        if (_state.DeletePlot(vm.CharName, vm.PlotId))
            SyncFromState();
    }

    [RelayCommand]
    private void ClearHarvested()
    {
        _state.ClearHarvested();
        SyncFromState();
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

    private int _tickCount;
    private void Tick()
    {
        foreach (var vm in Plots) vm.Refresh();
        if (++_tickCount % 60 == 0)
        {
            var before = _plotVms.Count;
            _state.PruneWithered();
            if (_state.Snapshot().Sum(kv => kv.Value.Count) != before) SyncFromState();
        }
    }
}
