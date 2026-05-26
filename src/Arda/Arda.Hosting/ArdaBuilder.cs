using Arda.Dispatch;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Arda.Hosting;

/// <summary>
/// Fluent builder for configuring Arda's dispatch table. Collects handler
/// registrations and builds the frozen <see cref="DispatchTable"/> at first
/// resolve from the DI container.
/// </summary>
public sealed class ArdaBuilder
{
    private readonly IServiceCollection _services;
    private readonly List<Action<IServiceProvider, Dictionary<string, List<IFrameHandler>>>> _registrations = [];
    private readonly List<Func<IServiceProvider, ILineObserver>> _observerFactories = [];

    internal ArdaBuilder(IServiceCollection services)
    {
        _services = services;
    }

    /// <summary>
    /// The underlying service collection, exposed for additional registrations.
    /// </summary>
    public IServiceCollection Services => _services;

    /// <summary>
    /// Register a handler factory that will be invoked during dispatch table
    /// construction. The factory receives the service provider and the mutable
    /// registry dictionary. Use <see cref="Register"/> for common cases.
    /// </summary>
    public ArdaBuilder ConfigureHandlers(
        Action<IServiceProvider, Dictionary<string, List<IFrameHandler>>> configure)
    {
        _registrations.Add(configure);
        return this;
    }

    /// <summary>
    /// Register one or more handlers for a verb. Handlers are dispatched in
    /// registration order.
    /// </summary>
    public ArdaBuilder Register(string verb, params IFrameHandler[] handlers)
    {
        _registrations.Add((_, registry) =>
        {
            if (!registry.TryGetValue(verb, out var list))
            {
                list = [];
                registry[verb] = list;
            }
            list.AddRange(handlers);
        });
        return this;
    }

    /// <summary>
    /// Register an <see cref="ILineObserver"/> that will be called for every
    /// log line before verb dispatch. The observer type must also be registered
    /// in the DI container (typically as a singleton).
    /// </summary>
    public ArdaBuilder AddLineObserver<T>() where T : class, ILineObserver
    {
        _observerFactories.Add(sp => sp.GetRequiredService<T>());
        return this;
    }

    internal IReadOnlyList<ILineObserver> BuildLineObservers(IServiceProvider sp) =>
        _observerFactories.Select(f => f(sp)).ToList();

    internal DispatchTable BuildDispatchTable(IServiceProvider sp)
    {
        var registry = new Dictionary<string, List<IFrameHandler>>(StringComparer.Ordinal);

        foreach (var registration in _registrations)
            registration(sp, registry);

        var logger = sp.GetRequiredService<ILogger<DispatchTable>>();
        return new DispatchTable(registry, logger);
    }
}
