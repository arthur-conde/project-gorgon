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
    private readonly ISessionAnchor? _sessionAnchor;
    private readonly object _gate = new();
    private readonly List<Channel<RawLogLine>> _subs = new();
    // Accumulated lines from session start to "now". Null until RunAsync has
    // performed its first read; nulled again on StopRunning so a rebound
    // PlayerLogPath starts from a fresh session. Grows for the life of the
    // session, bounded in practice by one game session's log volume.
    private List<RawLogLine>? _sessionReplay;
    private CancellationTokenSource? _runCts;
    // Chain of consecutive RunAsync invocations. See #547 — a fast
    // unsub→sub previously spawned a second RunAsync while the first was
    // still mid-poll, briefly putting two file readers into the publish
    // path. ContinueWith guarantees the next run only starts after the
    // prior unwinds. The chain replaces the nullable _runTask field.
    private Task _runChain = Task.CompletedTask;
    private string? _activePath;

    public PlayerLogStream(
        GameConfig config,
        IDiagnosticsSink? diag = null,
        TimeProvider? time = null,
        ISessionAnchor? sessionAnchor = null)
    {
        _config = config;
        _diag = diag;
        _time = time ?? TimeProvider.System;
        _sessionAnchor = sessionAnchor;
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

        RawLogLine[] replay;
        lock (_gate)
        {
            // Snapshot the replay buffer and register for live lines under the
            // same lock, so any concurrent Publish is either already represented
            // in the snapshot or gets written to the channel after we release.
            replay = _sessionReplay is { Count: > 0 } r ? r.ToArray() : [];
            _subs.Add(channel);
            EnsureRunning();
        }

        try
        {
            // Replay session history directly via yield — bypassing the bounded
            // channel — so a late joiner can't lose history to DropOldest when
            // the replay buffer exceeds 1024 lines (common after a mid-session
            // launch). Live lines continue through the bounded channel and keep
            // their DropOldest safety net.
            foreach (var line in replay)
            {
                if (ct.IsCancellationRequested) yield break;
                yield return line;
            }

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
        // Healthy uncancelled run already in progress? Keep it.
        if (_runCts is { IsCancellationRequested: false } && !_runChain.IsCompleted) return;

        var path = _config.PlayerLogPath;
        if (string.IsNullOrEmpty(path)) return;
        _activePath = path;

        // Queue a new RunAsync at the tail of the chain via ContinueWith so
        // it only starts after the prior task has fully drained its poll
        // loop — at most one file reader is active at any moment (#547).
        var cts = new CancellationTokenSource();
        _runCts = cts;
        _runChain = _runChain.ContinueWith(
            _ => RunChainLinkAsync(path, cts),
            CancellationToken.None,
            TaskContinuationOptions.None,
            TaskScheduler.Default).Unwrap();
    }

    private async Task RunChainLinkAsync(string path, CancellationTokenSource cts)
    {
        try
        {
            // A StopRunning landing between queueing and this point makes
            // the queued link a no-op.
            bool shouldRun;
            lock (_gate) shouldRun = _subs.Count > 0 && !cts.IsCancellationRequested;
            if (!shouldRun) return;
            await RunAsync(path, cts.Token).ConfigureAwait(false);
        }
        finally
        {
            cts.Dispose();
        }
    }

    private void StopRunning()
    {
        // Signal cancellation; the chain link observes it and unwinds. We
        // DON'T null _runCts here, because EnsureRunning reads
        // IsCancellationRequested to decide whether to queue a successor.
        try { _runCts?.Cancel(); } catch { }
        _activePath = null;
        _sessionReplay = null;
    }

    private async Task RunAsync(string path, CancellationToken ct)
    {
        _diag?.Info("PlayerLog", $"Subscribing to {path}");
        var reader = new PlayerLogTailReader(path, _time, _sessionAnchor);
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
