namespace Mithril.Shared.Logging;

/// <summary>
/// L0.5 (#556) common interface over the three classified-line records
/// emitted by the L0.5 layer:
/// <see cref="LocalPlayerLogLine"/>, <see cref="CombatActorLogLine"/>, and
/// <see cref="SystemSignalLogLine"/>. It defines a closed set — every
/// implementer is one of those three records; future typed pipes added at
/// L0.5 must implement it.
///
/// <para>The unified <see cref="IClassifiedPlayerLogStream"/> publishes
/// envelopes typed as this interface so cross-pipe-ordering-sensitive
/// consumers (Pin, Weather, Position — see #556 §1) can observe every
/// classified line in source-Sequence order through one subscription. The
/// per-Kind typed pipes are derived views; a consumer that needs only one
/// Kind subscribes via <see cref="ILocalPlayerLogStream"/> / friends and
/// the L0.5 splitter routes there.</para>
///
/// <para>Interface members map directly to the primary-ctor properties on
/// each concrete record — no new state, no migration of consumers that
/// already use the concrete types.</para>
/// </summary>
public interface IClassifiedPlayerLogLine
{
    DateTimeOffset Timestamp { get; }
    string Data { get; }
    long Sequence { get; }
    long ReadMonotonicTicks { get; }
    string? Raw { get; }
}
