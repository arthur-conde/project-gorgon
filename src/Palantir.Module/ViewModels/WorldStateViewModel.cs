using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Mithril.GameState.Areas;
using Mithril.GameState.Celestial;
using Mithril.GameState.Movement;
using Mithril.GameState.Pins;
using Mithril.GameState.Weather;
using Mithril.Shared.Reference;

namespace Palantir.ViewModels;

/// <summary>
/// Debug surface over <b>Mithril.GameState</b>'s live map + position state:
/// the current area (<see cref="PlayerAreaTracker"/>) resolved through
/// <c>areas.json</c> (<see cref="IReferenceDataService.Areas"/>) to its
/// friendly names, the player's last-known <see cref="PlayerPosition"/>
/// (coords + the UTC instant it was measured) from
/// <see cref="IPlayerPositionTracker"/>, and the area-scoped player map-pin
/// set from <see cref="IPlayerPinTracker"/>, and the per-map ambient weather
/// from <see cref="IPlayerWeatherTracker"/>.
///
/// <para>Position is event-driven (sparse — teleport / zone-in only). The
/// area tracker exposes no change event, so it is re-read on every position
/// update (area changes coincide with the teleport that emits a position)
/// and on the manual <see cref="RefreshCommand"/>.</para>
///
/// <para>Pins are push-driven: <see cref="IPlayerPinTracker.Subscribe"/>
/// replays the current set synchronously then delivers add / remove / area
/// swap live (PG bulk-replays the set on every login / zone, idempotently).
/// All tracker subscriptions fire on the GameState ingestion thread, so
/// every handler marshals through <see cref="_dispatch"/> before touching
/// bound state.</para>
///
/// <para>The lunar phase (<see cref="IPlayerCelestialState"/>) is push-driven
/// too — replayed on subscribe, then delivered live on every phase
/// roll-over.</para>
/// </summary>
public sealed partial class WorldStateViewModel : ObservableObject, IDisposable
{
    private readonly IPlayerPositionTracker _positionTracker;
    private readonly PlayerAreaTracker _areaTracker;
    private readonly IPlayerPinTracker _pinTracker;
    private readonly IPlayerCelestialState _celestial;
    private readonly IPlayerWeatherTracker _weatherTracker;
    private readonly IReferenceDataService? _refData;
    private readonly Action<Action> _dispatch;
    private IDisposable? _subscription;
    private IDisposable? _pinSubscription;
    private IDisposable? _celestialSubscription;
    private IDisposable? _weatherSubscription;

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
    [ObservableProperty] private string _weatherFlagText = "—";
    [ObservableProperty] private string _weatherObservedAtText = "—";

    /// <summary>The current area's pins as presentation rows. Mutated only
    /// on the dispatched (UI) thread — see <see cref="OnPins"/>.</summary>
    public ObservableCollection<MapPinRow> Pins { get; } = [];

    public WorldStateViewModel(
        IPlayerPositionTracker positionTracker,
        PlayerAreaTracker areaTracker,
        IPlayerPinTracker pinTracker,
        IPlayerCelestialState celestial,
        IPlayerWeatherTracker weatherTracker,
        IReferenceDataService? refData = null)
        : this(positionTracker, areaTracker, pinTracker, celestial, weatherTracker, refData, dispatch: null)
    { }

