using Arda.Abstractions.Logs;
using Arda.Dispatch;
using Arda.World.Player.Events;

namespace Arda.World.Player.Internal;

/// <summary>
/// Tier 2 passthrough handler for <c>ProcessVendorUpdateAvailableGold</c>.
/// Args: (remainingGold, resetCounter, goldCap)
/// </summary>
internal sealed class VendorGoldHandler(IDomainEventPublisher bus) : IFrameHandler
{
    public void Handle(ReadOnlySpan<char> args, string sourceLog, LogLineMetadata metadata)
    {
        var tok = new ArgTokenizer(args);
        tok.SkipOpen();

        var remainingGold = tok.NextLong();
        var goldResetsAt = DateTimeOffset.FromUnixTimeMilliseconds(tok.NextLong());
        var goldCap = tok.NextLong();

        bus.Publish(new VendorGoldUpdated(remainingGold, goldCap, goldResetsAt, metadata));
    }
}
