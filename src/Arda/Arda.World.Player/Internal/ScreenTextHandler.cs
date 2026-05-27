using Arda.Abstractions.Logs;
using Arda.Contracts;
using Arda.Dispatch;
using Arda.World.Player.Events;

namespace Arda.World.Player.Internal;

/// <summary>
/// Discriminating handler for <c>ProcessScreenText</c>.
/// Emits <see cref="ScreenTextObserved"/> for ALL categories (Gandalf, Legolas, Samwise consumers).
/// Additionally emits <see cref="ScreenTextErrorFrame"/> for ErrorMessage lines
/// (backward-compat marker for Samwise).
/// </summary>
internal sealed class ScreenTextHandler(IDomainEventPublisher bus) : IFrameHandler
{
    private static ReadOnlySpan<char> ErrorMarker => "ErrorMessage";

    public void Handle(ReadOnlySpan<char> args, ReadOnlySpan<char> verb, string sourceLog, LogLineMetadata metadata)
    {
        if (args.Length == 0) return;

        var isError = args.Contains(ErrorMarker, StringComparison.Ordinal);

        if (isError)
            bus.Publish(new ScreenTextErrorFrame(metadata));

        var tok = new ArgTokenizer(args, verb, sourceLog);
        tok.SkipOpen();
        var categorySpan = tok.NextTokenSpan();
        var textSpan = tok.NextQuotedSpan();

        var categoryMem = SpanHelpers.SliceFromSource(sourceLog, categorySpan);
        var textMem = SpanHelpers.SliceFromSource(sourceLog, textSpan);

        bus.Publish(new ScreenTextObserved(categoryMem, textMem, metadata));
    }
}
