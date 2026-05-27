using Arda.Contracts;

namespace Arda.Dispatch.Internal;

/// <summary>
/// Composite bus interface combining <see cref="IDomainEventSubscriber"/> and
/// <see cref="IDomainEventPublisher"/>. Internal to <c>Arda.Dispatch</c> so the
/// concrete <see cref="DomainEventBus"/> can implement both halves with a single
/// declaration. External consumers depend on the narrow halves directly —
/// modules and composers that need to subscribe take <see cref="IDomainEventSubscriber"/>,
/// handlers and composers that need to publish take <see cref="IDomainEventPublisher"/>.
/// </summary>
internal interface IDomainEventBus : IDomainEventSubscriber, IDomainEventPublisher;
