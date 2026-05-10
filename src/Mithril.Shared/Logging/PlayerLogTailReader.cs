using System.IO;
using System.Text;

namespace Mithril.Shared.Logging;

/// <summary>
/// Single-file tail with session-start seek. Mirrors ws_bridge.py's
/// _find_session_start: scans the last 10 MB for the most recent
/// ProcessAddPlayer( occurrence so the consumer always receives the
/// login event regardless of how long ago the session began.
///
/// Each emitted <see cref="RawLogLine"/> carries the absolute UTC of the
/// log line itself (recovered from the <c>[HH:MM:SS]</c> prefix every PG
/// gameplay line carries) rather than wall-clock-at-read. Past-anchored
/// cooldown sources (Gandalf Quest/Loot) rely on this to correctly anchor
/// rows when Mithril launches mid-session and the seed replay surfaces
/// hours-old completions.
/// </summary>
public sealed class PlayerLogTailReader
{
    private const int SessionScanChunkBytes = 10 * 1024 * 1024;
    private const string SessionMarker = "ProcessAddPlayer(";

    private readonly string _path;
    private readonly TimeProvider _time;
    private long _offset;
    private byte[] _residual = Array.Empty<byte>();

    // Date-folding state, threaded across ReadNew batches. PG's [HH:MM:SS]
    // prefix lacks a date, so the first content batch anchors on file mtime
    // (counting any midnight rollovers in the batch and walking the start
    // back), and subsequent batches keep walking forward — incrementing
    // _currentLocalDate whenever the time-of-day jumps backward by >12h
    // (the threshold distinguishes a real midnight rollover from a DST
    // fall-back's <=1h overlap).
    private DateOnly? _currentLocalDate;
    private TimeSpan? _prevLocalTimeOfDay;
    private DateTime _lastEmittedUtc;

    public PlayerLogTailReader(string path, TimeProvider? time = null)
    {
        _path = path ?? throw new ArgumentNullException(nameof(path));
        _time = time ?? TimeProvider.System;
    }

    public void SeedToSessionStart()
    {
        if (!File.Exists(_path)) { _offset = 0; return; }
        var size = new FileInfo(_path).Length;
        using var fs = new FileStream(_path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);

        // Scan backward in chunks so we find the most recent ProcessAddPlayer even
        // in a 100MB+ log. Each chunk overlaps the previous by the marker length
        // so a login at a chunk boundary isn't missed.
        var overlap = SessionMarker.Length;
        var end = size;
        while (end > 0)
        {
            var chunkSize = (int)Math.Min(SessionScanChunkBytes, end);
            var scanFrom = end - chunkSize;
            fs.Seek(scanFrom, SeekOrigin.Begin);
            var buf = new byte[chunkSize];
            var read = fs.Read(buf, 0, buf.Length);
            var text = Encoding.UTF8.GetString(buf, 0, read);
            var idx = text.LastIndexOf(SessionMarker, StringComparison.Ordinal);
            if (idx >= 0)
            {
                var lineStart = text.LastIndexOf('\n', idx);
                var startInChunk = lineStart < 0 ? 0 : lineStart + 1;
                _offset = scanFrom + Encoding.UTF8.GetByteCount(text.AsSpan(0, startInChunk));
                return;
            }
            if (scanFrom == 0) break;
            end = scanFrom + overlap; // keep overlap so a marker spanning chunks is caught
        }
        _offset = 0; // no login found anywhere — replay from the top
    }

    public void SeedToEnd()
    {
        _offset = File.Exists(_path) ? new FileInfo(_path).Length : 0;
    }

