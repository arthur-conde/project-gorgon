using Arda.Abstractions.Logs;
using Arda.Dispatch;
using Arda.World.Player.Events;

namespace Arda.World.Player.Internal;

/// <summary>
/// Parses <c>ProcessDeltaFavor(entityId, "NPC_Key", delta, bool)</c> and emits
/// <see cref="DeltaFavorReceived"/> for positive deltas during an active NPC interaction.
/// </summary>
internal sealed class DeltaFavorHandler(Npc npc, IDomainEventPublisher bus) : IFrameHandler
{
    public void Handle(ReadOnlySpan<char> args, string sourceLog, LogLineMetadata metadata)
    {
        var tok = new ArgTokenizer(args);
        tok.SkipOpen();

        tok.NextLong(); // entityId — npc state already tracks it
        var npcKey = tok.NextQuotedSpan();
        var delta = tok.NextDouble();

        if (delta <= 0)
            return;

        if (npc.ActiveNpcKey is null)
            return;

        if (!npcKey.SequenceEqual(npc.ActiveNpcKey))
            return;

        bus.Publish(new DeltaFavorReceived(npc.ActiveNpcKey, delta, metadata));
    }
}
