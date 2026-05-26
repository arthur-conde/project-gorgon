using Arda.Contracts;

namespace Arda.Dispatch;

/// <summary>
/// Composite bus interface combining <see cref="IDomainEventSubscriber"/> and
/// <see cref="IDomainEventPublisher"/>. Internal Arda components that need both
/// pub and sub (e.g. correlators) depend on this. Modules and external consumers
/// should depend on <see cref="IDomainEventSubscriber"/> only; handlers should
/// depend on <see cref="IDomainEventPublisher"/> only.
/// </summary>
public interface IDomainEventBus : IDomainEventSubscriber, IDomainEventPublisher;

