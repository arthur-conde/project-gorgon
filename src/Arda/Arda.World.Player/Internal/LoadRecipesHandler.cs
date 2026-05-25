using Arda.Abstractions.Logs;
using Arda.Dispatch;

namespace Arda.World.Player.Internal;

/// <summary>
/// Thin dispatch adapter routing <c>ProcessLoadRecipes</c> to <see cref="Player.OnLoadRecipes"/>.
/// </summary>
internal sealed class LoadRecipesHandler(Player player) : IFrameHandler
{
    public void Handle(ReadOnlySpan<char> args, string sourceLog, LogLineMetadata metadata)
        => player.OnLoadRecipes(args, sourceLog, metadata);
}
