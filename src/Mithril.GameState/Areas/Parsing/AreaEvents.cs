using Mithril.Shared.Logging;

namespace Mithril.GameState.Areas.Parsing;

/// <summary>
/// Player transitioned to a new area (or out of game-world, e.g. character
/// select / disconnect). Parsed from the <c>LOADING LEVEL Area&lt;Name&gt;</c>
/// line every PG zone change emits.
///
/// <see cref="AreaKey"/> is the area code (matches <c>areas.json</c> keys
/// exactly: <c>"AreaSerbule"</c>, <c>"AreaEltibule"</c>, …) when in a real
/// area, or <c>null</c> for the character-select screen, disconnect, or any
/// non-Area level load. Consumers should treat <c>null</c> as "current area
/// is unknown" — e.g. Gandalf chest commits during a null-area window persist
/// with <c>Area = null</c> and self-heal on the next portal.
/// </summary>
public sealed record AreaTransitionEvent(DateTime Timestamp, string? AreaKey)
    : LogEvent(Timestamp);
