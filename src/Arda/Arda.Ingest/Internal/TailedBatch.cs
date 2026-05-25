using System.Buffers;

namespace Arda.Ingest.Internal;

/// <summary>
/// A batch of lines produced by a single <see cref="Tailer.LogSourceTailer.ReadNew"/>
/// call. Wraps a pooled <c>char[]</c> buffer and the line boundaries within it.
/// <para>
/// The buffer is rented from <see cref="ArrayPool{T}.Shared"/> and must be returned
/// after processing via <see cref="Dispose"/>. Lines are represented as start/length
/// pairs into the buffer — no per-line string allocation occurs at this layer.
/// </para>
/// </summary>
internal struct TailedBatch : IDisposable
{
    /// <summary>The pooled char buffer containing decoded UTF-8 text.</summary>
    public char[] Buffer { get; init; }

    /// <summary>
    /// The valid length within <see cref="Buffer"/> (the buffer may be larger
    /// than the content due to pool rounding).
    /// </summary>
    public int ContentLength { get; init; }

    /// <summary>
    /// Line boundaries as (start, length) pairs into <see cref="Buffer"/>.
    /// Each entry represents one complete line (excluding the <c>\n</c> delimiter).
    /// </summary>
    public (int Start, int Length)[] Lines { get; init; }

    /// <summary>Number of valid entries in <see cref="Lines"/>.</summary>
    public int LineCount { get; init; }

    /// <summary>Whether this batch contains any lines.</summary>
    public bool IsEmpty => LineCount == 0;

    /// <summary>
    /// Returns the pooled buffers to <see cref="ArrayPool{T}.Shared"/>.
    /// Must be called after the batch has been fully processed.
    /// </summary>
    public void Dispose()
    {
        if (Buffer is not null)
            ArrayPool<char>.Shared.Return(Buffer);
        if (Lines is not null)
            ArrayPool<(int, int)>.Shared.Return(Lines);
    }
}
