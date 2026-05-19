using System.IO;
using System.Text;

namespace Mithril.Shared.Logging;

/// <summary>
/// Player.log seed-strategy wrapper around <see cref="LogSourceTailer"/>.
/// The tail mechanics (offset, residual, rotation, Sequence,
/// ReadMonotonicTicks, timestamp normalization) live in
/// <see cref="LogSourceTailer"/> per #513 — this class owns only the
/// Player-side seek strategy: scan the file backwards for the most
/// recent session-start marker so the consumer always receives the
/// login event regardless of how long ago the session began.
///
/// <para><b>Session markers.</b> Two markers identify a session start:</para>
/// <list type="bullet">
///   <item><c>Logged in as character </c> — the user-facing login banner that
///   carries the authoritative UTC datetime + timezone offset. Consumed by
///   <c>GameSessionService</c> to mint a <c>GameSession</c> and feed
///   <see cref="ISessionAnchor"/>.</item>
///   <item><c>ProcessAddPlayer(</c> — the engine spawn event. Consumed by
///   <c>ActiveCharacterLogSynchronizer</c> to set the active character name.</item>
/// </list>
/// PG emits both within a few lines of one another at the start of each
/// session, but ordering between them isn't guaranteed. The seed picks the
/// <b>earlier</b> of the two recent occurrences so both lines appear in the
/// replay regardless of PG's emission order. If neither is found, the seed
/// falls through to byte 0 (replay everything).
/// </summary>
public sealed class PlayerLogTailReader
{
    private const int SessionScanChunkBytes = 10 * 1024 * 1024;
    private static readonly string[] SessionMarkers =
    [
        "Logged in as character ",
        "ProcessAddPlayer(",
    ];

    private readonly string _path;
    private readonly LogSourceTailer _tailer;

    public PlayerLogTailReader(string path, TimeProvider? time = null, ISessionAnchor? sessionAnchor = null)
    {
        _path = path ?? throw new ArgumentNullException(nameof(path));
        var t = time ?? TimeProvider.System;
        _tailer = new LogSourceTailer(_path, new PlayerLogClock(t, sessionAnchor), t);
    }

    public void SeedToSessionStart()
    {
        if (!File.Exists(_path)) { _tailer.Offset = 0; return; }
        var size = new FileInfo(_path).Length;
        using var fs = new FileStream(_path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);

        // Scan backward in chunks so we find the most recent session marker
        // even in a 100MB+ log. Each chunk overlaps the previous by the longest
        // marker length so a hit at a chunk boundary isn't missed. For each
        // chunk, locate the latest occurrence of EACH marker and keep the
        // smallest resulting line-start offset (the chronologically earlier
        // of the two), so both the banner and ProcessAddPlayer always land
        // in the replay regardless of which PG emits first.
        var overlap = 0;
        foreach (var m in SessionMarkers) if (m.Length > overlap) overlap = m.Length;
        var end = size;
        while (end > 0)
        {
            var chunkSize = (int)Math.Min(SessionScanChunkBytes, end);
            var scanFrom = end - chunkSize;
            fs.Seek(scanFrom, SeekOrigin.Begin);
            var buf = new byte[chunkSize];
            var read = fs.Read(buf, 0, buf.Length);
            var text = Encoding.UTF8.GetString(buf, 0, read);

            long? bestOffset = null;
            foreach (var marker in SessionMarkers)
            {
                var idx = text.LastIndexOf(marker, StringComparison.Ordinal);
                if (idx < 0) continue;
                var lineStart = text.LastIndexOf('\n', idx);
                var startInChunk = lineStart < 0 ? 0 : lineStart + 1;
                var fileOffset = scanFrom + Encoding.UTF8.GetByteCount(text.AsSpan(0, startInChunk));
                if (bestOffset is null || fileOffset < bestOffset.Value) bestOffset = fileOffset;
            }

            if (bestOffset is not null)
            {
                _tailer.Offset = bestOffset.Value;
                return;
            }
            if (scanFrom == 0) break;
            end = scanFrom + overlap; // keep overlap so a marker spanning chunks is caught
        }
        _tailer.Offset = 0; // no marker found anywhere — replay from the top
    }

    public void SeedToEnd()
    {
        _tailer.Offset = File.Exists(_path) ? new FileInfo(_path).Length : 0;
    }

    public IReadOnlyList<RawLogLine> ReadNew() => _tailer.ReadNew();
}
