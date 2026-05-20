namespace Mithril.Shared.Logging;

/// <summary>
/// L0.5 of the layered log pipeline (#511 / #532): a Player.log line classified
/// as <c>[ts] LocalPlayer: Process*(…)</c>, with the <c>LocalPlayer:</c> actor
/// envelope already eaten. Downstream of L0.5, no consumer ever sees or
/// re-matches the actor envelope — <see cref="Data"/> is the bare
/// <c>Verb(args)</c> string ready for L2's verb-keyed dispatch.
///
/// <para><see cref="Timestamp"/>, <see cref="Sequence"/>, and
/// <see cref="ReadMonotonicTicks"/> pass through verbatim from the originating
/// <see cref="RawLogLine"/> — L0.5 owns classification + envelope-eating only,
/// not re-timestamping.</para>
///
/// <para><b><see cref="Raw"/></b> is <c>null</c> unless the infra diagnostic
/// setting (sampled at emit by the L0.5 router) is on, in which case it
/// carries the exact source <see cref="RawLogLine.Line"/>. The L1 diagnostic
/// mirror (#511 deliverable 3) reads <see cref="Raw"/> rather than maintaining
/// a parallel raw-capture path. <c>null</c> by design rather than empty-string
/// so the typical zero-allocation path is observable: an off-toggle costs no
/// per-line string allocation.</para>
/// </summary>
public sealed record LocalPlayerLogLine(
    DateTimeOffset Timestamp,
    string Data,
    long Sequence,
    long ReadMonotonicTicks,
    string? Raw = null) : IClassifiedPlayerLogLine;
