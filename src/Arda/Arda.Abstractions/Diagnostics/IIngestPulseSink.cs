namespace Arda.Abstractions.Diagnostics;

/// <summary>
/// Write-side of the tailer-poll pulse signal. Implemented by Arda.Hosting,
/// consumed by Arda.Ingest poll loops.
/// <para>
/// Lives in <c>Arda.Abstractions</c> rather than alongside the read-side
/// <c>IIngestPulse</c> in Arda.Hosting because Arda.Ingest does not (and
/// should not) reference Arda.Hosting — Hosting is the higher composition
/// layer. Splitting the contract along read/write lines keeps the dependency
/// direction one-way: Ingest depends on Abstractions only.
/// </para>
/// </summary>
public interface IIngestPulseSink
{
    /// <summary>
    /// Records that the tailer for <paramref name="family"/> just completed a
    /// poll iteration of its live-tail loop, regardless of whether new bytes
    /// were available. Acts as a liveness heartbeat for the tailer itself —
    /// distinct from "the game wrote a log line" (which is a downstream
    /// domain event, not a tailer-health signal).
    /// </summary>
    /// <param name="family">Which log family's tailer just polled.</param>
    /// <param name="polledAt">Wall-clock time of the poll.</param>
    /// <param name="bytesRead">Number of bytes read in this poll (0 for empty reads).</param>
    /// <param name="linesEmitted">Number of <c>LogLine</c>s yielded from this poll.</param>
    void RecordPoll(LogFamily family, System.DateTimeOffset polledAt, int bytesRead, int linesEmitted);
}
