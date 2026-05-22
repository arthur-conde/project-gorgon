using System.IO;
using System.Runtime.CompilerServices;
using Mithril.Shared.Diagnostics;
using Mithril.Shared.Game;
using Mithril.Shared.Logging;

namespace Mithril.WorldSim.Chat.Producers;

/// <summary>
/// Concrete <see cref="IChatLogReplaySource"/> over <see cref="GameConfig.ChatLogDirectory"/>.
/// On attach: enumerates every existing file in the directory, raw-scans the
/// pooled content for the chat-side login banner
/// (<see cref="ChatLoginBannerParser"/>) to identify the globally most-recent
/// banner, then drains each file through a <see cref="LogSourceTailer"/> from
/// byte 0 with all per-file <see cref="ChatLogClock"/> instances pre-seeded
/// with the canonical session offset (per #639). The cutoff timestamp folded
/// via the session offset (principle 9 — "seek to the most recent chat banner
/// matching the current Player.log session") gates the replay window; lines
/// at-or-after the cutoff are yielded in timestamp-merged order with
/// <see cref="LogEnvelope{T}.IsReplay"/> set to <c>true</c>. After the replay
/// drain the source transitions to live-tail and yields subsequent appends with
/// <c>IsReplay = false</c>.
///
/// <para>"Matching the current Player.log session" is delegated to the
/// cross-source agreement check at the view layer (principle 7 — both streams
/// self-scope independently). This source's job is the chat-intrinsic "most
/// recent banner" decision.</para>
///
/// <para><b>Session-shared clock offset (#639).</b> All per-file tailers in
/// a session share the same canonical offset, so a chat file that doesn't
/// itself contain a banner — or whose leading lines precede its own banner —
/// still folds its local-date prefixes via the session-canonical offset
/// rather than the host TZ fallback. Without this, a cross-machine replay
/// (chat written on UTC-7, replayed on a UTC+1 host) would mis-stamp every
/// no-banner file's lines by 8 hours. Each per-file clock keeps
/// <c>_lastEmitted</c> tailer-local (so an unprefixed leading line in file B
/// doesn't inherit file A's stamp), and the immutability of the seeded
/// offset within a session matches the issue's "set once + reconcile on
/// mismatch" intent for the seed path (a subsequent banner mid-session would
/// re-anchor via the existing PG-re-login semantics, which is unchanged).</para>
///
/// <para><b>Phase 0 scope limits.</b> Files that exist at attach are tailed
/// for the rest of the session; new files created post-attach (channels added
/// mid-session — rare for PG) are not picked up. FileSystemWatcher-driven new-
/// file pickup is a follow-on enhancement once a real world-sim consumer
/// (#602 / #603) needs it. Mid-session banner-mismatch warn-and-discard (the
/// "step 4" in #639's fix sketch) is also deferred — current behaviour
/// re-anchors on second banner, matching the chat-clock's pre-#639 semantics.</para>
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

        // 1. Pre-scan banners. Per #639: all per-file clocks in the session
        //    must share the same canonical session offset, so we identify
        //    the globally-most-recent banner before stamping any line. We
        //    read raw text here (one cheap pass per file) and look only at
        //    the banner-line offset + local-date prefix; the actual stamped
        //    lines are produced in pass 2 with the shared seeded offset, so
        //    every chat-derived RawLogLine.Timestamp in the session folds
        //    via the same offset — even unprefixed leading lines and lines
        //    in files that don't themselves carry the banner.
        var fileRawLines = new List<(string Path, string[] RawLines)>();
        foreach (var path in Directory.EnumerateFiles(directory))
        {
            try
            {
                // FileShare.ReadWrite | Delete matches LogSourceTailer so a
                // concurrently-appending PG client doesn't lock us out.
                using var fs = new FileStream(path, FileMode.Open, FileAccess.Read,
                    FileShare.ReadWrite | FileShare.Delete);
                using var reader = new StreamReader(fs);
                var text = reader.ReadToEnd();
                fileRawLines.Add((path, text.Split('\n')));
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

        // 2. Find globally most-recent banner by parsing local-date prefixes
        //    directly (offset is set per the banner itself, so we compare
        //    *local* timestamps — every banner is consistent with itself).
        //    Capture the canonical session offset from that banner.
        DateTime? bannerCutoffLocal = null;
        TimeSpan? sessionOffset = null;
        foreach (var (_, rawLines) in fileRawLines)
        {
            foreach (var raw in rawLines)
            {
                var line = raw.EndsWith('\r') ? raw[..^1] : raw;
                if (line.Length == 0) continue;
                if (!ChatLoginBannerParser.TryParse(line, out var banner)) continue;
                if (!ChatLogClock.TryParseLocalDatePrefix(line, out var local)) continue;
                if (bannerCutoffLocal is null || local > bannerCutoffLocal)
                {
                    bannerCutoffLocal = local;
                    sessionOffset = banner.Offset;
                }
            }
        }

        // 3. Snapshot files + drain each via LogSourceTailer, using
        //    `ChatLogClock` instances all pre-seeded with the canonical
        //    session offset (when known). Per-file clock instances keep
        //    `_lastEmitted` tailer-local — only `_bannerOffset` is shared
        //    state, and it's seeded once + immutable for the seed path
        //    (subsequent banners would re-anchor, which is the intended
        //    PG-re-login behaviour and unchanged from prior semantics).
        var tailers = new List<(string Path, LogSourceTailer Tailer, List<RawLogLine> Lines)>();
        foreach (var (path, _) in fileRawLines)
        {
            try
            {
                var clock = new ChatLogClock(_time, _fallbackTz, sessionOffset);
                var tailer = new LogSourceTailer(path, clock, _time);
                var lines = new List<RawLogLine>();
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

        // 4. Compute the cross-source replay cutoff in UTC terms. The
        //    cutoff banner's local timestamp folded by the session offset
        //    is the UTC instant downstream events are compared against.
        DateTimeOffset? bannerCutoff = null;
        if (bannerCutoffLocal is { } cutoffLocal && sessionOffset is { } off)
        {
            bannerCutoff = new DateTimeOffset(cutoffLocal, off);
        }

        // 5. Replay phase — yield lines at-or-after the cutoff, merged across
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

        // 6. Live phase — poll the same tailers. They've already advanced their
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
