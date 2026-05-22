namespace Mithril.GameState.Chat;

/// <summary>
/// ChatWorld passthrough folder for player-typed chat messages (#603) — the
/// minimal surface that exposes <see cref="PlayerChatLineFrame"/>s on the
/// ChatWorld bus as <see cref="ChatPlayerLineObserved"/> change events. No
/// state is retained; the folder's only role is to give a producer-emitted
/// frame a bus presence per the world-sim contract (frames must route to a
/// folder for the world to dispatch them).
///
/// <para><b>Naming.</b> Follows #657 — folder interfaces take the form
/// <c>I&lt;World&gt;&lt;Domain&gt;State</c> for stateful folders. This is a
/// pure pass-through with no read-side TryGet, so the interface only carries
/// the marker shape — consumers subscribe to <see cref="ChatPlayerLineObserved"/>
/// frames on the ChatWorld bus.</para>
/// </summary>
public interface IPlayerChatLineLog
{
}
