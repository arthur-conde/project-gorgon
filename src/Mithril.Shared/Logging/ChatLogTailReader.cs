using System.IO;

namespace Mithril.Shared.Logging;

/// <summary>
/// Chat-directory wrapper around <see cref="LogSourceTailer"/>. Per #513
/// the tail mechanics (offset, residual, rotation, Sequence,
/// ReadMonotonicTicks, timestamp normalization) all live in
/// <see cref="LogSourceTailer"/>; this class owns only the chat-side
/// concerns the unified tailer doesn't: directory enumeration, one
/// tailer-per-channel-file (chat logs are split per channel and rotate
/// independently), and the "skip whatever was already in the directory
/// at startup" seed strategy.
///
/// <para>The chat clock injected into each per-file tailer is
/// <see cref="ChatLogClock"/>, which parses the <c>yy-MM-dd HH:mm:ss\t</c>
/// LOCAL prefix and folds it into a TZ-correct
/// <see cref="DateTimeOffset"/>. This is the bug-class kill from #513:
/// downstream of L0 a chat-derived <see cref="RawLogLine.Timestamp"/> is
/// already TZ-correct, so the per-consumer
/// <c>new DateTimeOffset(ts, TimeSpan.Zero)</c> wrap can no longer apply
/// the wrong offset (it's now a no-op on a pre-typed value).</para>
/// </summary>
public sealed class ChatLogTailReader
{
    private readonly TimeProvider _time;
    private readonly ISessionAnchor? _sessionAnchor;
    private readonly Dictionary<string, LogSourceTailer> _files = new(StringComparer.OrdinalIgnoreCase);

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
                var tailer = GetOrCreate(path);
                tailer.Offset = new FileInfo(path).Length;
            }
            catch (IOException) { }
        }
    }

    public IReadOnlyList<RawLogLine> ReadNew(string path)
    {
        if (!File.Exists(path)) return Array.Empty<RawLogLine>();
        return GetOrCreate(path).ReadNew();
    }

    private LogSourceTailer GetOrCreate(string path)
    {
        if (_files.TryGetValue(path, out var existing)) return existing;
        // sessionAnchor is plumbed through even though ChatLogClock doesn't
        // use it today — keeps the chat tail-reader's ctor symmetric with
        // the Player one and leaves the door open for a future chat-side
        // clock that does want a session-pinned anchor.
        _ = _sessionAnchor;
        var t = new LogSourceTailer(path, new ChatLogClock(_time), _time);
        _files[path] = t;
        return t;
    }
}
