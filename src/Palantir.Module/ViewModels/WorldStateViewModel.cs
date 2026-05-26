using System.Collections.ObjectModel;
using System.Globalization;
using Arda.Dispatch;
using Arda.World.Player;
using Arda.World.Player.Events;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Mithril.Shared.Reference;

namespace Palantir.ViewModels;

/// <summary>
/// Debug surface over Arda's live world state: position, area, map pins,
/// celestial (moon phase), and weather. State is read from Arda state
/// interfaces (<see cref="IPositionState"/>, <see cref="IAreaState"/>, etc.)
/// and kept current via domain event subscriptions through
/// <see cref="IDomainEventSubscriber"/>.
///
/// <para>All event handlers fire on the Arda dispatch thread, so every handler
/// marshals through <see cref="_dispatch"/> before touching bound state.</para>
/// </summary>
public sealed partial class WorldStateViewModel : ObservableObject, IDisposable
{
    private readonly IPositionState _position;
    private readonly IAreaState _area;
    private readonly IMapPinState _pinState;
    private readonly ICelestialState _celestial;
    private readonly IWeatherState _weather;
    private readonly IReferenceDataService? _refData;
    private readonly Action<Action> _dispatch;

    private IDisposable? _positionSub;
    private IDisposable? _areaSub;
    private IDisposable? _pinAddedSub;
    private IDisposable? _pinRemovedSub;
    private IDisposable? _celestialSub;
    private IDisposable? _weatherSub;

    [ObservableProperty] private string _areaKey = "(unknown)";
    [ObservableProperty] private string _areaFriendlyName = "(area not yet known)";
    [ObservableProperty] private string _areaShortName = "";
    [ObservableProperty] private bool _areaResolved;

    [ObservableProperty] private bool _hasPosition;
    [ObservableProperty] private string _positionText = "(no position observed yet)";
    [ObservableProperty] private string _measuredAtText = "—";
    [ObservableProperty] private string _positionSourceText = "—";

    [ObservableProperty] private int _pinCount;
    [ObservableProperty] private bool _hasPins;
    [ObservableProperty] private string _pinsObservedAtText = "—";

    [ObservableProperty] private bool _hasMoonPhase;
    [ObservableProperty] private string _moonPhaseText = "(no celestial info observed yet)";
    [ObservableProperty] private string _moonPhaseRawText = "—";
    [ObservableProperty] private string _moonMeasuredAtText = "—";

    [ObservableProperty] private bool _hasWeather;
    [ObservableProperty] private string _weatherConditionText = "(weather unknown for this map)";
    [ObservableProperty] private string _weatherObservedAtText = "—";

    /// <summary>The current area's pins as presentation rows. Mutated only
    /// on the dispatched (UI) thread.</summary>
    public ObservableCollection<MapPinRow> Pins { get; } = [];

    public WorldStateViewModel(
        IPositionState position,
        IAreaState area,
        IMapPinState pins,
        ICelestialState celestial,
        IWeatherState weather,
        IDomainEventSubscriber bus,
        IReferenceDataService? refData = null)
        : this(position, area, pins, celestial, weather, bus, refData, dispatch: null)
    { }

    /// <summary>
    /// Test-friendly ctor: inject a synchronous dispatcher so unit tests
    /// don't need an STA Application running.
    /// </summary>
    public WorldStateViewModel(
        IPositionState position,
        IAreaState area,
        IMapPinState pins,
        ICelestialState celestial,
        IWeatherState weather,
        IDomainEventSubscriber bus,
        IReferenceDataService? refData,
        Action<Action>? dispatch)
    {
        _position = position;
        _area = area;
        _pinState = pins;
        _celestial = celestial;
        _weather = weather;
        _refData = refData;
        _dispatch = dispatch ?? DefaultDispatch;

        SeedFromState();

        _positionSub = bus.Subscribe<PlayerPositionChanged>(OnPosition);
        _areaSub = bus.Subscribe<AreaChanged>(OnAreaChanged);
        _pinAddedSub = bus.Subscribe<MapPinAdded>(OnPinAdded);
        _pinRemovedSub = bus.Subscribe<MapPinRemoved>(OnPinRemoved);
        _celestialSub = bus.Subscribe<CelestialInfoChanged>(OnCelestial);
        _weatherSub = bus.Subscribe<WeatherChanged>(OnWeather);
    }

    /// <summary>
    /// Reads current state from the Arda state interfaces to seed the UI
    /// (replay may already have run before the VM is constructed).
    /// </summary>
    private void SeedFromState()
    {
        RefreshArea();

        if (_position.X is not null)
        {
            HasPosition = true;
            PositionText = FormatPosition(_position.X.Value, _position.Y ?? 0, _position.Z ?? 0);
        }

        RefreshPins();

        if (_celestial.Phase != MoonPhase.Unknown || _celestial.CurrentPhaseRaw is not null)
        {
            HasMoonPhase = true;
            MoonPhaseText = _celestial.DisplayName ?? "(unknown phase)";
            MoonPhaseRawText = _celestial.Phase == MoonPhase.Unknown
                ? $"{_celestial.CurrentPhaseRaw} (unrecognised token)"
                : _celestial.CurrentPhaseRaw ?? "—";
            MoonMeasuredAtText = FormatTimestamp(_celestial.MeasuredAt);
        }

        if (_weather.CurrentWeather is { } w)
        {
            HasWeather = true;
            WeatherConditionText = w;
        }
    }

