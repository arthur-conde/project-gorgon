namespace Mithril.Shared.Logging;

/// <summary>
/// L0.5 (#532): a Player.log line classified as a session-level system signal.
/// The small fixed set is enumerated by <see cref="Kind"/>; <see cref="Data"/>
/// is the post-prefix payload (e.g. for <see cref="SystemSignalKind.AreaLoading"/>
/// the area name; for <see cref="SystemSignalKind.LoginBanner"/> the banner
/// body sans the <c>[ts] </c> bracket). Verb-arg parsing is L2's job — L0.5
/// only classifies and trims the envelope.
///
/// <para>Pass-through fields (<see cref="Timestamp"/> / <see cref="Sequence"/>
/// / <see cref="ReadMonotonicTicks"/> / <see cref="Raw"/>) follow the same
/// rules as <see cref="LocalPlayerLogLine"/>. <see cref="SystemSignalKind.SessionLifecycle"/>
/// lines lack a <c>[ts]</c> prefix in the source file but inherit the most
/// recently observed gameplay timestamp from <see cref="PlayerLogClock"/>.</para>
/// </summary>
public sealed record SystemSignalLogLine(
    DateTimeOffset Timestamp,
    SystemSignalKind Kind,
    string Data,
    long Sequence,
    long ReadMonotonicTicks,
    string? Raw = null) : IClassifiedPlayerLogLine;
