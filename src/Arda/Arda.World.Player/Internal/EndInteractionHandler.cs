using Arda.Abstractions.Logs;
using Arda.Contracts;
using Arda.Dispatch;
using Arda.World.Player.Events;

namespace Arda.World.Player.Internal;

/// <summary>
/// Tier 2 passthrough handler for <c>ProcessEndInteraction</c>.
/// Args: (entityId)
/// </summary>
internal sealed class EndInteractionHandler(IDomainEventPublisher bus) : IFrameHandler
{
    public void Handle(ReadOnlySpan<char> args, string sourceLog, LogLineMetadata metadata)
    {
        var tok = new ArgTokenizer(args);
        tok.SkipOpen();
        var entityId = tok.NextLong();
        bus.Publish(new InteractionEnded(entityId, metadata));
    }
}
