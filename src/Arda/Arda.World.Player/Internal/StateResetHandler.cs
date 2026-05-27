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
    private readonly Inventory _inventory;
    private readonly Player _player;
    private readonly Npc _npc;
    private readonly Vault _vault;
    private readonly Weather _weather;
    private readonly Session _session;
    private readonly Celestial _celestial;
    private readonly MapPins _mapPins;
    private readonly Position _position;
    private readonly Effects _effects;
    private readonly Quest _quest;

    public StateResetHandler(
        Inventory inventory, Player player, Npc npc, Vault vault,
        Weather weather, Session session, Celestial celestial, MapPins mapPins,
        Position position, Effects effects, Quest quest)
    {
        _inventory = inventory;
        _player = player;
        _npc = npc;
        _vault = vault;
        _weather = weather;
        _session = session;
        _celestial = celestial;
        _mapPins = mapPins;
        _position = position;
        _effects = effects;
        _quest = quest;
    }

    public void Handle(ReadOnlySpan<char> args, string sourceLog, LogLineMetadata metadata)
    {
        if (args.IsEmpty || WellKnownArgs.IsNonAreaKey(args))
        {
            _inventory.Reset();
            _player.Reset();
            _npc.Reset();
            _vault.Reset();
            _weather.Reset();
            _session.Reset();
            _celestial.Reset();
            _mapPins.Reset();
            _position.Reset();
            _effects.Reset();
            _quest.Reset();
        }
        else
        {
            // Area transition: Npc, vault, weather, map pins, and position are per-area
            _npc.Reset();
            _vault.Reset();
            _weather.Reset();
            _mapPins.Reset();
            _position.Reset();
        }
    }
}
