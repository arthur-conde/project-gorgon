namespace Mithril.GameState.Weather;

/// <summary>
/// The local player's ambient weather <em>for the current map</em>, plus the
/// instant it was observed. <see cref="MeasuredAt"/> is the source log line's
/// UTC instant (Player.log <c>[HH:MM:SS]</c> prefixes are UTC), exposed as a
/// <see cref="DateTimeOffset"/>.
///
/// <para><see cref="Condition"/> is the game's verb argument verbatim
/// (e.g. <c>"Foggy"</c>) — deliberately <em>not</em> classified into
/// sunny/overcast here. The <c>Vampirism</c> sun-damage consumer owns that
/// interpretation; this record is the faithful raw signal (the same
/// dumb-producer split as the pin tracker surfacing raw pin appearance).</para>
/// </summary>
/// <param name="Condition">The weather condition string verbatim from the
/// <c>ProcessSetWeather</c> first argument.</param>
/// <param name="Flag">The line's opaque second boolean — passed through
/// unmodified; semantics unverified (Verification owed, see
/// <see cref="WeatherChangedEvent.Flag"/>).</param>
/// <param name="MeasuredAt">UTC instant of the source log line.</param>
public sealed record WeatherState(
    string Condition, bool Flag, DateTimeOffset MeasuredAt);

/// <summary>How a <see cref="WeatherChanged"/> notification arose.</summary>
public enum WeatherChangeKind
{
    /// <summary>Replayed synchronously to a new subscriber — the current
    /// map's weather as it already stands (<see cref="WeatherChanged.State"/>
    /// is <c>null</c> if none has been observed for this map yet). Mirrors
    /// <c>PinSetChange.Snapshot</c>.</summary>
    Snapshot,

    /// <summary>A genuine weather change for the current map. An idempotent
    /// re-emit of the same condition/flag (e.g. a zone-entry replay) does
    /// <b>not</b> raise this.</summary>
    Changed,

    /// <summary>The player changed map: the previous map's weather was dropped
    /// (it is per-map and does not carry across) and
    /// <see cref="WeatherChanged.State"/> is <c>null</c> until the new map's
    /// <c>ProcessSetWeather</c> arrives. Mirrors
    /// <c>PinSetChange.AreaChanged</c>.</summary>
    AreaChanged,
}

/// <summary>
/// A change to the current map's weather. <see cref="State"/> is the full
/// weather <em>after</em> the change (an immutable record, safe to hold), or
/// <c>null</c> when the current map's weather is not yet known — after an
/// <see cref="WeatherChangeKind.AreaChanged"/>, or a
/// <see cref="WeatherChangeKind.Snapshot"/> replay before any weather has been
/// observed for the map. For the <c>Vampirism</c> consumer, <c>null</c> means
/// "weather unknown" — distinct from a known clear/sunny condition.
/// </summary>
/// <param name="Kind">Why this notification arose.</param>
/// <param name="Area">The map key the weather belongs to (the shared
/// <c>PlayerAreaTracker</c> key; may be <c>null</c> if the area is
/// unknown).</param>
/// <param name="State">The current map's weather after the change, or
/// <c>null</c> if not yet known for this map.</param>
/// <param name="ObservedAt">UTC instant of the source log line. For a
/// <see cref="WeatherChangeKind.Snapshot"/> replay this is the most-recent
/// envelope timestamp the tracker has applied
/// (<see cref="DateTimeOffset.MinValue"/> if no envelope has been applied yet
/// — in which case <c>State</c> is also <c>null</c>); for a
/// <see cref="WeatherChangeKind.AreaChanged"/> notification this is the
/// area-transition envelope instant.</param>
public sealed record WeatherChanged(
    WeatherChangeKind Kind,
    string? Area,
    WeatherState? State,
    DateTimeOffset ObservedAt);

/// <summary>
/// Shared live game-state: the local player's <b>per-map</b> ambient weather,
/// owned authoritatively here. Mirrors
/// <see cref="Mithril.GameState.Pins.IPlayerPinTracker"/> — weather is
/// map-scoped (it does not carry from one map to the next, which matters for
/// the <c>Vampirism</c> sun-damage consumer), so this exposes a
/// <see cref="CurrentArea"/> + the map's <see cref="Current"/> weather plus a
/// replay-on-<see cref="Subscribe"/> handler so late subscribers see the same
/// view already-attached ones do.
///
/// <para><b>Why the service owns the lifecycle.</b> Weather belongs to the
/// map; on a map change the previous map's value is stale and is dropped, and
/// the new map's <c>ProcessSetWeather</c> repopulates it (an unchanged re-emit
/// is idempotent). Centralising this here means every consumer reads a correct
/// current-map weather instead of each re-deriving the area gate.</para>
/// </summary>
public interface IPlayerWeatherTracker
{
    /// <summary>The map the tracked weather belongs to (the shared
    /// <c>PlayerAreaTracker</c> key), or <c>null</c> if unknown.</summary>
    string? CurrentArea { get; }

    /// <summary>
    /// The current map's weather, or <c>null</c> before the first
    /// <c>ProcessSetWeather</c> for this map is seen / immediately after a map
    /// change. <c>null</c> means "weather unknown for this map" — for the
    /// <c>Vampirism</c> consumer that is distinct from a known clear sky.
    /// </summary>
    WeatherState? Current { get; }

    /// <summary>
    /// Register a handler. The current state is replayed synchronously as a
    /// <see cref="WeatherChangeKind.Snapshot"/> before the call returns;
    /// subsequent changes are delivered live until the returned token is
    /// disposed. Handlers run on the ingestion thread — marshal off-thread for
    /// non-trivial / UI work (mirrors the pin tracker's contract).
    /// </summary>
    IDisposable Subscribe(Action<WeatherChanged> handler);
}
