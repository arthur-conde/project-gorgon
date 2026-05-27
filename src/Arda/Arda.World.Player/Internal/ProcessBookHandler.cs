using Arda.Abstractions.Logs;
using Arda.Contracts;
using Arda.Dispatch;
using Arda.World.Player.Events;

namespace Arda.World.Player.Internal;

/// <summary>
/// Multi-consumer handler for <c>ProcessBook</c>. Routes based on content:
/// <list type="bullet">
///   <item>"Foods Consumed" → <see cref="FoodsConsumedReport"/> (Pippin)</item>
///   <item>"You discovered a word of power!" → <see cref="WordOfPowerDiscovered"/> (Saruman/GameState)</item>
///   <item>Everything else → <see cref="BookOpened"/> (generic)</item>
/// </list>
/// Free-text fields sliced as <see cref="ReadOnlyMemory{T}"/> into the source log line.
/// </summary>
internal sealed class ProcessBookHandler(IDomainEventPublisher bus) : IFrameHandler
{
    private static ReadOnlySpan<char> FoodsConsumedMarker => "Foods Consumed";
    private static ReadOnlySpan<char> WordOfPowerMarker => "You discovered a word of power!";
    private static ReadOnlySpan<char> WopCodeOpen => "<sel>";
    private static ReadOnlySpan<char> WopCodeClose => "</sel>";
    private static ReadOnlySpan<char> WopEffectOpen => "Word of Power: ";
    private static ReadOnlySpan<char> WopEffectClose => "</size>";
    private static ReadOnlySpan<char> WopDescNewline => @"\n";

    public void Handle(ReadOnlySpan<char> args, ReadOnlySpan<char> verb, string sourceLog, LogLineMetadata metadata)
    {
        // Args: ("title", "body"[, ...])
        var tok = new ArgTokenizer(args, verb, sourceLog);
        tok.SkipOpen();

        var titleSpan = tok.NextQuotedSpan();
        var bodySpan = tok.NextQuotedSpan();

        if (bodySpan.Contains(FoodsConsumedMarker, StringComparison.Ordinal))
        {
            var bodyMem = SpanHelpers.SliceFromSource(sourceLog, bodySpan);
            bus.Publish(new FoodsConsumedReport(bodyMem, metadata));
            return;
        }

        if (titleSpan.Contains(WordOfPowerMarker, StringComparison.Ordinal))
        {
            TryEmitWordOfPower(bodySpan, sourceLog, metadata);
            return;
        }

        var titleMem = SpanHelpers.SliceFromSource(sourceLog, titleSpan);
        var genericBodyMem = SpanHelpers.SliceFromSource(sourceLog, bodySpan);
        bus.Publish(new BookOpened(titleMem, genericBodyMem, metadata));
    }

    private void TryEmitWordOfPower(ReadOnlySpan<char> body, string sourceLog, LogLineMetadata metadata)
    {
        // Body contains HTML-like markup:
        // "You've discovered a word of power: <sel>CODE</sel>...
        //  <b><size=125%>Word of Power: EffectName</size></b>\nDescription\n\n<i>..."
        var codeStart = body.IndexOf(WopCodeOpen);
        if (codeStart < 0) return;
        var afterCodeOpen = body[(codeStart + WopCodeOpen.Length)..];
        var codeEnd = afterCodeOpen.IndexOf(WopCodeClose);
        if (codeEnd < 0) return;
        var codeSpan = afterCodeOpen[..codeEnd];

        var effectStart = body.IndexOf(WopEffectOpen);
        if (effectStart < 0) return;
        var afterEffectOpen = body[(effectStart + WopEffectOpen.Length)..];
        var effectEnd = afterEffectOpen.IndexOf(WopEffectClose);
        if (effectEnd < 0) return;
        var effectSpan = afterEffectOpen[..effectEnd];

        // Description sits between the first \n after </size></b> and the \n\n before <i>
        var sizeClosePos = body.IndexOf(WopEffectClose);
        var afterSizeClose = body[(sizeClosePos + WopEffectClose.Length)..];

        // Skip past "</b>\n" to reach description start
        var descNewlineStart = afterSizeClose.IndexOf(WopDescNewline);
        if (descNewlineStart < 0) return;
        var descStart = afterSizeClose[(descNewlineStart + WopDescNewline.Length)..];

        // Find the double-newline "\n\n" marking description end
        var doubleNewline = descStart.IndexOf(@"\n\n");
        var descSpan = doubleNewline >= 0 ? descStart[..doubleNewline] : descStart;

        var codeMem = SpanHelpers.SliceFromSource(sourceLog, codeSpan);
        var effectMem = SpanHelpers.SliceFromSource(sourceLog, effectSpan);
        var descMem = SpanHelpers.SliceFromSource(sourceLog, descSpan);

        bus.Publish(new WordOfPowerDiscovered(codeMem, effectMem, descMem, metadata));
    }
}