    /// <summary>
    /// Test-friendly ctor: inject a synchronous dispatcher so unit tests
    /// don't need an STA Application running.
    /// </summary>
    public WorldStateViewModel(
        IPlayerPositionTracker positionTracker,
        PlayerAreaTracker areaTracker,
        IPlayerPinTracker pinTracker,
        IPlayerCelestialState celestial,
        IPlayerWeatherTracker weatherTracker,
        IReferenceDataService? refData,
        Action<Action>? dispatch)
    {
        _positionTracker = positionTracker;
        _areaTracker = areaTracker;
        _pinTracker = pinTracker;
        _celestial = celestial;
        _weatherTracker = weatherTracker;
        _refData = refData;
        _dispatch = dispatch ?? DefaultDispatch;

        RefreshArea();
        // Replay-on-subscribe seeds position / the pin set / the phase /
        // weather if already known.
        _subscription = _positionTracker.Subscribe(OnPosition);
        _pinSubscription = _pinTracker.Subscribe(OnPins);
        _celestialSubscription = _celestial.Subscribe(OnCelestial);
        _weatherSubscription = _weatherTracker.Subscribe(OnWeather);
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

    /// <summary>
    /// Rebuild the pin list from the post-change snapshot. The set is tiny
    /// (a handful per area) and replay-on-login is idempotent at the tracker,
    /// so a clear-and-refill is simpler than diffing and correct for every
    /// <see cref="PinSetChange"/> kind (Snapshot / Added / Removed /
    /// AreaChanged). <see cref="PinSetChanged.Pins"/> is an immutable
    /// snapshot — safe to read off the ingestion thread before dispatching.
    /// </summary>
    private void OnPins(PinSetChanged change)
    {
        var rows = change.Pins.Select(MapPinRow.From).ToArray();
        // A Snapshot replay reflects the existing set, not a fresh
        // observation — keep the "—" placeholder until a real change lands.
        var stamp = change.Kind == PinSetChange.Snapshot
            ? null
            : change.ObservedAt.UtcDateTime.ToString("u", CultureInfo.InvariantCulture);

        _dispatch(() =>
        {
            Pins.Clear();
            foreach (var r in rows) Pins.Add(r);
            PinCount = rows.Length;
            HasPins = rows.Length > 0;
            if (stamp is not null) PinsObservedAtText = stamp;
        });
    }

    /// <summary>
    /// Surface the current lunar phase. <see cref="CelestialInfo"/> is an
    /// immutable record — safe to read off the ingestion thread before
    /// dispatching. The raw token is shown alongside the friendly name so an
    /// unrecognised / future phase token is still inspectable on this debug
    /// surface.
    /// </summary>
    private void OnCelestial(CelestialInfo c) => _dispatch(() =>
    {
        HasMoonPhase = true;
        MoonPhaseText = c.DisplayName;
        MoonPhaseRawText = c.Phase == MoonPhase.Unknown
            ? $"{c.RawPhase} (unrecognised token)"
            : c.RawPhase;
        MoonMeasuredAtText = c.MeasuredAt.UtcDateTime.ToString("u", CultureInfo.InvariantCulture);
    });

    /// <summary>
    /// Project the current map's weather. <see cref="WeatherChanged.State"/>
    /// is <c>null</c> for an <see cref="WeatherChangeKind.AreaChanged"/> reset
    /// (or a pre-known Snapshot) — surfaced as "weather unknown for this map",
    /// deliberately distinct from a known clear sky (the Vampirism-relevant
    /// distinction). Stamping mirrors <see cref="OnPins"/>: a Snapshot replay
    /// reflects existing state, not a fresh observation, so the "—" placeholder
    /// is kept until a real change lands; a map change resets it.
    /// </summary>
    private void OnWeather(WeatherChanged change)
    {
        var state = change.State;
        var stamp = state is null || change.Kind == WeatherChangeKind.Snapshot
            ? null
            : change.ObservedAt.UtcDateTime.ToString("u", CultureInfo.InvariantCulture);

        _dispatch(() =>
        {
            HasWeather = state is not null;
            WeatherConditionText = state?.Condition ?? "(weather unknown for this map)";
            WeatherFlagText = state is null ? "—" : (state.Flag ? "True" : "False");
            if (stamp is not null) WeatherObservedAtText = stamp;
            else if (change.Kind == WeatherChangeKind.AreaChanged) WeatherObservedAtText = "—";
        });
    }

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
        _pinSubscription?.Dispose();
        _pinSubscription = null;
        _celestialSubscription?.Dispose();
        _celestialSubscription = null;
        _weatherSubscription?.Dispose();
        _weatherSubscription = null;
    }

    private static void DefaultDispatch(Action action)
    {
        var d = System.Windows.Application.Current?.Dispatcher;
        if (d is null || d.CheckAccess()) action();
        else d.InvokeAsync(action);
    }
}

/// <summary>
/// Presentation-only projection of a <see cref="MapPin"/> for the World
/// State debug list — formatting lives here, not in the XAML.
/// </summary>
/// <param name="Label">The pin label, or "Unnamed pin" for blank labels
/// (<see cref="MapPin.DisplayName"/>).</param>
/// <param name="Appearance">Human appearance phrase, e.g. "red dot"
/// (<see cref="MapPin.Appearance"/>).</param>
/// <param name="Coords">Signed engine-unit ground-plane coordinate, formatted
/// to match the POSITION row (Y is intentionally dropped for pins).</param>
/// <param name="Detail">Raw enum names — debug-surface extra.</param>
public sealed record MapPinRow(string Label, string Appearance, string Coords, string Detail)
{
    public static MapPinRow From(MapPin p) => new(
        p.DisplayName,
        p.Appearance,
        string.Format(CultureInfo.InvariantCulture, "X {0:0.00}   Z {1:0.00}", p.X, p.Z),
        $"{p.Color} · {p.Shape}");
}
