using System.IO;
using System.Text;

namespace Gorgon.Shared.Logging;

/// <summary>
/// Single-file tail with session-start seek. Mirrors ws_bridge.py's
/// _find_session_start: scans the last 10 MB for the most recent
/// ProcessAddPlayer( occurrence so the consumer always receives the
/// login event regardless of how long ago the session began.
/// </summary>
public sealed class PlayerLogTailReader
{
    private const int SessionScanWindowBytes = 10 * 1024 * 1024;
    private const string SessionMarker = "ProcessAddPlayer(";

    private readonly string _path;
    private readonly TimeProvider _time;
    private long _offset;
    private byte[] _residual = Array.Empty<byte>();

    public PlayerLogTailReader(string path, TimeProvider? time = null)
    {
        _path = path ?? throw new ArgumentNullException(nameof(path));
        _time = time ?? TimeProvider.System;
    }

    public void SeedToSessionStart()
    {
        if (!File.Exists(_path)) { _offset = 0; return; }
        var size = new FileInfo(_path).Length;
        var scanFrom = Math.Max(0, size - SessionScanWindowBytes);
        using var fs = new FileStream(_path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        fs.Seek(scanFrom, SeekOrigin.Begin);
        var buf = new byte[size - scanFrom];
        var read = fs.Read(buf, 0, buf.Length);
        var text = Encoding.UTF8.GetString(buf, 0, read);
        var idx = text.LastIndexOf(SessionMarker, StringComparison.Ordinal);
        if (idx < 0) { _offset = scanFrom; return; }
        var lineStart = text.LastIndexOf('\n', idx);
        var startInChunk = lineStart < 0 ? 0 : lineStart + 1;
        _offset = scanFrom + Encoding.UTF8.GetByteCount(text.AsSpan(0, startInChunk));
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

        var ts = _time.GetUtcNow().UtcDateTime;
        var lines = text.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        var result = new List<RawLogLine>(lines.Length);
        foreach (var line in lines)
        {
            var trimmed = line.EndsWith('\r') ? line[..^1] : line;
            if (trimmed.Length > 0) result.Add(new RawLogLine(ts, trimmed));
        }
        return result;
    }
}
