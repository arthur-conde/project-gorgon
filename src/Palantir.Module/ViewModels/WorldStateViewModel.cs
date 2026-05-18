using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Mithril.GameState.Areas;
using Mithril.GameState.Movement;
using Mithril.Shared.Reference;

namespace Palantir.ViewModels;

/// <summary>
/// Debug surface over <b>Mithril.GameState</b>'s live map + position state:
/// the current area (<see cref="PlayerAreaTracker"/>) resolved through
/// <c>areas.json</c> (<see cref="IReferenceDataService.Areas"/>) to its
/// friendly names, plus the player's last-known <see cref="PlayerPosition"/>
/// (coords + the UTC instant it was measured) from
/// <see cref="IPlayerPositionTracker"/>.
///
/// <para>Position is event-driven (sparse — teleport / zone-in only). The
/// area tracker exposes no change event, so it is re-read on every position
/// update (area changes coincide with the teleport that emits a position)
/// and on the manual <see cref="RefreshCommand"/>.</para>
/// </summary>
public sealed partial class WorldStateViewModel : ObservableObject, IDisposable
{
    private readonly IPlayerPositionTracker _positionTracker;
    private readonly PlayerAreaTracker _areaTracker;
    private readonly IReferenceDataService? _refData;
    private readonly Action<Action> _dispatch;
    private IDisposable? _subscription;

    [ObservableProperty] private string _areaKey = "(unknown)";
    [ObservableProperty] private string _areaFriendlyName = "(area not yet known)";
    [ObservableProperty] private string _areaShortName = "";
    [ObservableProperty] private bool _areaResolved;

    [ObservableProperty] private bool _hasPosition;
    [ObservableProperty] private string _positionText = "(no position observed yet)";
    [ObservableProperty] private string _measuredAtText = "—";
    [ObservableProperty] private string _positionSourceText = "—";

    public WorldStateViewModel(
        IPlayerPositionTracker positionTracker,
        PlayerAreaTracker areaTracker,
        IReferenceDataService? refData = null)
        : this(positionTracker, areaTracker, refData, dispatch: null)
    { }

    /// <summary>
    /// Test-friendly ctor: inject a synchronous dispatcher so unit tests
    /// don't need an STA Application running.
    /// </summary>
    public WorldStateViewModel(
        IPlayerPositionTracker positionTracker,
        PlayerAreaTracker areaTracker,
        IReferenceDataService? refData,
        Action<Action>? dispatch)
    {
        _positionTracker = positionTracker;
        _areaTracker = areaTracker;
        _refData = refData;
        _dispatch = dispatch ?? DefaultDispatch;

        RefreshArea();
        // Replay-on-subscribe seeds position if one is already known.
        _subscription = _positionTracker.Subscribe(OnPosition);
    }

    private void OnPosition(PlayerPosition p) => _dispatch(() =>
    {
        HasPosition = true;
        PositionText = string.Format(
            CultureInfo.InvariantCulture, "X {0:0.00}   Y {1:0.00}   Z {2:0.00}", p.X, p.Y, p.Z);
        MeasuredAtText = p.MeasuredAt.UtcDateTime.ToString("u", CultureInfo.InvariantCulture);
        PositionSourceText = p.Source switch
        {
            PlayerPositionSource.Spawn => "Spawn / zone-in (ProcessAddPlayer)",
            PlayerPositionSource.Movement => "Movement / teleport (ProcessNewPosition)",
            _ => p.Source.ToString(),
        };
        // A new position implies a possible zone change — re-resolve the area.
        RefreshArea();
    });

    [RelayCommand]
    private void Refresh() => _dispatch(RefreshArea);

    private void RefreshArea()
    {
        var key = _areaTracker.CurrentArea;
        if (string.IsNullOrEmpty(key))
        {
            AreaKey = "(none)";
            AreaFriendlyName = "(not in a game area)";
            AreaShortName = "";
            AreaResolved = false;
            return;
        }

        AreaKey = key;
        if (_refData is not null && _refData.Areas.TryGetValue(key, out var entry))
        {
            AreaFriendlyName = entry.FriendlyName;
            AreaShortName = string.Equals(entry.ShortFriendlyName, entry.FriendlyName, StringComparison.Ordinal)
                ? ""
                : entry.ShortFriendlyName;
            AreaResolved = true;
        }
        else
        {
            // Key known but areas.json hasn't loaded it (offline / stale bundle).
            AreaFriendlyName = key;
            AreaShortName = "";
            AreaResolved = false;
        }
    }

    public void Dispose()
    {
        _subscription?.Dispose();
        _subscription = null;
    }

    private static void DefaultDispatch(Action action)
    {
        var d = System.Windows.Application.Current?.Dispatcher;
        if (d is null || d.CheckAccess()) action();
        else d.InvokeAsync(action);
    }
}
