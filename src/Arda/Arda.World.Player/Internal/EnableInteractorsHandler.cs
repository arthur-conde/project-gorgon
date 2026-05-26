using Arda.Abstractions.Logs;
using Arda.Contracts;
using Arda.Dispatch;
using Arda.World.Player.Events;

namespace Arda.World.Player.Internal;

/// <summary>
/// Tier 2 passthrough handler for <c>ProcessEnableInteractors([], [id,])</c>.
/// Extracts the interactor id from the second bracketed array.
/// Primary consumer: Gandalf (LootBracketTracker bracket close signal).
/// </summary>
internal sealed class EnableInteractorsHandler(IDomainEventPublisher bus) : IFrameHandler
{
    public void Handle(ReadOnlySpan<char> args, string sourceLog, LogLineMetadata metadata)
    {
        var tok = new ArgTokenizer(args);
        tok.SkipOpen();
        tok.NextBracketedSpan(); // skip first [] (always empty)

        var idArray = tok.NextBracketedSpan(); // "id," or "id"
        var commaIdx = idArray.IndexOf(',');
        var idSpan = commaIdx >= 0 ? idArray[..commaIdx] : idArray;

        if (idSpan.IsEmpty || !long.TryParse(idSpan.Trim(), out var interactorId))
            return;

        bus.Publish(new EnableInteractorsFrame(interactorId, metadata));
    }
}
