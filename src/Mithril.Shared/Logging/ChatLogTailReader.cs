using System.IO;
using System.Text;

namespace Mithril.Shared.Logging;

/// <summary>
/// Directory-based tail. Keeps a byte offset per file so we can pick up
/// where we left off, and buffers partial lines as residual bytes across
/// reads in case a line is flushed mid-write.
///
/// Each file gets its own <see cref="LogLineTimestampSequencer"/> so the
/// per-channel chat logs (which can rotate independently and interleave at
/// different rates) fold dates over their own mtimes. See
/// <see cref="PlayerLogTailReader"/> for the same fix on the gameplay log.
/// </summary>
public sealed class ChatLogTailReader
{
    private readonly TimeProvider _time;
    private readonly ISessionAnchor? _sessionAnchor;
    private readonly Dictionary<string, FileState> _files = new(StringComparer.OrdinalIgnoreCase);

    public ChatLogTailReader(TimeProvider? time = null, ISessionAnchor? sessionAnchor = null)
    {
        _time = time ?? TimeProvider.System;
        _sessionAnchor = sessionAnchor;
    }

    /// <summary>
    /// Mark every existing file as already-consumed. New files created after
    /// this call are tailed from byte 0.
    /// </summary>
    public void SeedDirectoryToCurrentEnd(string directory)
    {
        if (!Directory.Exists(directory)) return;
        foreach (var path in Directory.EnumerateFiles(directory))
        {
            try
            {
                _files[path] = new FileState
                {
                    Offset = new FileInfo(path).Length,
                    Sequencer = new LogLineTimestampSequencer(_time, _sessionAnchor),
                };
            }
            catch (IOException) { }
        }
    }

    public IReadOnlyList<RawLogLine> ReadNew(string path)
    {
        if (!File.Exists(path)) return Array.Empty<RawLogLine>();

        if (!_files.TryGetValue(path, out var state))
        {
            state = new FileState { Sequencer = new LogLineTimestampSequencer(_time, _sessionAnchor) };
            _files[path] = state;
        }

        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete);
        if (fs.Length < state.Offset) state.Offset = 0;
        if (fs.Length == state.Offset) return Array.Empty<RawLogLine>();

        fs.Seek(state.Offset, SeekOrigin.Begin);
        var len = (int)Math.Min(int.MaxValue, fs.Length - state.Offset);
        var buf = new byte[state.Residual.Length + len];
        Buffer.BlockCopy(state.Residual, 0, buf, 0, state.Residual.Length);
        var read = fs.Read(buf, state.Residual.Length, len);
        var total = state.Residual.Length + read;

        var lastNl = -1;
        for (var i = total - 1; i >= 0; i--) { if (buf[i] == (byte)'\n') { lastNl = i; break; } }
        if (lastNl < 0)
        {
            state.Residual = buf[..total];
            state.Offset += read;
            return Array.Empty<RawLogLine>();
        }

        var text = Encoding.UTF8.GetString(buf, 0, lastNl + 1);
        state.Residual = buf[(lastNl + 1)..total];
        state.Offset += read;

        var lines = text.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        if (lines.Length == 0) return Array.Empty<RawLogLine>();

        state.Sequencer.EnsureAnchored(lines, () => File.GetLastWriteTimeUtc(path));

        var result = new List<RawLogLine>(lines.Length);
        foreach (var line in lines)
        {
            var trimmed = line.EndsWith('\r') ? line[..^1] : line;
            if (trimmed.Length == 0) continue;
            result.Add(new RawLogLine(state.Sequencer.StampForLine(trimmed), trimmed));
        }
        return result;
    }

    private sealed class FileState
    {
        public long Offset { get; set; }
        public byte[] Residual { get; set; } = Array.Empty<byte>();
        public required LogLineTimestampSequencer Sequencer { get; init; }
    }
}
