namespace Mithril.WorldSim;

/// <summary>
/// One unit of simulated input (principle 1 — "frame = (timestamp, payload)" — the
/// unifying primitive of the world-simulator architecture). Producers stamp every
/// frame with the event time the frame represents (NOT the wall-clock when the
/// producer fired), so a replay against a recorded source stream reconstructs the
/// same trajectory the live run produced.
/// </summary>
/// <typeparam name="TPayload">
/// The payload type carried by this frame. The world's merger routes a
/// <see cref="Frame{TPayload}"/> to its registered folder by the payload type.
/// </typeparam>
public readonly record struct Frame<TPayload>(
    DateTimeOffset Timestamp,
    TPayload Payload);
