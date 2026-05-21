namespace Mithril.WorldSim.Player;

/// <summary>
/// The world reconstructed from <c>Player.log</c> (design notebook §Contracts
/// — "Concrete worlds"). Subscribes to the L1 classified pipe
/// (<c>IClassifiedPlayerLogStream</c>) as its source-stream producer; merges
/// frames by timestamp; dispatches to registered folders; resolves composers;
/// publishes domain frames on its bus.
///
/// <para>This is the Phase 0 shell (issue #616) — no folders or composers
/// are registered yet. Per-folder migrations (skills, recipes, inventory,
/// effects, position, pins, weather, areas, celestial, sessions, quests,
/// WoP-discovery, …) land in Phase 1+ issues and add their corresponding
/// folder + property pair to this interface at that time. Until then the
/// shell carries the clock + bus + merger only.</para>
/// </summary>
public interface IPlayerWorld : IWorld
{
}
