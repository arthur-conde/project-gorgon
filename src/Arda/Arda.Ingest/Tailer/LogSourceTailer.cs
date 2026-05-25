using System.Buffers;
using System.Text;
using Arda.Ingest.Internal;

namespace Arda.Ingest.Tailer;

/// <summary>
/// L0 mechanical primitive: tails a single log file and produces batches of
/// line spans without per-line string allocation. Owns:
/// <list type="bullet">
///   <item><b>Byte-offset tracking.</b> Maintains the file position across
///   reads. Advances monotonically within a session; resets to 0 on
///   truncation/rotation detection.</item>
///   <item><b>Residual buffering.</b> A partial trailing line (no terminating
///   newline yet) is buffered and prepended to the next read.</item>
///   <item><b>Rotation/truncation detection.</b> If the file length drops
///   below the current offset, the file has been replaced — offset resets
///   to 0 and residual is discarded.</item>
///   <item><b>Pooled buffer output.</b> Decoded characters land in an
///   <see cref="ArrayPool{T}"/>-rented buffer. Line boundaries are computed
///   by scanning for <c>\n</c>. No <c>string.Split</c>, no per-line heap
///   allocation.</item>
/// </list>
/// <para>
/// This class does NOT own timestamp parsing, classification, or any
/// knowledge of log grammar. It is a purely mechanical bytes-to-char-spans
/// engine. The <see cref="Internal.TailedBatch"/> it produces is consumed
/// by the clock and classifier at L1.
/// </para>
/// </summary>
internal sealed class LogSourceTailer
{
    private readonly string _path;
    private long _offset;
    private byte[] _residual = [];
    private bool _hasCaughtUp;

    public LogSourceTailer(string path)
    {
        _path = path ?? throw new ArgumentNullException(nameof(path));
    }

    /// <summary>The file path being tailed.</summary>
    public string Path => _path;

    /// <summary>
    /// <c>true</c> after a <see cref="ReadNew"/> call in which the tailer's
    /// offset reached the file's length (no unread bytes remain beyond any
    /// residual partial line). Used by coordinators to determine when
    /// historical replay is complete and the stream is live.
    /// <para>
    /// This property resolves a correctness gap: if the game writes data
    /// faster than the tailer's poll interval, the coordinator would never
    /// observe an empty batch and <c>IsReplay</c> would remain <c>true</c>
    /// indefinitely. Checking EOF after each successful read ensures the
    /// transition fires as soon as all pre-existing content is consumed,
    /// regardless of whether new content has already arrived.
    /// </para>
    /// </summary>
    public bool HasCaughtUp => _hasCaughtUp;

    /// <summary>
    /// Byte offset at which the next <see cref="ReadNew"/> begins. Source
    /// coordinators write this to implement seed strategies (session-start
    /// scan, skip-to-EOF).
    /// </summary>
    public long Offset
    {
        get => _offset;
        set
        {
            _offset = value;
            _residual = [];
        }
    }

    /// <summary>
    /// Read all new complete lines written since the last call. Returns an
    /// empty batch when nothing new is available, when a partial trailing
    /// line is the only new content (buffered as residual), or when the
    /// file has been truncated (offset resets; next call reads from start).
    /// <para>
    /// The returned <see cref="TailedBatch"/> owns a pooled buffer that
    /// MUST be disposed after processing.
    /// </para>
    /// </summary>
    public TailedBatch ReadNew()
    {
        if (!File.Exists(_path))
            return default;

        using var fs = new FileStream(
            _path, FileMode.Open, FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete);

        var fileLength = fs.Length;

        if (fileLength < _offset)
        {
            _offset = 0;
            _residual = [];
            _hasCaughtUp = false;
        }

        if (fileLength == _offset)
        {
            _hasCaughtUp = true;
            return default;
        }

        fs.Seek(_offset, SeekOrigin.Begin);
        var bytesToRead = (int)Math.Min(int.MaxValue, fileLength - _offset);
        var rawBuf = ArrayPool<byte>.Shared.Rent(_residual.Length + bytesToRead);

        try
        {
            Buffer.BlockCopy(_residual, 0, rawBuf, 0, _residual.Length);
            var bytesRead = fs.Read(rawBuf, _residual.Length, bytesToRead);
            var totalBytes = _residual.Length + bytesRead;

            // EOF reached when we've read everything that was available at
            // the time we sampled the file length.
            _hasCaughtUp = (_offset + bytesRead) >= fileLength;

            var lastNl = FindLastNewline(rawBuf, totalBytes);
            if (lastNl < 0)
            {
                // No complete line yet — buffer the partial content as residual.
                // Allocates via the range indexer. Safe because game log lines
                // are bounded (typically <500 chars); under pathological conditions
                // (no newline across many reads) this grows but cannot exceed the
                // file size.
                _residual = rawBuf[..totalBytes];
                _offset += bytesRead;
                return default;
            }

            var charCount = Encoding.UTF8.GetCharCount(rawBuf, 0, lastNl + 1);
            var charBuf = ArrayPool<char>.Shared.Rent(charCount);
            try
            {
                Encoding.UTF8.GetChars(rawBuf, 0, lastNl + 1, charBuf, 0);

                _residual = rawBuf[(lastNl + 1)..totalBytes];
                _offset += bytesRead;

                var (lines, lineCount) = FindLinesBoundaries(charBuf, charCount);

                // Ownership transfers to TailedBatch — caller returns via Dispose.
                return new TailedBatch
                {
                    Buffer = charBuf,
                    ContentLength = charCount,
                    Lines = lines,
                    LineCount = lineCount
                };
            }
            catch
            {
                ArrayPool<char>.Shared.Return(charBuf);
                throw;
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(rawBuf);
        }
    }

    private static int FindLastNewline(byte[] buf, int length)
    {
        for (var i = length - 1; i >= 0; i--)
        {
            if (buf[i] == (byte)'\n')
                return i;
        }
        return -1;
    }

    private static ((int Start, int Length)[] Lines, int Count) FindLinesBoundaries(
        char[] buf, int contentLength)
    {
        var lines = ArrayPool<(int, int)>.Shared.Rent(64);
        var count = 0;
        var lineStart = 0;

        for (var i = 0; i < contentLength; i++)
        {
            if (buf[i] != '\n') continue;

            var lineEnd = i > 0 && buf[i - 1] == '\r' ? i - 1 : i;
            var length = lineEnd - lineStart;

            if (length > 0)
            {
                if (count >= lines.Length)
                {
                    var bigger = ArrayPool<(int, int)>.Shared.Rent(lines.Length * 2);
                    Array.Copy(lines, bigger, count);
                    ArrayPool<(int, int)>.Shared.Return(lines);
                    lines = bigger;
                }
                lines[count++] = (lineStart, length);
            }

            lineStart = i + 1;
        }

        return (lines, count);
    }
}
