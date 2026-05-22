namespace Mithril.GameState.WordsOfPower;

/// <summary>
/// World-simulator frame payload for the Player.log Words-of-Power discovery
/// folder (<see cref="PlayerWordOfPowerDiscoveryStateService"/>) — #603. A
/// sibling <see cref="Producers.PlayerWordOfPowerDiscoveryFrameProducer"/>
/// reads classified LocalPlayer envelopes, matches the
/// <c>ProcessBook("You discovered a word of power!", …)</c> grammar via
/// <see cref="Parsing.WordOfPowerDiscoveredParser"/>, and emits one frame per
/// matched line.
///
/// <para>Discovery is monotonic on the PG side — each <c>ProcessBook</c> line
/// carries the code + effect name + description in one go. Rediscovery of the
/// same code is structurally impossible: PG generates a new random string per
/// discovery, so a chat-spent code can never be re-Known by a fresh
/// <c>ProcessBook</c> event for the same code (see #603 spec).</para>
/// </summary>
/// <param name="Code">The Word-of-Power code — a random uppercase string
/// generated per-discovery (e.g. <c>CHUCKMRYJ</c>, <c>BWUBGUCH</c>).</param>
/// <param name="EffectName">Player-facing effect name (e.g. "Anemia", "Fast Swimmer").</param>
/// <param name="Description">Player-facing effect description.</param>
public sealed record WordOfPowerDiscoveryFrame(
    string Code,
    string EffectName,
    string Description);
