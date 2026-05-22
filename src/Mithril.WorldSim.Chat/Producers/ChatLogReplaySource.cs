using System.IO;
using System.Runtime.CompilerServices;
using Mithril.Shared.Diagnostics;
using Mithril.Shared.Game;
using Mithril.Shared.Logging;

namespace Mithril.WorldSim.Chat.Producers;

/// <summary>
/// Concrete <see cref="IChatLogReplaySource"/> over <see cref="GameConfig.ChatLogDirectory"/>.
/// On attach: enumerates every existing file in the directory, reads each via
/// <see cref="LogSourceTailer"/> from byte 0 (so all existing lines are pulled
/// with TZ-correct timestamps via <see cref="ChatLogClock"/>), then scans the
/// pooled content for the chat-side login banner
/// (<see cref="ChatLoginBannerParser"/>). The globally most-recent banner's
/// timestamp becomes the replay cutoff (principle 9 — "seek to the most recent
/// chat banner matching the current Player.log session"); lines at-or-after
/// that timestamp are yielded in timestamp-merged order with
/// <see cref="LogEnvelope{T}.IsReplay"/> set to <c>true</c>. After the replay
/// drain the source transitions to live-tail and yields subsequent appends with
/// <c>IsReplay = false</c>.
///
/// <para>"Matching the current Player.log session" is delegated to the
/// cross-source agreement check at the view layer (principle 7 — both streams
/// self-scope independently). This source's job is the chat-intrinsic "most
/// recent banner" decision.</para>
///
/// <para><b>Phase 0 scope limits.</b> Files that exist at attach are tailed
/// for the rest of the session; new files created post-attach (channels added
/// mid-session — rare for PG) are not picked up. FileSystemWatcher-driven new-
/// file pickup is a follow-on enhancement once a real world-sim consumer
/// (#602 / #603) needs it.</para>
/// </summary>
public sealed class ChatLogReplaySource : IChatLogReplaySource
{
    private readonly GameConfig _config;
    private readonly TimeProvider _time;
    private readonly IDiagnosticsSink? _diag;
    private readonly TimeZoneInfo? _fallbackTz;

    public ChatLogReplaySource(
        GameConfig config,
        IDiagnosticsSink? diag = null,
        TimeProvider? time = null,
        TimeZoneInfo? fallbackTz = null)
    {
        _config = config ?? throw new ArgumentNullException(nameof(config));
        _time = time ?? TimeProvider.System;
        _diag = diag;
        _fallbackTz = fallbackTz;
    }

    public async IAsyncEnumerable<LogEnvelope<RawLogLine>> SubscribeWithReplayMarkerAsync(
        [EnumeratorCancellation] CancellationToken ct)
    {
        var directory = _config.ChatLogDirectory;
        if (string.IsNullOrEmpty(directory) || !Directory.Exists(directory))
        {
            _diag?.Info("ChatLogReplaySource", $"Chat directory unavailable: '{directory}'. No frames will be emitted.");
            yield break;
        }

        // 1. Snapshot files + drain each via LogSourceTailer.
        var tailers = new List<(string Path, LogSourceTailer Tailer, List<RawLogLine> Lines)>();
        foreach (var path in Directory.EnumerateFiles(directory))
        {
            try
            {
                var tailer = new LogSourceTailer(path, new ChatLogClock(_time, _fallbackTz), _time);
                var lines = new List<RawLogLine>();
                // ReadNew may return in batches (the file is small enough that
                // a single call gets it all, but the contract is no-guarantee;
                // loop until empty to be safe).
                while (true)
                {
                    var batch = tailer.ReadNew();
                    if (batch.Count == 0) break;
                    lines.AddRange(batch);
                }
                tailers.Add((path, tailer, lines));
            }
            catch (IOException ex)
            {
                _diag?.Warn("ChatLogReplaySource", $"Skipping unreadable file '{path}': {ex.Message}");
            }
            catch (UnauthorizedAccessException ex)
            {
                _diag?.Warn("ChatLogReplaySource", $"Skipping inaccessible file '{path}': {ex.Message}");
            }
        }

        // 2. Find globally most-recent banner.
        DateTimeOffset? bannerCutoff = null;
        foreach (var (_, _, lines) in tailers)
        {
            foreach (var l in lines)
            {
                if (ChatLoginBannerParser.TryParse(l.Line, out _))
                {
                    if (bannerCutoff is null || l.Timestamp > bannerCutoff)
                    {
                        bannerCutoff = l.Timestamp;
                    }
                }
            }
        }

        // 3. Replay phase — yield lines at-or-after the cutoff, merged across
        //    files by timestamp. If no banner, replay is empty (matches the
        //    legacy "skip what's already there" seed behaviour for pre-banner
        //    cold attaches).
        if (bannerCutoff is not null)
        {
            var replay = new List<RawLogLine>();
            foreach (var (_, _, lines) in tailers)
            {
                foreach (var l in lines)
                {
                    if (l.Timestamp >= bannerCutoff) replay.Add(l);
                }
            }
            replay.Sort((a, b) => a.Timestamp.CompareTo(b.Timestamp));

            _diag?.Info("ChatLogReplaySource",
                $"Replay: banner @ {bannerCutoff:O}, {replay.Count} line(s) across {tailers.Count} file(s).");

            foreach (var line in replay)
            {
                ct.ThrowIfCancellationRequested();
                yield return new LogEnvelope<RawLogLine>(line, IsReplay: true);
            }
        }
        else
        {
            _diag?.Info("ChatLogReplaySource",
                $"No banner across {tailers.Count} file(s); skipping replay phase.");
        }

        // 4. Live phase — poll the same tailers. They've already advanced their
        //    offsets past the replay batch, so ReadNew() now returns appended
        //    content only.
        var pollInterval = TimeSpan.FromSeconds(Math.Max(0.25, _config.PollIntervalSeconds));
        while (!ct.IsCancellationRequested)
        {
            try { await Task.Delay(pollInterval, _time, ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { yield break; }

            foreach (var (_, tailer, _) in tailers)
            {
                IReadOnlyList<RawLogLine> appended;
                try
                {
                    appended = tailer.ReadNew();
                }
                catch (IOException)
                {
                    continue; // rotated mid-read; retry next tick
                }
                catch (UnauthorizedAccessException)
                {
                    continue;
                }

                foreach (var line in appended)
                {
                    yield return new LogEnvelope<RawLogLine>(line, IsReplay: false);
                }
            }
        }
    }
}
