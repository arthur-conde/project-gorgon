namespace Mithril.WorldSim;

/// <summary>
/// Marker for change events emitted by <see cref="IFolder{TPayload}"/> instances
/// after applying a frame (principle 10 — folders consume frames and emit change
/// events). Change events are world-internal: they flow to intra-world composers
/// during a frame's resolution, but never cross the world boundary. Cross-world
/// consumers (views) see only the domain frames the world's composers chose to
/// publish to the bus.
/// </summary>
public interface IChangeEvent
{
}
