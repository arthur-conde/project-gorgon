namespace Arda.Abstractions.Logs;

/// <summary>
/// A source of <see cref="LogLine"/> values representing one log family
/// (Player or Chat). Implemented by source coordinators in Arda.Ingest;
/// consumed by world dispatchers (L2/L3) and the composition pipeline (L4).
/// <para>
/// Each family has its own <see cref="ILogLineSource"/>. Lines are emitted
/// in structural source order (deterministic, no cross-source arbitration).
/// The consumer drives the polling cadence — the source yields lines as
/// they become available.
/// </para>
/// </summary>
public interface ILogLineSource
{
    /// <summary>
    /// Asynchronously enumerates game-event lines from this log family.
    /// Lines are yielded in source order. The enumeration completes when
    /// the source is disposed or the <paramref name="ct"/> is cancelled.
    /// </summary>
    IAsyncEnumerable<LogLine> Lines(CancellationToken ct = default);
}
