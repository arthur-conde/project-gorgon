namespace Mithril.GameState.WordsOfPower;

/// <summary>
/// View-emitted event signalling that a Word-of-Power code's effective state
/// has changed (#603). Fires on:
/// <list type="bullet">
///   <item>First chat-utterance observation of a Known code (state flips from
///   <see cref="WordOfPowerKnowledge.Known"/> to <see cref="WordOfPowerKnowledge.Spent"/>).</item>
///   <item>First discovery observation of a code (entry materialises; state
///   defaults to <see cref="WordOfPowerKnowledge.Known"/>).</item>
/// </list>
///
/// <para>Spent → Known is structurally impossible per the #603 spec
/// (monotonic Spent), so the event never fires for that direction.</para>
///
/// <para><b>Naming.</b> Past-tense participle per #657; NO world prefix on
/// view-emitted events (view bus, not world bus).</para>
/// </summary>
/// <param name="Code">The Word-of-Power code whose state changed.</param>
/// <param name="State">The new effective state.</param>
/// <param name="Timestamp">UTC timestamp of the triggering event (the chat
/// utterance for a Known→Spent flip, the discovery timestamp for a fresh
/// materialisation).</param>
public readonly record struct WordOfPowerKnowledgeChanged(
    string Code,
    WordOfPowerKnowledge State,
    DateTime Timestamp);
