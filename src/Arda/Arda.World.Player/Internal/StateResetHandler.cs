using Arda.Abstractions.Logs;
using Arda.Dispatch;

namespace Arda.World.Player.Internal;

/// <summary>
/// Registered for <c>LOADING_LEVEL</c> alongside <see cref="Map"/>. Resets Inventory,
/// Player, and Npc state when the player leaves the world (bare LOADING_LEVEL,
/// ChooseCharacter, ReconnectToServer) or transitions between areas (Npc only).
/// <para>
/// Dispatch order: Map runs first (clears CurrentArea), then this handler resets
/// downstream state. Prevents stale data from the previous character/area persisting.
/// </para>
/// </summary>
internal sealed class StateResetHandler : IFrameHandler
{
    private static readonly string[] NonAreaKeys = ["ChooseCharacter", "ReconnectToServer"];

    private readonly Inventory _inventory;
    private readonly Player _player;
    private readonly Npc _npc;

    public StateResetHandler(Inventory inventory, Player player, Npc npc)
    {
        _inventory = inventory;
        _player = player;
        _npc = npc;
    }

    public void Handle(ReadOnlySpan<char> args, string sourceLog, LogLineMetadata metadata)
    {
        if (args.IsEmpty || IsNonAreaKey(args))
        {
            _inventory.Reset();
            _player.Reset();
            _npc.Reset();
        }
        else
        {
            // Area transition: only Npc needs clearing (stale interaction context)
            _npc.Reset();
        }
    }

    private static bool IsNonAreaKey(ReadOnlySpan<char> args)
    {
        foreach (var key in NonAreaKeys)
        {
            if (args.SequenceEqual(key))
                return true;
        }
        return false;
    }
}
