using Arda.Abstractions.Logs;
using Arda.Dispatch;
using Arda.World.Chat.Events;

namespace Arda.World.Chat.Internal;

/// <summary>
/// Handles <c>CHAT_PLAYER_LINE</c> — parses <c>[Channel] Speaker: text</c> into
/// its components. Tier 2 passthrough: no state retained, just re-emits as a
/// typed domain event for downstream subscribers.
/// </summary>
internal sealed class ChatLine : IFrameHandler
{
    private readonly IDomainEventBus _bus;

    public ChatLine(IDomainEventBus bus) => _bus = bus;

    public void Handle(ReadOnlySpan<char> args, string sourceLog, LogLineMetadata metadata)
    {
        // args is the full line: "[Trade] Emraell: WTS something"
        if (args.Length < 3 || args[0] != '[')
            return;

        var closeBracket = args.IndexOf(']');
        if (closeBracket <= 1)
            return;

        var channel = args[1..closeBracket];

        // After "] " is "Speaker: text"
        var afterChannel = args[(closeBracket + 1)..];
        if (afterChannel.Length > 0 && afterChannel[0] == ' ')
            afterChannel = afterChannel[1..];

        var colonSpace = afterChannel.IndexOf(": ");
        if (colonSpace <= 0)
            return;

        var speaker = afterChannel[..colonSpace];
        var text = afterChannel[(colonSpace + 2)..];

        _bus.Publish(new PlayerChatLine(
            channel.ToString(),
            speaker.ToString(),
            text.ToString(),
            metadata));
    }
}
