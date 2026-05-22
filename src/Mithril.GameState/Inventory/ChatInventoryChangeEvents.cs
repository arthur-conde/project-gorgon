using Mithril.WorldSim;

namespace Mithril.GameState.Inventory;

/// <summary>
/// Folder-emitted change event for a chat <c>[Status] X xN added to inventory.</c>
/// (or <c>[Status] X added to inventory.</c> implying N=1) observation (#602).
/// Carries the chat-side <c>DisplayName</c> (e.g. <c>"Egg"</c>) and the
/// authoritative stack-size count — the only signal the game emits that
/// carries stack-size information for fresh additions.
///
/// <para>Display-name → InternalName resolution happens at the view layer
/// (the view holds the reference-data join). The chat folder stores the raw
/// chat-side observation in a name-keyed time-series so the view's TTL-windowed
/// correlator (see <see cref="IInventoryView"/>) can pair it with a matching
/// <see cref="PlayerInventoryAdded"/>.</para>
///
/// <para><b>Naming.</b> Follows #657 — past-tense participle, no
/// <c>Event</c> suffix, mandatory <c>Chat</c> world prefix.</para>
/// </summary>
/// <param name="DisplayName">Chat-side display name verbatim from the
/// <c>[Status]</c> line (e.g. <c>"Egg"</c>, <c>"Guava"</c>). View-layer
/// resolves to <c>InternalName</c>.</param>
/// <param name="Count">Stack-size count from the chat line. Always
/// <c>&gt;= 1</c> — the <c>x1</c> form is implicit (single addition) per
/// the chat grammar.</param>
/// <param name="Timestamp">UTC timestamp of the source chat line. The chat
/// log clock (<see cref="Mithril.Shared.Logging.ChatLogClock"/>) is responsible
/// for the TZ fold; this field is already UTC by L0.5 boundary.</param>
public readonly record struct ChatInventoryObserved(
    string DisplayName,
    int Count,
    DateTime Timestamp) : IChangeEvent;
