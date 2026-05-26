using Arda.Abstractions.Logs;
using Arda.Dispatch;

namespace Arda.World.Player.Internal;

/// <summary>
/// Thin dispatch adapter routing <c>ProcessAddEffects</c> to <see cref="Effects.OnAdd"/>.
/// </summary>
internal sealed class AddEffectsHandler(Effects effects) : IFrameHandler
{
    public void Handle(ReadOnlySpan<char> args, string sourceLog, LogLineMetadata metadata)
        => effects.OnAdd(args, sourceLog, metadata);
}
