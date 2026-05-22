using Mithril.WorldSim;

namespace Mithril.GameState.WordsOfPower;

/// <summary>
/// Folder-emitted change event for a Player.log Word-of-Power discovery
/// (#603). Fired by <see cref="PlayerWordOfPowerDiscoveryStateService"/> when a
/// new <c>ProcessBook("You discovered a word of power!", …)</c> line lands.
/// Rediscovery of an already-known code suppresses the emit (PG can't actually
/// generate the same code twice, but defence-in-depth — the folder elides
/// duplicates so downstream view subscribers see at most one
/// <see cref="PlayerWordOfPowerDiscovered"/> per code).
///
/// <para><b>Naming.</b> Past-tense participle per #657; mandatory <c>Player</c>
/// world prefix on folder-emitted events.</para>
/// </summary>
/// <param name="Code">The discovered Word-of-Power code.</param>
/// <param name="EffectName">Player-facing effect name.</param>
/// <param name="Description">Player-facing effect description.</param>
/// <param name="Timestamp">UTC timestamp of the source <c>ProcessBook</c> line —
/// event-time, NOT wall-clock (see #603 spec acceptance criteria).</param>
public readonly record struct PlayerWordOfPowerDiscovered(
    string Code,
    string EffectName,
    string Description,
    DateTime Timestamp) : IChangeEvent;
