using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using System.Threading.Channels;
using Gorgon.Shared.Diagnostics;
using Gorgon.Shared.Game;

namespace Gorgon.Shared.Logging;

/// <summary>
/// Centralized Player.log tail. Holds one <see cref="PlayerLogTailReader"/>
/// per active path; each subscriber gets its own bounded channel and
/// receives every line emitted after their subscription begins.
/// </summary>
public sealed class PlayerLogStream : IPlayerLogStream, IDisposable
{
    private readonly GameConfig _config;
    private readonly TimeProvider _time;
    private readonly IDiagnosticsSink? _diag;
    private readonly object _gate = new();
    private readonly List<Channel<RawLogLine>> _subs = new();
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
    }

    private async Task RunAsync(string path, CancellationToken ct)
    {
        _diag?.Info("PlayerLog", $"Subscribing to {path}");
        var reader = new PlayerLogTailReader(path, _time);
        reader.SeedToSessionStart();

        // Initial flush (catch up from session start)
        Publish(reader.ReadNew());

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
                try { Publish(reader.ReadNew()); }
                catch (IOException) { /* file rotated mid-read; retry next tick */ }
            }
        }
        finally
        {
            watcher?.Dispose();
        }
    }

    private void Publish(IReadOnlyList<RawLogLine> lines)
    {
        if (lines.Count == 0) return;
        Channel<RawLogLine>[] snapshot;
        lock (_gate) { snapshot = _subs.ToArray(); }
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
