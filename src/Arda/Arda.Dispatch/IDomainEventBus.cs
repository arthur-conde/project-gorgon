namespace Arda.Dispatch;

/// <summary>
/// Typed publish-subscribe bus for domain events emitted by L3 handlers.
/// Replaces the legacy <c>IWorldEventBus</c> / <c>Frame&lt;T&gt;</c> pattern.
/// <para>
/// Events are value-type structs carrying their own <see cref="Arda.Abstractions.Logs.LogLineMetadata"/>
/// field — no wrapper envelope needed. Dispatch is synchronous within the
/// publishing handler's tick, so subscribers execute inline on the driver thread.
/// </para>
/// </summary>
public interface IDomainEventBus
{
    /// <summary>Subscribe to domain events of type <typeparamref name="T"/>.</summary>
    IDisposable Subscribe<T>(Action<T> handler) where T : struct;

    /// <summary>Emit a domain event (called by frame handlers / state machines).</summary>
    void Publish<T>(T domainEvent) where T : struct;
}