    private void OnPosition(PlayerPositionChanged e) => _dispatch(() =>
    {
        HasPosition = true;
        PositionText = FormatPosition(e.X, e.Y, e.Z);
        MeasuredAtText = FormatTimestamp(e.Metadata.Timestamp);
        PositionSourceText = e.Source switch
        {
            PositionSource.Spawn => "Spawn / zone-in (ProcessAddPlayer)",
            PositionSource.Movement => "Movement / teleport (ProcessNewPosition)",
            _ => e.Source.ToString(),
        };
        RefreshArea();
    });

    private void OnAreaChanged(AreaChanged e) => _dispatch(RefreshArea);

    private void OnPinAdded(MapPinAdded e) => _dispatch(() =>
    {
        PinsObservedAtText = FormatTimestamp(e.Metadata.Timestamp);
        RefreshPins();
    });

    private void OnPinRemoved(MapPinRemoved e) => _dispatch(() =>
    {
        PinsObservedAtText = FormatTimestamp(e.Metadata.Timestamp);
        RefreshPins();
    });

    private void OnCelestial(CelestialInfoChanged e) => _dispatch(() =>
    {
        HasMoonPhase = true;
        MoonPhaseText = e.DisplayName;
        MoonPhaseRawText = e.Phase == MoonPhase.Unknown
            ? $"{e.RawPhase} (unrecognised token)"
            : e.RawPhase;
        MoonMeasuredAtText = FormatTimestamp(e.Metadata.Timestamp);
    });

    private void OnWeather(WeatherChanged e) => _dispatch(() =>
    {
        HasWeather = e.Current is not null;
        WeatherConditionText = e.Current ?? "(weather unknown for this map)";
        WeatherObservedAtText = FormatTimestamp(e.Metadata.Timestamp);
    });

    [RelayCommand]
    private void Refresh() => _dispatch(() =>
    {
        RefreshArea();
        RefreshPins();
    });

    private void RefreshArea()
    {
        var key = _area.CurrentArea;
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
            AreaFriendlyName = key;
            AreaShortName = "";
            AreaResolved = false;
        }
    }

    /// <summary>
    /// Rebuilds the pin list from <see cref="IMapPinState.Pins"/>. The set is
    /// tiny (a handful per area), so clear-and-refill is simpler than diffing.
    /// </summary>
    private void RefreshPins()
    {
        Pins.Clear();
        foreach (var pin in _pinState.Pins)
            Pins.Add(MapPinRow.From(pin));
        PinCount = Pins.Count;
        HasPins = Pins.Count > 0;
    }

    public void Dispose()
    {
        _positionSub?.Dispose();
        _positionSub = null;
        _areaSub?.Dispose();
        _areaSub = null;
        _pinAddedSub?.Dispose();
        _pinAddedSub = null;
        _pinRemovedSub?.Dispose();
        _pinRemovedSub = null;
        _celestialSub?.Dispose();
        _celestialSub = null;
        _weatherSub?.Dispose();
        _weatherSub = null;
    }

    private static string FormatPosition(double x, double y, double z) =>
        string.Format(CultureInfo.InvariantCulture, "X {0:0.00}   Y {1:0.00}   Z {2:0.00}", x, y, z);

    private static string FormatTimestamp(DateTimeOffset? ts) =>
        ts?.UtcDateTime.ToString("u", CultureInfo.InvariantCulture) ?? "—";

    private static void DefaultDispatch(Action action)
    {
        var d = System.Windows.Application.Current?.Dispatcher;
        if (d is null || d.CheckAccess()) action();
        else d.InvokeAsync(action);
    }
}

/// <summary>
/// Presentation-only projection of a <see cref="MapPinEntry"/> for the World
/// State debug list — formatting lives here, not in the XAML.
/// </summary>
/// <param name="Label">The pin label.</param>
/// <param name="Appearance">Friendly shape+color description (e.g. "red dot").</param>
/// <param name="Coords">Signed engine-unit ground-plane coordinate.</param>
/// <param name="Detail">Raw shape/color values — debug-surface extra.</param>
public sealed record MapPinRow(string Label, string Appearance, string Coords, string Detail)
{
    public static MapPinRow From(MapPinEntry p) => new(
        string.IsNullOrEmpty(p.Label) ? "Unnamed pin" : p.Label,
        FormatAppearance(p.Shape, p.Color),
        string.Format(CultureInfo.InvariantCulture, "X {0:0.00}   Z {1:0.00}", p.X, p.Z),
        $"Color {p.Color} · Shape {p.Shape}");

    private static string FormatAppearance(int shape, int color)
    {
        var c = color switch
        {
            0 => "white", 1 => "red", 2 => "orange", 3 => "yellow",
            4 => "green", 5 => "cyan", 6 => "blue", 7 => "purple",
            8 => "pink", 9 => "black", _ => ""
        };
        var s = shape switch { 0 => "dot", 1 => "square", _ => "pin" };
        return string.IsNullOrEmpty(c) ? s : $"{c} {s}";
    }
}
