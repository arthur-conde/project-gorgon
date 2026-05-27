namespace Arda.Contracts;

/// <summary>
/// Write-only view of the domain event bus. Internal Arda handlers use this
/// interface to emit events without gaining subscribe access.
/// </summary>
public interface IDomainEventPublisher
{
    /// <summary>Emit a domain event (called by frame handlers / state machines).</summary>
    void Publish<T>(T domainEvent) where T : struct;
}
