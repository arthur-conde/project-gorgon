using Arda.Abstractions.Logs;
using Arda.Contracts;
using Arda.Dispatch;
using Arda.World.Player.Events;
using Arda.World.Player.Internal;
using FluentAssertions;
using Xunit;

namespace Arda.World.Player.Tests;

public class WeatherTests
{
    private readonly SpyEventBus _bus = new();
    private readonly Weather _weather;

    public WeatherTests()
    {
        _weather = new Weather(_bus);
    }

    private static LogLineMetadata Meta(bool isReplay = false) =>
        new(DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, isReplay);

    private void Dispatch(string condition, bool flag = true)
    {
        var args = $"(\"{condition}\", {(flag ? "True" : "False")})".AsSpan();
        _weather.Handle(args, default, $"LocalPlayer: ProcessSetWeather(\"{condition}\", {(flag ? "True" : "False")})", Meta());
    }

    [Fact]
    public void FirstWeather_EmitsEvent()
    {
        Dispatch("Foggy");

        _weather.CurrentWeather.Should().Be("Foggy");
        _bus.Published<WeatherChanged>().Should().ContainSingle()
            .Which.Should().BeEquivalentTo(new
            {
                Previous = (string?)null,
                Current = "Foggy"
            });
    }

    [Fact]
    public void SameWeather_DoesNotEmit()
    {
        Dispatch("Clear");
        _bus.Clear();

        Dispatch("Clear");

        _bus.Published<WeatherChanged>().Should().BeEmpty();
        _weather.CurrentWeather.Should().Be("Clear");
    }

    [Fact]
    public void WeatherTransition_ReportsPrevious()
    {
        Dispatch("Clear");
        _bus.Clear();

        Dispatch("Rain");

        _weather.CurrentWeather.Should().Be("Rain");
        _bus.Published<WeatherChanged>().Should().ContainSingle()
            .Which.Should().BeEquivalentTo(new
            {
                Previous = "Clear",
                Current = "Rain"
            });
    }

    [Fact]
    public void Reset_ClearsState()
    {
        Dispatch("Snow");
        _weather.Reset();

        _weather.CurrentWeather.Should().BeNull();
    }

    [Fact]
    public void AfterReset_NextWeatherEmitsWithNullPrevious()
    {
        Dispatch("Snow");
        _weather.Reset();
        _bus.Clear();

        Dispatch("Clear");

        _bus.Published<WeatherChanged>().Should().ContainSingle()
            .Which.Previous.Should().BeNull();
    }

    private sealed class SpyEventBus : IDomainEventSubscriber, IDomainEventPublisher
    {
        private readonly Dictionary<Type, List<object>> _published = [];

        public IDisposable Subscribe<T>(Action<T> handler) where T : struct => new NoopDisposable();

        public void Publish<T>(T domainEvent) where T : struct
        {
            if (!_published.TryGetValue(typeof(T), out var list))
            {
                list = [];
                _published[typeof(T)] = list;
            }
            list.Add(domainEvent);
        }

        public List<T> Published<T>() where T : struct
        {
            if (_published.TryGetValue(typeof(T), out var list))
                return list.Cast<T>().ToList();
            return [];
        }

        public void Clear() => _published.Clear();

        private sealed class NoopDisposable : IDisposable { public void Dispose() { } }
    }
}
