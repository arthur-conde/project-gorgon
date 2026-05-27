namespace Arda.World.Player.Internal;

/// <summary>
/// Well-known argument values for LOADING_LEVEL that indicate a
/// non-area transition (character select or disconnect).
/// </summary>
internal static class WellKnownArgs
{
    internal static readonly string[] NonAreaKeys = ["ChooseCharacter", "ReconnectToServer"];

    internal static bool IsNonAreaKey(ReadOnlySpan<char> args)
    {
        foreach (var key in NonAreaKeys)
        {
            if (args.SequenceEqual(key))
                return true;
        }
        return false;
    }
}
