using Arda.Abstractions.Logs;
using Arda.Dispatch;

namespace Arda.World.Player.Internal;

/// <summary>
/// Thin dispatch adapter routing <c>ProcessMapPinAdd</c> to <see cref="MapPins.OnAdd"/>.
/// </summary>
internal sealed class MapPinAddHandler(MapPins pins) : IFrameHandler
{
    public void Handle(ReadOnlySpan<char> args, string sourceLog, LogLineMetadata metadata)
        => pins.OnAdd(args, sourceLog, metadata);
}
