namespace Arda.Dispatch;

/// <summary>
/// Read-only view of the domain event bus. Modules and external consumers
/// depend on this interface to receive events without gaining publish access.
/// </summary>
public interface IDomainEventSubscriber
{
    /// <summary>Subscribe to domain events of type <typeparamref name="T"/>.</summary>
    IDisposable Subscribe<T>(Action<T> handler) where T : struct;
}
