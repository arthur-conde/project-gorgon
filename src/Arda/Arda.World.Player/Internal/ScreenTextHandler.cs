using Arda.Abstractions.Logs;
using Arda.Dispatch;
using Arda.World.Player.Events;

namespace Arda.World.Player.Internal;

/// <summary>
/// Discriminating handler for <c>ProcessScreenText</c>.
/// Emits <see cref="ScreenTextErrorFrame"/> for ErrorMessage lines (Samwise consumer)
/// and <see cref="ScreenTextObserved"/> for all other categories (Gandalf, Legolas consumers).
/// </summary>
internal sealed class ScreenTextHandler(IDomainEventPublisher bus) : IFrameHandler
{
    private static ReadOnlySpan<char> ErrorMarker => "ErrorMessage";

    public void Handle(ReadOnlySpan<char> args, string sourceLog, LogLineMetadata metadata)
    {
        if (args.Length == 0) return;

        if (args.Contains(ErrorMarker, StringComparison.Ordinal))
        {
            bus.Publish(new ScreenTextErrorFrame(metadata));
            return;
        }

        // Non-ErrorMessage categories: extract (Category, "Text")
        var tok = new ArgTokenizer(args);
        tok.SkipOpen();
        var categorySpan = tok.NextTokenSpan();
        var textSpan = tok.NextQuotedSpan();

        var categoryMem = SliceFromSource(sourceLog, categorySpan);
        var textMem = SliceFromSource(sourceLog, textSpan);

        bus.Publish(new ScreenTextObserved(categoryMem, textMem, metadata));
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
