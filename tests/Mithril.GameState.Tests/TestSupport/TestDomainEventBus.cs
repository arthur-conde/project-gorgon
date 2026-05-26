using Arda.Dispatch;

namespace Mithril.GameState.Tests.TestSupport;

/// <summary>
/// Minimal <see cref="IDomainEventSubscriber"/> test double. Supports
/// synchronous <see cref="Subscribe{T}"/> + <see cref="Publish{T}"/>
/// without the Arda dispatch pipeline. Handlers fire on the publishing
/// thread in registration order.
/// </summary>
internal sealed class TestDomainEventBus : IDomainEventSubscriber
{
    private readonly Dictionary<Type, List<Delegate>> _handlers = new();

    public IDisposable Subscribe<T>(Action<T> handler) where T : struct
    {
        ArgumentNullException.ThrowIfNull(handler);
        if (!_handlers.TryGetValue(typeof(T), out var list))
            _handlers[typeof(T)] = list = new();
        list.Add(handler);
        return new Sub(() => list.Remove(handler));
    }

    public void Publish<T>(T evt) where T : struct
    {
        if (!_handlers.TryGetValue(typeof(T), out var list)) return;
        foreach (var h in list.ToArray())
            ((Action<T>)h)(evt);
    }

    private sealed class Sub(Action onDispose) : IDisposable
    {
        private Action? _action = onDispose;
        public void Dispose() { _action?.Invoke(); _action = null; }
    }
}
