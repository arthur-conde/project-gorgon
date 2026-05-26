using Arda.Abstractions.Logs;
using Arda.Dispatch;
using Arda.World.Player.Events;

namespace Arda.World.Player.Internal;

/// <summary>
/// Tier 2 passthrough handler for <c>ProcessVendorAddItem</c>.
/// Args: (price, InternalName(instanceId), bool)
/// The InternalName(instanceId) token is split on '(' to extract both parts.
/// </summary>
internal sealed class VendorAddItemHandler(IDomainEventPublisher bus) : IFrameHandler
{
    public void Handle(ReadOnlySpan<char> args, string sourceLog, LogLineMetadata metadata)
    {
        var tok = new ArgTokenizer(args);
        tok.SkipOpen();

        var price = tok.NextLong();

        // InternalName(instanceId) is a single token like "SwordNovice(12345)"
        var nameAndIdSpan = tok.NextTokenSpan();
        var parenIdx = nameAndIdSpan.IndexOf('(');
        if (parenIdx < 0) return;

        var internalName = nameAndIdSpan[..parenIdx].ToString();
        var idSpan = nameAndIdSpan[(parenIdx + 1)..];
        var closeIdx = idSpan.IndexOf(')');
        if (closeIdx > 0)
            idSpan = idSpan[..closeIdx];

        if (!long.TryParse(idSpan, System.Globalization.CultureInfo.InvariantCulture, out var instanceId))
            return;

        bus.Publish(new VendorItemSold(price, internalName, instanceId, metadata));
    }
}
