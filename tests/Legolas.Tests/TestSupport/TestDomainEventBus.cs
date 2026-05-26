using Arda.Contracts;

namespace Legolas.Tests.TestSupport;

/// <summary>
/// Headless typed pub-sub bus for Arda domain events in Legolas tests.
/// Mirrors the real <c>DomainEventBus</c> shape — subscribers see events
/// on the publishing thread. Copied from Samwise.Tests' TestDomainEventBus.
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
