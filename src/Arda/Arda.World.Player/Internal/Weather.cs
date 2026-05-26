using Arda.Abstractions.Logs;
using Arda.Dispatch;
using Arda.World.Player.Events;

namespace Arda.World.Player.Internal;

/// <summary>
/// Tracks the player's current ambient weather condition via
/// <c>ProcessSetWeather("condition", flag)</c>. Deduplicates same-condition
/// re-emissions and resets on character switch / area transition.
/// </summary>
internal sealed class Weather : IFrameHandler, IWeatherState
{
    private readonly IDomainEventPublisher _bus;

    public string? CurrentWeather { get; private set; }
    public DateTimeOffset? MeasuredAt { get; private set; }

    public Weather(IDomainEventPublisher bus) => _bus = bus;

    internal void Reset()
    {
        CurrentWeather = null;
        MeasuredAt = null;
    }

    public void Handle(ReadOnlySpan<char> args, string sourceLog, LogLineMetadata metadata)
    {
        var tok = new ArgTokenizer(args);
        tok.SkipOpen();

        var conditionSpan = tok.NextQuotedSpan();
        if (conditionSpan.IsEmpty)
            return;

        var condition = conditionSpan.ToString();

        if (string.Equals(CurrentWeather, condition, StringComparison.Ordinal))
            return;

        var previous = CurrentWeather;
        CurrentWeather = condition;
        MeasuredAt = metadata.Timestamp ?? metadata.ReadOn;
        _bus.Publish(new WeatherChanged(previous, condition, metadata));
    }
}
