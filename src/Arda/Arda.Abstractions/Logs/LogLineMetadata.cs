namespace Arda.Abstractions.Logs;

/// <summary>
/// Minimal metadata attached at the L0/L1 boundary. No structural position
/// data (byte offsets, file paths, coordinates) leaks to consumers — those
/// are internal concerns of the source coordinator used for resumption and
/// deduplication only.
/// <para>
/// Cross-world correlation at the composition layer (L4) uses
/// <see cref="Timestamp"/> for semantic matching and <see cref="ReadOn"/>
/// for sub-second tiebreaking.
/// </para>
/// </summary>
public readonly record struct LogLineMetadata(
    /// <summary>
    /// The timestamp extracted from the line prefix, if present. For Player.log
    /// this is UTC with zero offset; for chat logs this carries the originating
    /// session's local UTC offset.
    /// </summary>
    DateTimeOffset? Timestamp,

    /// <summary>
    /// Wall-clock instant when the tailer captured this batch. Sampled once per
    /// poll cycle via <see cref="TimeProvider.GetUtcNow"/> and stamped on every
    /// line in the batch. Used for cross-world correlation tiebreaking at the
    /// composition layer.
    /// </summary>
    DateTimeOffset ReadOn,

    /// <summary>
    /// <c>true</c> while processing historic data from before the ingest engine
    /// started tailing. Flips to <c>false</c> exactly once per stream lifetime
    /// and never reverts. The source coordinator signals "caught up" when the
    /// tail offset reaches EOF and no more historical files remain.
    /// </summary>
    bool IsReplay);
