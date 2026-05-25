using Arda.Dispatch;
using Microsoft.Extensions.DependencyInjection;

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

    internal DispatchTable BuildDispatchTable(IServiceProvider sp)
    {
        var registry = new Dictionary<string, List<IFrameHandler>>(StringComparer.Ordinal);

        foreach (var registration in _registrations)
            registration(sp, registry);

        return new DispatchTable(registry);
    }
}
