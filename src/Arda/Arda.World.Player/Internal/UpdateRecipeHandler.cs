using Arda.Abstractions.Logs;
using Arda.Dispatch;

namespace Arda.World.Player.Internal;

/// <summary>
/// Thin dispatch adapter routing <c>ProcessUpdateRecipe</c> to <see cref="Player.OnUpdateRecipe"/>.
/// </summary>
internal sealed class UpdateRecipeHandler(Player player) : IFrameHandler
{
    public void Handle(ReadOnlySpan<char> args, string sourceLog, LogLineMetadata metadata)
        => player.OnUpdateRecipe(args, sourceLog, metadata);
}
