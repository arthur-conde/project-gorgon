using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using System.Threading.Channels;
using Mithril.Shared.Diagnostics;
using Mithril.Shared.Game;

namespace Mithril.Shared.Logging;

/// <summary>
/// Centralized Player.log tail. Holds one <see cref="PlayerLogTailReader"/>
/// per active path; each subscriber gets its own bounded channel and
/// receives every line from the current session start onwards — including
/// lines emitted before their subscription began. The session replay buffer
/// makes late-joining subscribers (e.g. ingestion services that wait on a
/// module gate) observe the same history as subscribers present at boot.
/// </summary>
public sealed class PlayerLogStream : IPlayerLogStream, IDisposable
{
    private readonly GameConfig _config;
    private readonly TimeProvider _time;
    private readonly IDiagnosticsSink? _diag;
    private readonly object _gate = new();
    private readonly List<Channel<RawLogLine>> _subs = new();
    // Accumulated lines from session start to "now". Null until RunAsync has
    // performed its first read; nulled again on StopRunning so a rebound
    // PlayerLogPath starts from a fresh session. Grows for the life of the
    // session, bounded in practice by one game session's log volume.
    private List<RawLogLine>? _sessionReplay;
    private CancellationTokenSource? _runCts;
    private Task? _runTask;
    private string? _activePath;

    public PlayerLogStream(GameConfig config, IDiagnosticsSink? diag = null, TimeProvider? time = null)
    {
        _config = config;
        _diag = diag;
        _time = time ?? TimeProvider.System;
        _config.PropertyChanged += OnConfigChanged;
    }

    public async IAsyncEnumerable<RawLogLine> SubscribeAsync([EnumeratorCancellation] CancellationToken ct)
    {
        var channel = Channel.CreateBounded<RawLogLine>(new BoundedChannelOptions(1024)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = true,
            SingleWriter = true,
        });

        lock (_gate)
        {
            // Replay session-start-to-now under the lock BEFORE the channel joins
            // _subs, so any concurrent Publish appends after the replayed lines
            // rather than interleaving ahead of them.
            if (_sessionReplay is { Count: > 0 } replay)
                foreach (var line in replay) channel.Writer.TryWrite(line);
            _subs.Add(channel);
            EnsureRunning();
        }

        try
        {
            await foreach (var line in channel.Reader.ReadAllAsync(ct).ConfigureAwait(false))
            {
                yield return line;
            }
        }
        finally
        {
            lock (_gate)
            {
                _subs.Remove(channel);
                channel.Writer.TryComplete();
                if (_subs.Count == 0) StopRunning();
            }
        }
    }

    private void OnConfigChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(GameConfig.PlayerLogPath)) return;
        lock (_gate)
        {
            if (_subs.Count == 0) return;
            StopRunning();
            EnsureRunning();
        }
    }

    private void EnsureRunning()
    {
        if (_runTask is { IsCompleted: false }) return;
        var path = _config.PlayerLogPath;
        if (string.IsNullOrEmpty(path)) return;
        _activePath = path;
        _runCts = new CancellationTokenSource();
        _runTask = Task.Run(() => RunAsync(path, _runCts.Token));
    }

    private void StopRunning()
    {
        try { _runCts?.Cancel(); } catch { }
        _runCts?.Dispose();
        _runCts = null;
        _runTask = null;
        _activePath = null;
        _sessionReplay = null;
    }

    private async Task RunAsync(string path, CancellationToken ct)
    {
        _diag?.Info("PlayerLog", $"Subscribing to {path}");
        var reader = new PlayerLogTailReader(path, _time);
        reader.SeedToSessionStart();

        // Initial flush (catch up from session start)
        Publish(reader.ReadNew(), seedReplay: true);

        FileSystemWatcher? watcher = null;
        try
        {
            var dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir) && Directory.Exists(dir))
            {
                watcher = new FileSystemWatcher(dir, Path.GetFileName(path))
                {
                    NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.Size,
                    EnableRaisingEvents = true,
                };
            }

            var pollInterval = TimeSpan.FromSeconds(Math.Max(0.25, _config.PollIntervalSeconds));
            while (!ct.IsCancellationRequested)
            {
                try { await Task.Delay(pollInterval, ct).ConfigureAwait(false); }
                catch (OperationCanceledException) { break; }
                try { Publish(reader.ReadNew(), seedReplay: false); }
                catch (IOException) { /* file rotated mid-read; retry next tick */ }
            }
        }
        finally
        {
            watcher?.Dispose();
        }
    }

    private void Publish(IReadOnlyList<RawLogLine> lines, bool seedReplay)
    {
        if (lines.Count == 0 && !seedReplay) return;
        Channel<RawLogLine>[] snapshot;
        lock (_gate)
        {
            // Initialize (on seed) or append to the replay buffer inside the same
            // lock that SubscribeAsync uses to read it, so a subscriber joining
            // between Publish calls either sees the fully-appended buffer or not
            // this batch — never a torn view.
            if (seedReplay) _sessionReplay ??= new List<RawLogLine>(lines.Count);
            if (_sessionReplay is not null && lines.Count > 0) _sessionReplay.AddRange(lines);
            snapshot = _subs.ToArray();
        }
        foreach (var line in lines)
        {
            _diag?.Trace("PlayerLog", line.Line);
            foreach (var ch in snapshot) ch.Writer.TryWrite(line);
        }
    }

    public void Dispose()
    {
        _config.PropertyChanged -= OnConfigChanged;
        lock (_gate) { StopRunning(); }
    }
}
