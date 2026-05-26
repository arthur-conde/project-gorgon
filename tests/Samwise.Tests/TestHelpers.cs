using Arda.Dispatch;
using Arda.World.Player;
using Samwise.Config;
using Samwise.State;

namespace Samwise.Tests;

internal sealed class FakeTime : TimeProvider
{
    public DateTimeOffset Now { get; private set; }
    public FakeTime(DateTime utc) { Now = new DateTimeOffset(utc, TimeSpan.Zero); }
    public override DateTimeOffset GetUtcNow() => Now;
    public void Advance(TimeSpan ts) => Now += ts;
}

/// <summary>
/// Fake <see cref="ICalendarState"/> exposing a settable <see cref="LastTimestamp"/>
/// for tests that need world-clock-driven time gates. The production calendar state
/// advances from log-line timestamps; the test stand-in lets callers set it directly.
/// </summary>
internal sealed class FakeCalendarState : ICalendarState
{
    public DateTimeOffset? LastTimestamp { get; set; }
    public string? CurrentShift { get; set; }
}

/// <summary>
/// Headless typed pub-sub bus for Arda domain events in tests. Mirrors the
/// real <c>DomainEventBus</c> shape — subscribers see events on the publishing
/// thread. The test calls <see cref="Publish{T}(T)"/> and all registered
/// handlers fire synchronously.
/// </summary>
internal sealed class TestDomainEventBus : IDomainEventSubscriber
{
    private readonly Dictionary<Type, List<Delegate>> _handlers = new();

    public IDisposable Subscribe<T>(Action<T> handler) where T : struct
    {
        var type = typeof(T);
        if (!_handlers.TryGetValue(type, out var list))
        {
            list = new List<Delegate>();
            _handlers[type] = list;
        }
        list.Add(handler);
        return new Subscription(this, type, handler);
    }

    public void Publish<T>(T domainEvent) where T : struct
    {
        if (!_handlers.TryGetValue(typeof(T), out var list)) return;
        foreach (var h in list.ToArray())
            ((Action<T>)h)(domainEvent);
    }

    public int SubscriberCountFor<T>() where T : struct
        => _handlers.TryGetValue(typeof(T), out var list) ? list.Count : 0;

    private sealed class Subscription(TestDomainEventBus bus, Type type, Delegate handler) : IDisposable
    {
        public void Dispose()
        {
            if (bus._handlers.TryGetValue(type, out var list))
                list.Remove(handler);
        }
    }
}

internal sealed class InMemoryCropConfig : ICropConfigStore
{
    public CropConfig Current { get; }
    public event EventHandler? Reloaded;
    public Task ReloadAsync(CancellationToken ct = default) { Reloaded?.Invoke(this, EventArgs.Empty); return Task.CompletedTask; }
    public InMemoryCropConfig()
    {
        Current = new CropConfig
        {
            SlotFamilies = new()
            {
                ["Carrot"] = new() { Max = 2 },
                ["Onion"] = new() { Max = 2 },
                ["Cotton"] = new() { Max = 5 },
                ["Flowers"] = new() { Max = 3 },
            },
            Crops = new()
            {
                ["Carrot"] = new() { SlotFamily = "Carrot", GrowthSeconds = 175 },
                ["Onion"] = new() { SlotFamily = "Onion", GrowthSeconds = 50 },
                ["Squash"] = new() { SlotFamily = "Onion", GrowthSeconds = 170 },
                ["Violet"] = new() { SlotFamily = "Flowers", GrowthSeconds = 110 },
                ["Pansy"] = new() { SlotFamily = "Flowers", GrowthSeconds = 140 },
                ["Cotton Plant"] = new() { SlotFamily = "Cotton", GrowthSeconds = 150 },
                ["Barley"] = new() { SlotFamily = "Carrot", GrowthSeconds = 150 },
            },
        };
    }
}
