namespace Mithril.GameState.Inventory;

/// <summary>
/// World-simulator frame payload for the chat-side inventory folder
/// (<see cref="ChatInventoryStateService"/>) — #602. A sibling
/// <see cref="Producers.ChatInventoryFrameProducer"/> reads chat
/// <see cref="Mithril.Shared.Logging.RawLogLine"/> envelopes, parses the
/// <c>[Status]</c> channel via <see cref="InventoryStatusChatParser"/>, and
/// emits one frame per matched line.
///
/// <para>Single-shape payload (unlike the multi-subtype
/// <see cref="PlayerInventoryFrame"/>) — the chat side has only one signal of
/// interest. Kept as a record type rather than the parser's tuple so a future
/// chat-side observation type (e.g. a vault deposit chat line, if PG adds
/// one) can join the same dispatch path without breaking existing wiring.</para>
/// </summary>
/// <param name="DisplayName">Chat-side display name as parsed verbatim from
/// the <c>[Status]</c> line.</param>
/// <param name="Count">Authoritative stack-size count from the chat line.
/// Always <c>&gt;= 1</c>.</param>
public sealed record ChatInventoryObservationFrame(string DisplayName, int Count);
