namespace Mithril.WorldSim;

/// <summary>
/// Marker for change events emitted by <see cref="IFolder{TPayload}"/> instances
/// after applying a frame (principle 10 — folders consume frames and emit change
/// events).
///
/// <para><b>Resolution-graph topology.</b> Change events feed intra-world composers
/// during a frame's resolution: a folder emits change events; composers subscribed
/// to those concrete event types fire and may emit domain frames; resolution
/// continues until no new events are produced. This topology is internal to the
/// world's per-frame dispatch and is unchanged.</para>
///
/// <para><b>Bus consumability.</b> Change events are ALSO published to the world's
/// <see cref="IWorldEventBus"/> alongside domain frames. Single-world consumers
/// (views, modules subscribed directly to one world) MAY subscribe to a concrete
/// change-event type via <c>IWorldEventBus.Subscribe&lt;TConcreteChange&gt;(...)</c>
/// — they are first-class bus output for a single-world surface.</para>
///
/// <para><b>Cross-world surface.</b> Domain frames remain the cross-world consumption
/// contract: views that compose across PlayerWorld and ChatWorld join on domain
/// frames, not change events. A composer's job is to recognize a multi-frame
/// pattern in change events and emit a semantically-new domain frame (e.g., three
/// same-source change events → a single <c>GiftObservation</c>) — composers exist
/// for recognition, NOT for re-labeling a folder's output into a same-shape domain
/// frame.</para>
///
/// <para>Ratified 2026-05-22 against the original "never cross the world boundary"
/// framing; see <c>docs/world-simulator.md</c> "Decisions ratified post-#642" and
/// <a href="https://github.com/moumantai-gg/mithril/issues/643">#643</a>.</para>
/// </summary>
public interface IChangeEvent
{
}
