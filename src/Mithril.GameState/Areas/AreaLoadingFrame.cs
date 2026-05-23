namespace Mithril.GameState.Areas;

/// <summary>
/// World-simulator frame payload for the Player.log area folder
/// (<see cref="PlayerAreaTracker"/>) — #775. A sibling
/// <see cref="Producers.AreaLoadingFrameProducer"/> reads
/// <see cref="Mithril.Shared.Logging.SystemSignalKind.AreaLoading"/> envelopes
/// off the L1 driver, parses them via
/// <see cref="Parsing.AreaTransitionParser"/>, and emits one frame per
/// transition.
///
/// <para><b>Single-shape payload.</b> Unlike <see cref="Skills.SkillFrame"/> /
/// <see cref="Inventory.PlayerInventoryFrame"/> there is only one verb shape
/// (<c>LOADING LEVEL …</c>), so the payload is a single record rather than a
/// closed hierarchy of subtypes. <see cref="AreaKey"/> follows the
/// <see cref="Parsing.AreaTransitionEvent.AreaKey"/> convention: the
/// <c>"Area*"</c> code on a real transition, or <c>null</c> for
/// <c>ChooseCharacter</c> / disconnect / empty body.</para>
/// </summary>
/// <param name="AreaKey">The area code (matches <c>areas.json</c> keys
/// exactly: <c>"AreaSerbule"</c>, <c>"AreaEltibule"</c>, …) when in a real
/// area, or <c>null</c> for the character-select / disconnect / unknown
/// intermediate forms.</param>
public sealed record AreaLoadingFrame(string? AreaKey);
