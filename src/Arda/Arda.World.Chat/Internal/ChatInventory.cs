using Arda.Abstractions.Logs;
using Arda.Contracts;
using Arda.Dispatch;
using Arda.World.Chat.Events;

namespace Arda.World.Chat.Internal;

/// <summary>
/// Handles <c>STATUS_INVENTORY</c> — parses <c>[Status] X [xN] added to inventory.</c>
/// lines to extract the display name and count.
/// <para>
/// Format variants:
/// <list type="bullet">
///   <item><c>[Status] Apple added to inventory.</c> (count = 1)</item>
///   <item><c>[Status] Apple x2 added to inventory.</c> (count = 2)</item>
/// </list>
/// </para>
/// </summary>
internal sealed class ChatInventory : IFrameHandler
{
    private const string Prefix = "[Status] ";
    private const string Suffix = " added to inventory.";

    private readonly IDomainEventPublisher _bus;

    public ChatInventory(IDomainEventPublisher bus) => _bus = bus;

    public void Handle(ReadOnlySpan<char> args, string sourceLog, LogLineMetadata metadata)
    {
        // args is the full line: "[Status] Apple x2 added to inventory."
        if (!args.StartsWith(Prefix) || !args.EndsWith(Suffix))
            return;

        // Middle portion is the item + optional count: "Apple x2" or "Apple"
        var middle = args[Prefix.Length..^Suffix.Length];
        if (middle.IsEmpty)
            return;

        // Check for " xN" suffix pattern
        var count = 1;
        var lastSpace = middle.LastIndexOf(' ');
        if (lastSpace > 0)
        {
            var candidate = middle[(lastSpace + 1)..];
            if (candidate.Length > 1 && candidate[0] == 'x' && int.TryParse(candidate[1..], out var parsed))
            {
                count = parsed;
                middle = middle[..lastSpace];
            }
        }

        var displayName = middle.ToString();
        _bus.Publish(new ChatInventoryObserved(displayName, count, metadata));
    }
}
