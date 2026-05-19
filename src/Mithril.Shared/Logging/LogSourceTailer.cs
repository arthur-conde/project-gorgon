using System.IO;
using System.Text;

namespace Mithril.Shared.Logging;

/// <summary>
/// L0 of the layered log pipeline (#511 / #513): the <b>one and only one
/// service</b> that owns tailing a single log file and turning each new
/// line into a normalized <see cref="RawLogLine"/>. Owns:
///
/// <list type="bullet">
///   <item><b>Tailing.</b> Byte offset across reads, residual partial
///   trailing line buffered for the next read, rotation/truncation
///   detection (<c>fs.Length &lt; _offset</c>), shared-read
///   <see cref="FileShare"/>.</item>
///   <item><b>Per-source timestamp grammar.</b> Delegated to an injected
///   <see cref="ILogSourceClock"/> — <see cref="PlayerLogClock"/> for the
///   Player.log <c>[HH:MM:SS]</c>-UTC grammar, <see cref="ChatLogClock"/>
///   for the chat <c>yy-MM-dd HH:mm:ss\t</c>-local grammar. The clock
///   does the work; the tailer only knows "ask it." Downstream of L0 no
///   consumer ever needs to know which grammar applied — every
///   <see cref="RawLogLine.Timestamp"/> is already a TZ-correct
///   <see cref="DateTimeOffset"/>.</item>
///   <item><b>Per-source monotonic <see cref="RawLogLine.Sequence"/>.</b>
///   The line's byte offset within the current physical source file. Each
///   emitted line starts at a strictly greater offset than the previous,
///   so <see cref="RawLogLine.Sequence"/> is strictly monotonic within a
///   tailing session. Restart-stable while the file hasn't been truncated:
///   a mid-session Mithril restart re-seeds to some byte N, the next
///   line's <see cref="RawLogLine.Sequence"/> is N+(intra-batch offset),
///   matching what the pre-restart tailer would have emitted — so an L1
///   high-water filter (#511 deliverable 3) keyed on
///   <see cref="RawLogLine.Sequence"/> continues to skip already-processed
///   lines without double-counting. On true rotation/truncation
///   (<c>fs.Length &lt; _offset</c>) the sequence space resets to 0
///   alongside the file offset; this is correct, because the consumer's
///   high-water key was scoped to the file that no longer exists.</item>
///   <item><b>Per-batch <see cref="RawLogLine.ReadMonotonicTicks"/>.</b>
///   Sampled once per <see cref="ReadNew"/> call via
///   <see cref="TimeProvider.GetTimestamp"/> and stamped on every line in
///   the batch — the tick value tells L1 / a #523 Tier-3 consumer "this
///   batch was tailed at monotonic instant T," which is the only
///   sub-second cross-source ordering signal available between Player.log
///   and chat. Per-batch (not per-line) granularity is the right
///   resolution for cross-source tiebreaking (within-source ordering is
///   already given by <see cref="RawLogLine.Sequence"/>) and keeps the
///   per-line cost zero on the hot path.</item>
/// </list>
///
/// <para><b>What this class does NOT own</b> — the explicit out-of-scope
/// list from #513 (these are L1 / L2 / L3 / module concerns):</para>
/// <list type="bullet">
///   <item>The session-start seek (<c>SeedToSessionStart</c>) for
///   Player.log. That's a Player-specific seed strategy and stays in
///   <see cref="PlayerLogTailReader"/>.</item>
///   <item>The directory enumeration + per-file fan-out for chat logs.
///   That stays in <see cref="ChatLogTailReader"/>; the tailer is
///   single-file by design (one tailer per chat file).</item>
///   <item>Subscription / channel fan-out / replay buffer — that's L1
///   (<see cref="PlayerLogStream"/> / <see cref="ChatLogStream"/>).</item>
///   <item>Per-message error containment, drop accounting, ReplayMode,
///   verb recognition, domain interpretation — L1 / L2 / L3 per #511.</item>
/// </list>
/// </summary>
internal sealed class LogSourceTailer
{
    private readonly string _path;
    private readonly ILogSourceClock _clock;
    private readonly TimeProvider _time;
    private long _offset;
    private byte[] _residual = Array.Empty<byte>();

    public LogSourceTailer(string path, ILogSourceClock clock, TimeProvider time)
    {
        _path = path ?? throw new ArgumentNullException(nameof(path));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
        _time = time ?? throw new ArgumentNullException(nameof(time));
    }

