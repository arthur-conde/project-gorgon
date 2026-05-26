using Arda.Contracts;
using Arda.Dispatch;

namespace Arda.World.Chat.Tests;

internal sealed class SpyEventBus : IDomainEventBus
{
    private readonly Dictionary<Type, List<object>> _published = [];

    public IDisposable Subscribe<T>(Action<T> handler) where T : struct
        => new NoopDisposable();

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

    private sealed class NoopDisposable : IDisposable
    {
        public void Dispose() { }
    }
}
