using Arda.Abstractions.Logs;
using Arda.Dispatch;
using Arda.World.Player.Events;

namespace Arda.World.Player.Internal;

/// <summary>
/// Tier 2 passthrough handler for <c>ProcessWaitInteraction</c>.
/// Args: (entityId, ms, "verb", "body")
/// </summary>
internal sealed class WaitInteractionHandler(IDomainEventPublisher bus) : IFrameHandler
{
    public void Handle(ReadOnlySpan<char> args, string sourceLog, LogLineMetadata metadata)
    {
        var tok = new ArgTokenizer(args);
        tok.SkipOpen();

        var entityId = tok.NextLong();
        tok.NextLong(); // ms — not consumed downstream
        tok.NextQuotedSpan(); // verb string
        var bodySpan = tok.NextQuotedSpan();

        var bodyMem = SliceFromSource(sourceLog, bodySpan);

        bus.Publish(new InteractionWaiting(entityId, bodyMem, metadata));
    }

    private static ReadOnlyMemory<char> SliceFromSource(string sourceLog, ReadOnlySpan<char> span)
    {
        if (span.IsEmpty) return ReadOnlyMemory<char>.Empty;
        var sourceSpan = sourceLog.AsSpan();
        if (sourceSpan.Overlaps(span, out var offset))
            return sourceLog.AsMemory(offset, span.Length);
        return span.ToString().AsMemory();
    }
}