    public IReadOnlyList<RawLogLine> ReadNew()
    {
        if (!File.Exists(_path)) return Array.Empty<RawLogLine>();
        using var fs = new FileStream(_path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        if (fs.Length < _offset) _offset = 0; // truncation / rotation
        if (fs.Length == _offset) return Array.Empty<RawLogLine>();

        fs.Seek(_offset, SeekOrigin.Begin);
        var len = (int)Math.Min(int.MaxValue, fs.Length - _offset);
        var buf = new byte[_residual.Length + len];
        Buffer.BlockCopy(_residual, 0, buf, 0, _residual.Length);
        var read = fs.Read(buf, _residual.Length, len);
        var total = _residual.Length + read;

        // Find last newline; everything after becomes residual
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

        var lines = text.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        if (lines.Length == 0) return Array.Empty<RawLogLine>();

        // First batch with at least one [HH:MM:SS] prefix anchors the date.
        // Subsequent batches keep _currentLocalDate as-is and continue folding.
        if (_currentLocalDate is null) InitializeDateAnchor(lines);

        var result = new List<RawLogLine>(lines.Length);
        foreach (var line in lines)
        {
            var trimmed = line.EndsWith('\r') ? line[..^1] : line;
            if (trimmed.Length == 0) continue;
            result.Add(new RawLogLine(StampForLine(trimmed), trimmed));
        }
        return result;
    }

    /// <summary>
    /// Pre-scan the first batch: count midnight rollovers and capture the
    /// last <c>[HH:MM:SS]</c> tod, then anchor on file mtime so the LAST
    /// timestamped line lines up with mtime (or mtime - 1 day if its tod
    /// is past mtime's tod, which happens when the file rolls over to the
    /// next calendar day shortly after the line was written).
    /// </summary>
    private void InitializeDateAnchor(string[] lines)
    {
        var rollovers = 0;
        TimeSpan? prev = null;
        TimeSpan? lastSeen = null;
        foreach (var rawLine in lines)
        {
            var trimmed = rawLine.EndsWith('\r') ? rawLine[..^1] : rawLine;
            if (!TryParseTimestampPrefix(trimmed, out var tod)) continue;
            if (prev.HasValue && tod < prev.Value && (prev.Value - tod) > TimeSpan.FromHours(12))
                rollovers++;
            prev = tod;
            lastSeen = tod;
        }

        // No timestamped lines anywhere in the batch: leave _currentLocalDate
        // null so each line falls through to the wall-clock fallback. The
        // next batch with a prefixed line will re-trigger init.
        if (lastSeen is null) return;

        DateTime mtime;
        try { mtime = File.GetLastWriteTime(_path); }
        catch { mtime = _time.GetUtcNow().LocalDateTime; }
        var anchorDate = DateOnly.FromDateTime(mtime);
        if (lastSeen.Value > mtime.TimeOfDay) anchorDate = anchorDate.AddDays(-1);

        _currentLocalDate = anchorDate.AddDays(-rollovers);
    }

    private DateTime StampForLine(string line)
    {
        if (TryParseTimestampPrefix(line, out var tod) && _currentLocalDate.HasValue)
        {
            // Midnight rollover: HH wrapped backward by >12h. Smaller backward
            // jumps (the 1-hour DST fall-back overlap, out-of-order writes
            // within the same minute) keep the same calendar date.
            if (_prevLocalTimeOfDay.HasValue
                && tod < _prevLocalTimeOfDay.Value
                && (_prevLocalTimeOfDay.Value - tod) > TimeSpan.FromHours(12))
            {
                _currentLocalDate = _currentLocalDate.Value.AddDays(1);
            }
            _prevLocalTimeOfDay = tod;

            var date = _currentLocalDate.Value;
            var local = new DateTime(date.Year, date.Month, date.Day,
                tod.Hours, tod.Minutes, tod.Seconds, DateTimeKind.Local);
            _lastEmittedUtc = local.ToUniversalTime();
        }
        else if (_lastEmittedUtc == default)
        {
            // No prefix has been seen yet at all in this stream — fall through
            // to wall-clock-now. Engine init banners and stack traces are the
            // typical non-prefixed lines and no parser cares about them.
            _lastEmittedUtc = _time.GetUtcNow().UtcDateTime;
        }
        // else: inherit prior _lastEmittedUtc (engine noise interleaved with
        // gameplay lines stays anchored on the most recent gameplay tod).

        return _lastEmittedUtc;
    }

    /// <summary>
    /// Parse the <c>[HH:MM:SS] </c> prefix every PG gameplay line carries.
    /// Hand-rolled (vs regex) because this runs once per line on the seed
    /// replay path and the format is fixed-width.
    /// </summary>
    private static bool TryParseTimestampPrefix(string line, out TimeSpan tod)
    {
        tod = default;
        if (line.Length < 11) return false;
        if (line[0] != '[' || line[3] != ':' || line[6] != ':' || line[9] != ']' || line[10] != ' ') return false;
        if (!IsAsciiDigit(line[1]) || !IsAsciiDigit(line[2])) return false;
        if (!IsAsciiDigit(line[4]) || !IsAsciiDigit(line[5])) return false;
        if (!IsAsciiDigit(line[7]) || !IsAsciiDigit(line[8])) return false;
        var h = (line[1] - '0') * 10 + (line[2] - '0');
        var m = (line[4] - '0') * 10 + (line[5] - '0');
        var s = (line[7] - '0') * 10 + (line[8] - '0');
        if (h >= 24 || m >= 60 || s >= 60) return false;
        tod = new TimeSpan(h, m, s);
        return true;
    }

    private static bool IsAsciiDigit(char c) => (uint)(c - '0') <= 9;
}
