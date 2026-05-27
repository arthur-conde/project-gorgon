namespace Arda.World.Player;

/// <summary>
/// Read-only view of the current ambient weather condition.
/// </summary>
public interface IWeatherState
{
    /// <summary>
    /// The current weather condition string (e.g. "Clear", "Foggy"), or <c>null</c>
    /// if no weather has been observed yet (pre-login or between areas).
    /// </summary>
    string? CurrentWeather { get; }
}
