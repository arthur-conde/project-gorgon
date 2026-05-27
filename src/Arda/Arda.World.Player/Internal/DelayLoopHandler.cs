using Arda.Abstractions.Logs;
using Arda.Contracts;
using Arda.Dispatch;
using Arda.World.Player.Events;

namespace Arda.World.Player.Internal;

/// <summary>
/// Tier 2 passthrough handler for <c>ProcessDoDelayLoop</c>.
/// Args: (seconds, Verb, "Text", unused, AbortFlag[, flags...])
/// Free-text fields sliced as <see cref="ReadOnlyMemory{T}"/> into the source log line.
/// </summary>
internal sealed class DelayLoopHandler(IDomainEventPublisher bus) : IFrameHandler
{
    public void Handle(ReadOnlySpan<char> args, ReadOnlySpan<char> verb, string sourceLog, LogLineMetadata metadata)
    {
        var tok = new ArgTokenizer(args, verb, sourceLog);
        tok.SkipOpen();

        var seconds = tok.NextDouble();
        var verbSpan = tok.NextTokenSpan();
        var textSpan = tok.NextQuotedSpan();

        var isInteractor = args.Contains("IsInteractorDelayLoop", StringComparison.Ordinal);

        var verbMem = SpanHelpers.SliceFromSource(sourceLog, verbSpan);
        var textMem = SpanHelpers.SliceFromSource(sourceLog, textSpan);

        bus.Publish(new DelayLoopStarted(seconds, verbMem, textMem, isInteractor, metadata));
    }
}