    /// <summary>
    /// Byte offset at which the next <see cref="ReadNew"/> begins. The
    /// wrapper classes (<see cref="PlayerLogTailReader"/>,
    /// <see cref="ChatLogTailReader"/>) write this directly to implement
    /// their seed strategies (session-marker scan / skip-to-current-end);
    /// no <c>Seed*</c> verbs live on the tailer because the seed policy
    /// is the source-specific concern, not the tail mechanics.
    /// </summary>
    public long Offset
    {
        get => _offset;
        set => _offset = value;
    }

    /// <summary>
    /// Read every new complete line written since the last call. Returns
    /// empty when nothing new is available, when a partial trailing line
    /// is the only new content (it is buffered as residual and emitted
    /// next time a newline arrives), or when the file has been truncated
    /// (offset is reset to 0 and the next call reads from the start).
    /// </summary>
    public IReadOnlyList<RawLogLine> ReadNew()
    {
        if (!File.Exists(_path)) return Array.Empty<RawLogLine>();
        using var fs = new FileStream(_path, FileMode.Open, FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete);
        if (fs.Length < _offset) { _offset = 0; _residual = Array.Empty<byte>(); } // truncation / rotation
        if (fs.Length == _offset) return Array.Empty<RawLogLine>();

        // The chunk we're about to assemble starts at this file offset —
        // residual is the prefix that was already read in the prior call
        // but didn't terminate in a newline. Sequence numbers below are
        // computed against this base so each line's Sequence equals its
        // start-byte offset in the file.
        var chunkStartOffset = _offset - _residual.Length;

        fs.Seek(_offset, SeekOrigin.Begin);
        var len = (int)Math.Min(int.MaxValue, fs.Length - _offset);
        var buf = new byte[_residual.Length + len];
        Buffer.BlockCopy(_residual, 0, buf, 0, _residual.Length);
        var read = fs.Read(buf, _residual.Length, len);
        var total = _residual.Length + read;

        // Find last newline; everything after becomes residual.
        var lastNl = -1;
        for (var i = total - 1; i >= 0; i--) { if (buf[i] == (byte)'\n') { lastNl = i; break; } }
        if (lastNl < 0)
        {
            _residual = buf[..total];
            _offset += read;
            return Array.Empty<RawLogLine>();
        }

        var text = Encoding.UTF8.GetString(buf, 0, lastNl + 1);
        _residual = buf[(lastNl + 1)..total];
        _offset += read;

        // Split keeping empty entries — we need positional info so the
        // byte-offset accumulator (used to compute Sequence) stays in
        // sync with the source file. The trailing empty entry that
        // follows the final '\n' (Split keeps it) gets skipped naturally
        // by the trimmed.Length == 0 check below.
        var parts = text.Split('\n');
        var lines = new List<(long fileOffset, string trimmed)>(parts.Length);
        var byteOffset = chunkStartOffset;
        foreach (var part in parts)
        {
            var partUtf8Len = Encoding.UTF8.GetByteCount(part);
            var trimmed = part.EndsWith('\r') ? part[..^1] : part;
            if (trimmed.Length > 0) lines.Add((byteOffset, trimmed));
            byteOffset += partUtf8Len + 1; // +1 for the '\n' delimiter (over-counts past the last line; harmless)
        }

        if (lines.Count == 0) return Array.Empty<RawLogLine>();

        // Anchor on the whole batch BEFORE stamping any line. The
        // Player-side clock's mtime fallback walks the full batch to
        // count midnight rollovers and back-derive the starting date;
        // passing only the first line would give it a wrong answer for a
        // cold-start backlog that spans multiple midnights. The chat-side
        // clock no-ops EnsureAnchored. Idempotent after the first call.
        var lineTexts = new string[lines.Count];
        for (var i = 0; i < lines.Count; i++) lineTexts[i] = lines[i].trimmed;
        _clock.EnsureAnchored(lineTexts, () => File.GetLastWriteTimeUtc(_path));

        var readTicks = _time.GetTimestamp();
        var result = new List<RawLogLine>(lines.Count);
        foreach (var (fileOffset, trimmed) in lines)
        {
            result.Add(new RawLogLine(
                Timestamp: _clock.StampForLine(trimmed),
                Line: trimmed,
                Sequence: fileOffset,
                ReadMonotonicTicks: readTicks));
        }
        return result;
    }
}
