using Arda.Abstractions.Logs;
using Arda.Dispatch;
using Arda.World.Player.Events;

namespace Arda.World.Player.Internal;

/// <summary>
/// Tier 2 passthrough handler for <c>ProcessVendorScreen</c>.
/// Args: (entityId, FavorTier, gold, resetCounter, cap, ...)
/// </summary>
internal sealed class VendorScreenHandler(IDomainEventPublisher bus) : IFrameHandler
{
    public void Handle(ReadOnlySpan<char> args, string sourceLog, LogLineMetadata metadata)
    {
        var tok = new ArgTokenizer(args);
        tok.SkipOpen();

        var entityId = tok.NextInt();
        var favorTier = tok.NextTokenSpan().ToString();
        var remainingGold = tok.NextLong();
        tok.NextLong(); // resetCounter — not consumed downstream
        var goldCap = tok.NextLong();

        bus.Publish(new VendorScreenOpened(entityId, favorTier, remainingGold, goldCap, metadata));
    }
}
