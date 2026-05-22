namespace Mithril.WorldSim.Chat;

/// <summary>
/// The world reconstructed from PG's chat log files (design notebook §Contracts
/// — "Concrete worlds"). Subscribes to <see cref="Producers.IChatLogReplaySource"/>
/// as its source-stream producer; merges frames by timestamp; dispatches to
/// registered folders; resolves composers; publishes domain frames on its bus.
///
/// <para>This is the Phase 0 shell (issue #617, sibling of #616's
/// <c>IPlayerWorld</c>) — no folders or composers are registered yet. The two
/// chat-side folders the architecture calls for — chat-inventory mirror and
/// chat-WoP spent — land in #602 / #603 respectively and add their
/// corresponding folder + property pair to this interface at that time. Until
/// then the shell carries the clock + bus + merger only.</para>
///
/// <para>Self-scope (Server, Character) is identified intra-source from the
/// chat banner <c>**** Logged In As X. Server Y. Timezone Offset Z.</c> and
/// exposed via the sibling <see cref="IChatSessionService"/> registered
/// alongside the world (principle 7 — both streams self-scope
/// independently).</para>
/// </summary>
public interface IChatWorld : IWorld
{
}
