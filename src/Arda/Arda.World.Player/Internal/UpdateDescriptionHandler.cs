using Arda.Abstractions.Logs;
using Arda.Contracts;
using Arda.Dispatch;
using Arda.World.Player.Events;

namespace Arda.World.Player.Internal;

/// <summary>
/// Tier 2 passthrough handler for <c>ProcessUpdateDescription</c>.
/// Parses garden plot state and emits <see cref="UpdateDescriptionFrame"/>
/// with zero-alloc <see cref="ReadOnlyMemory{T}"/> slices into the source log line.
/// </summary>
internal sealed class UpdateDescriptionHandler(IDomainEventPublisher bus) : IFrameHandler
{
    public void Handle(ReadOnlySpan<char> args, string sourceLog, LogLineMetadata metadata)
    {
        // Args: (plotId, "title", "description", "action", unused, "unused(Scale=X)", count)
        var tok = new ArgTokenizer(args);
        tok.SkipOpen();

        var plotId = tok.NextLong();

        var titleSpan = tok.NextQuotedSpan();
        var descSpan = tok.NextQuotedSpan();
        var actionSpan = tok.NextQuotedSpan();
        tok.NextTokenSpan(); // unused token
        var scaleContainer = tok.NextQuotedSpan(); // "unused(Scale=0.5)"

        var scale = 0.0;
        var scaleIdx = scaleContainer.IndexOf("Scale=");
        if (scaleIdx >= 0)
        {
            var afterScale = scaleContainer[(scaleIdx + 6)..];
            var end = afterScale.IndexOf(')');
            if (end > 0)
                double.TryParse(afterScale[..end], System.Globalization.CultureInfo.InvariantCulture, out scale);
        }

        var titleMem = SpanHelpers.SliceFromSource(sourceLog, titleSpan);
        var descMem = SpanHelpers.SliceFromSource(sourceLog, descSpan);
        var actionMem = SpanHelpers.SliceFromSource(sourceLog, actionSpan);

        bus.Publish(new UpdateDescriptionFrame(plotId, titleMem, descMem, actionMem, scale, metadata));
    }
}
