using Arda.Abstractions.Logs;
using Arda.Dispatch;

namespace Arda.World.Player.Internal;

/// <summary>
/// Thin dispatch adapter routing <c>ProcessAddItem</c> to <see cref="Inventory.OnAddItem"/>.
/// </summary>
internal sealed class AddItemHandler(Inventory inventory) : IFrameHandler
{
    public void Handle(ReadOnlySpan<char> args, string sourceLog, LogLineMetadata metadata)
        => inventory.OnAddItem(args, sourceLog, metadata);
}
