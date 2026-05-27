using Arda.Abstractions.Logs;
using Arda.Contracts;
using Arda.Dispatch;
using Arda.World.Chat.Events;

namespace Arda.World.Chat.Internal;

/// <summary>
/// Handles <c>CHAT_PLAYER_LINE</c> — parses <c>[Channel] Speaker: text</c> into
/// its components. Channel and Speaker are interned (Tier 1); Text is a
/// zero-copy <see cref="ReadOnlyMemory{T}"/> slice of <paramref name="sourceLog"/>
/// (Tier 2).
/// </summary>
internal sealed class ChatLine : IFrameHandler
{
    private readonly IDomainEventPublisher _bus;

    public ChatLine(IDomainEventPublisher bus) => _bus = bus;

    public void Handle(ReadOnlySpan<char> args, ReadOnlySpan<char> verb, string sourceLog, LogLineMetadata metadata)
    {
        if (args.Length < 3 || args[0] != '[')
            return;

        var closeBracket = args.IndexOf(']');
        if (closeBracket <= 1)
            return;

        var channel = args[1..closeBracket];

        var afterChannel = args[(closeBracket + 1)..];
        int skipSpace = afterChannel.Length > 0 && afterChannel[0] == ' ' ? 1 : 0;
        afterChannel = afterChannel[skipSpace..];

        var colonSpace = afterChannel.IndexOf(": ");
        if (colonSpace <= 0)
            return;

        if (!sourceLog.AsSpan().Overlaps(args, out int argsOffset))
            return;

        int textStart = argsOffset + closeBracket + 1 + skipSpace + colonSpace + 2;

        _bus.Publish(new PlayerChatLine(
            string.Intern(channel.ToString()),
            string.Intern(afterChannel[..colonSpace].ToString()),
            sourceLog.AsMemory(textStart),
            metadata));
    }
}
