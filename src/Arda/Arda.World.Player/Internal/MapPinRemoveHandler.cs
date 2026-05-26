using Arda.Abstractions.Logs;
using Arda.Dispatch;

namespace Arda.World.Player.Internal;

/// <summary>
/// Thin dispatch adapter routing <c>ProcessMapPinRemove</c> to <see cref="MapPins.OnRemove"/>.
/// </summary>
internal sealed class MapPinRemoveHandler(MapPins pins) : IFrameHandler
{
    public void Handle(ReadOnlySpan<char> args, string sourceLog, LogLineMetadata metadata)
        => pins.OnRemove(args, sourceLog, metadata);
}
