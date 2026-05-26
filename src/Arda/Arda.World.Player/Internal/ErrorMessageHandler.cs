using Arda.Abstractions.Logs;
using Arda.Contracts;
using Arda.Dispatch;
using Arda.World.Player.Events;

namespace Arda.World.Player.Internal;

/// <summary>
/// Tier 2 passthrough handler for <c>ProcessErrorMessage</c>. Filters for the
/// planting-cap message and emits <see cref="PlantingCapFrame"/>.
/// </summary>
internal sealed class ErrorMessageHandler(IDomainEventPublisher bus) : IFrameHandler
{
    private static ReadOnlySpan<char> PlantingCapMarker => "maximum of that type of plant growing";

    public void Handle(ReadOnlySpan<char> args, string sourceLog, LogLineMetadata metadata)
    {
        if (!args.Contains(PlantingCapMarker, StringComparison.Ordinal))
            return;

        // Extract seed display name from: (ItemUnusable, "SeedName can't be used: ...")
        var tok = new ArgTokenizer(args);
        tok.SkipOpen();
        tok.NextTokenSpan(); // "ItemUnusable"
        var msgSpan = tok.NextQuotedSpan();

        var cantIdx = msgSpan.IndexOf(" can't be used:");
        if (cantIdx <= 0)
            return;

        var nameSpan = msgSpan[..cantIdx];
        var nameMem = SpanHelpers.SliceFromSource(sourceLog, nameSpan);

        bus.Publish(new PlantingCapFrame(nameMem, metadata));
    }
}
