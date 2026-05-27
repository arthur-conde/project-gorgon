using Arda.Abstractions.Logs;

namespace Arda.World.Player.Events;

/// <summary>
/// Emitted when the ambient weather condition changes (e.g. "Clear" → "Foggy").
/// Per-map scoped — weather does not carry across area transitions; a new
/// <c>ProcessSetWeather</c> fires on zone entry.
/// </summary>
public readonly record struct WeatherChanged(
    string? Previous,
    string Current,
    LogLineMetadata Metadata);
