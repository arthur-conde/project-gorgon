using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using System.Threading.Channels;
using Gorgon.Shared.Diagnostics;
using Gorgon.Shared.Game;

namespace Gorgon.Shared.Logging;

/// <summary>
/// Centralized tail of <see cref="GameConfig.ChatLogDirectory"/>. Holds one
/// <see cref="ChatLogTailReader"/> while any subscriber is active; each
/// subscriber gets its own bounded channel. Starts at end-of-directory
/// (no history replay) so long-running chat logs don't flood new sessions.
/// </summary>
public sealed class ChatLogStream : IChatLogStream, IDisposable
{
    private readonly GameConfig _config;
    private readonly TimeProvider _time;
    private readonly IDiagnosticsSink? _diag;
    private readonly object _gate = new();
    private readonly List<Channel<RawLogLine>> _subs = new();
    private CancellationTokenSource? _runCts;
    private Task? _runTask;

    public ChatLogStream(GameConfig config, IDiagnosticsSink? diag = null, TimeProvider? time = null)
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
        if (e.PropertyName != nameof(GameConfig.ChatLogDirectory)) return;
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
        var dir = _config.ChatLogDirectory;
        if (string.IsNullOrEmpty(dir)) return;
        _runCts = new CancellationTokenSource();
        _runTask = Task.Run(() => RunAsync(dir, _runCts.Token));
    }

    private void StopRunning()
    {
        try { _runCts?.Cancel(); } catch { }
        _runCts?.Dispose();
        _runCts = null;
        _runTask = null;
    }

    private async Task RunAsync(string directory, CancellationToken ct)
    {
        _diag?.Info("ChatLog", $"Subscribing to {directory}");
        var reader = new ChatLogTailReader(_time);
        reader.SeedDirectoryToCurrentEnd(directory);

        FileSystemWatcher? watcher = null;
        try
        {
            if (Directory.Exists(directory))
            {
                watcher = new FileSystemWatcher(directory)
                {
                    NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.Size | NotifyFilters.FileName,
                    IncludeSubdirectories = false,
                    EnableRaisingEvents = true,
                };
            }

            var pollInterval = TimeSpan.FromSeconds(Math.Max(0.25, _config.PollIntervalSeconds));
            while (!ct.IsCancellationRequested)
            {
                try { await Task.Delay(pollInterval, ct).ConfigureAwait(false); }
                catch (OperationCanceledException) { break; }
                if (!Directory.Exists(directory)) continue;
                foreach (var path in Directory.EnumerateFiles(directory))
                {
                    try { Publish(reader.ReadNew(path)); }
                    catch (IOException) { /* file rotated mid-read; retry next tick */ }
                    catch (UnauthorizedAccessException) { }
                }
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
            _diag?.Trace("ChatLog", line.Line);
            foreach (var ch in snapshot) ch.Writer.TryWrite(line);
        }
    }

    public void Dispose()
    {
        _config.PropertyChanged -= OnConfigChanged;
        lock (_gate) { StopRunning(); }
    }
}
