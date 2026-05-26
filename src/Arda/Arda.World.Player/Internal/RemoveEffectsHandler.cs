using Arda.Abstractions.Logs;
using Arda.Dispatch;

namespace Arda.World.Player.Internal;

/// <summary>
/// Thin dispatch adapter routing <c>ProcessRemoveEffects</c> to <see cref="Effects.OnRemove"/>.
/// </summary>
internal sealed class RemoveEffectsHandler(Effects effects) : IFrameHandler
{
    public void Handle(ReadOnlySpan<char> args, string sourceLog, LogLineMetadata metadata)
        => effects.OnRemove(args, sourceLog, metadata);
}
