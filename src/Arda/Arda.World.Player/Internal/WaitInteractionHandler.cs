using Arda.Abstractions.Logs;
using Arda.Contracts;
using Arda.Dispatch;
using Arda.World.Player.Events;

namespace Arda.World.Player.Internal;

/// <summary>
/// Tier 2 passthrough handler for <c>ProcessWaitInteraction</c>.
/// Args: (entityId, ms, "verb", "body")
/// </summary>
internal sealed class WaitInteractionHandler(IDomainEventPublisher bus) : IFrameHandler
{
    public void Handle(ReadOnlySpan<char> args, ReadOnlySpan<char> verb, string sourceLog, LogLineMetadata metadata)
    {
        var tok = new ArgTokenizer(args, verb, sourceLog);
        tok.SkipOpen();

        var entityId = tok.NextLong();
        tok.NextLong(); // ms — not consumed downstream
        tok.NextQuotedSpan(); // verb string
        var bodySpan = tok.NextQuotedSpan();

        var bodyMem = SpanHelpers.SliceFromSource(sourceLog, bodySpan);

        bus.Publish(new InteractionWaiting(entityId, bodyMem, metadata));
    }
}
