using System.Runtime.CompilerServices;
using System.Text;
using System.Threading.Channels;
using Mithril.GameState.Areas.Parsing;
using Mithril.Shared.Diagnostics;
using Mithril.Shared.Game;
using Mithril.Shared.Logging;
using Mithril.WorldSim;

namespace Mithril.GameState.Areas.Producers;

/// <summary>
/// World-simulator producer that adapts the L1 driver's
/// <see cref="ILogStreamDriver"/> push-based <c>SystemSignal</c> subscription
/// into the world's pull-based <see cref="IFrameProducer{TPayload}"/> contract
/// for the Player.log area folder (#775). Filters
/// <see cref="SystemSignalKind.AreaLoading"/> envelopes, parses each via
/// <see cref="AreaTransitionParser"/>, and emits one
/// <see cref="AreaLoadingFrame"/> per transition; non-area system-signal kinds
/// (LoginBanner / PlayerAdded / SessionLifecycle) are dropped at this
/// boundary so the world sees only area frames.
///
/// <para><b>Eager pre-warm seed (separate from L1 stream).</b> Mithril's L1
/// driver rewinds the session-replay window to the most recent
/// <c>ProcessAddPlayer(</c> line, which lands ~9 s <em>after</em> the
/// <c>LOADING LEVEL</c> line for the current area — so a pure-L1
/// subscription would never see the current area's transition. The producer
/// exposes <see cref="TryBuildSeedFrame"/> as a pure helper: a one-shot
/// reverse-scan of <see cref="GameConfig.PlayerLogPath"/> for the most recent
/// <c>LOADING LEVEL</c> line, parsing its embedded <c>[HH:MM:SS]</c> prefix
/// (combined with the file's mtime to recover the UTC date — same fallback
/// shape as <c>PlayerLogClock.EnsureAnchored</c>'s mtime path). The
/// <c>PlayerAreaWorldRegistration</c> hosted service drives this synchronously
/// at its own <c>StartAsync</c> and applies the result directly to the folder,
/// so the back-compat <see cref="IPlayerAreaState.CurrentArea"/> read returns
/// the correct area BEFORE any consumer's <c>StartAsync</c> runs later in
/// the hosted-service registration order (Legolas's
/// <c>PlayerLogIngestionService.ApplyAreaIfChanged</c> being the relevant
/// caller).</para>
///
/// <para>The seed is NOT yielded through <see cref="SubscribeAsync"/> — it
/// would no-op at the folder anyway (Apply on the already-current area
/// returns an empty change list) and would arrive only after the merger
/// drains, defeating the eager-attach point. World-bus consumers reading
/// the seed snapshot use the same pattern as the skill folder:
/// <see cref="IPlayerAreaState.CurrentArea"/> for initial state,
/// <c>IPlayerWorld.Bus.Subscribe&lt;PlayerAreaChanged&gt;</c> for
/// subsequent transitions.</para>
///
/// <para><b>Mode awareness.</b> Mirrors
/// <see cref="Mithril.GameState.Skills.Producers.SkillFrameProducer"/>'s shape
/// — <see cref="ReachedLive"/> completes the moment the producer reads the
/// first non-replay envelope from the L1 driver. PG only emits a transition
/// per actual portal (~ once per minute at most); a player idle in one area
/// could go indefinitely without a live area-frame and we mustn't stall the
/// world's mode flip waiting for one. The seed frame itself does NOT flip the
/// mode (it is emitted before the L1 subscription opens).</para>
///
/// <para><b>L1 subscription disposition.</b> Archetype-A defaults
/// (<see cref="ReplayMode.FromSessionStart"/> +
/// <see cref="DeliveryContext.Inline"/>), matching the pre-migration
/// <c>PlayerAreaTracker</c> exactly — the L0.5 router strips the envelope,
/// the parser consumes <see cref="SystemSignalLogLine.Data"/> directly, and
/// L1 owns containment around each handler invocation.</para>
///
/// <para><b>Channel buffer.</b> An unbounded single-reader channel sits
/// between the push-callback and the IAsyncEnumerable yield. The L1 driver
/// already throttles by source-stream rate (Player.log area transitions are
/// human-portal-bounded — minutes apart at most), so unbounded is acceptable
/// and avoids the deadlock risk a bounded buffer would introduce.</para>
/// </summary>
public sealed class AreaLoadingFrameProducer
    : IFrameProducer<AreaLoadingFrame>, IModeAwareFrameProducer<AreaLoadingFrame>
{
    private readonly ILogStreamDriver _driver;
    private readonly AreaTransitionParser _parser;
    private readonly GameConfig? _config;
    private readonly IDiagnosticsSink? _diag;
    private readonly TaskCompletionSource _reachedLive = new(TaskCreationOptions.RunContinuationsAsynchronously);

    private const string DiagCategory = "GameState.Area";
    private const string Marker = "LOADING LEVEL";

    // Mirrored from the pre-migration PlayerAreaTracker.ScanChunkBytes
    // (originally mirrored from PlayerLogTailReader.SessionScanChunkBytes).
    // 10 MB chunk, then walk backward in chunks until either the marker is
    // found or the start of the file is reached — bounded by file size, not
    // by the chunk constant.
    private const int ScanChunkBytes = 10 * 1024 * 1024;

    public AreaLoadingFrameProducer(
        ILogStreamDriver driver,
        AreaTransitionParser parser,
        GameConfig? config = null,
        IDiagnosticsSink? diag = null)
    {
        _driver = driver ?? throw new ArgumentNullException(nameof(driver));
        _parser = parser ?? throw new ArgumentNullException(nameof(parser));
        _config = config;
        _diag = diag;
    }

    /// <summary>
    /// Producer priority for the merger's tie-breaking. Matches the
    /// classified-pipe producers' priority (0) — same source, same ordering
    /// rights.
    /// </summary>
    public int Priority => 0;

    public Task ReachedLive => _reachedLive.Task;

    public async IAsyncEnumerable<Frame<AreaLoadingFrame>> SubscribeAsync(
        [EnumeratorCancellation] CancellationToken ct)
    {
        // SingleReader: only the world's merger reads via the async enumerator.
        // Unbounded: see the class-doc rationale (L1 already paces by source).
        var channel = Channel.CreateUnbounded<Frame<AreaLoadingFrame>>(
            new UnboundedChannelOptions { SingleReader = true });

        _diag?.Info(DiagCategory,
            "AreaLoadingFrameProducer subscribing to L1 driver (SystemSignal pipe) for AreaLoading frames");

        var subscription = _driver.Subscribe<SystemSignalLogLine>(
            envelope =>
            {
                // Mode flip is driven by envelope-shape, not area-shape (see
                // class doc). The L1 contract guarantees IsReplay transitions
                // once and never re-arms, so TrySetResult is idempotent past
                // that boundary. Idle players in one area can go arbitrarily
                // long without an AreaLoading envelope; the flip must not
                // depend on one ever arriving.
                if (!envelope.IsReplay)
                {
                    _reachedLive.TrySetResult();
                }

                var line = envelope.Payload;
                if (line.Kind != SystemSignalKind.AreaLoading)
                {
                    return ValueTask.CompletedTask;
                }

                if (_parser.TryParse(line.Data, line.Timestamp.UtcDateTime)
                    is not AreaTransitionEvent evt)
                {
                    return ValueTask.CompletedTask;
                }

                // TryWrite on an unbounded channel can only fail post-Complete,
                // and we never complete from inside the callback. Discarding
                // the result keeps the hot path branch-free.
                _ = channel.Writer.TryWrite(new Frame<AreaLoadingFrame>(
                    line.Timestamp, new AreaLoadingFrame(evt.AreaKey)));
                return ValueTask.CompletedTask;
            },
            new LogSubscriptionOptions
            {
                ReplayMode = ReplayMode.FromSessionStart,
                DeliveryContext = DeliveryContext.Inline,
                DiagnosticCategory = DiagCategory,
            });

        try
        {
            await foreach (var frame in channel.Reader.ReadAllAsync(ct).ConfigureAwait(false))
            {
                yield return frame;
            }
        }
        finally
        {
            subscription.Dispose();
            // Guarantee ReachedLive completes even if the stream ended without
            // ever flipping (degenerate test fixture, or a replay-only file
            // tail). Matches SkillFrameProducer's terminal fallback.
            _reachedLive.TrySetResult();
        }
    }

    /// <summary>
    /// Reverse-scan the configured Player.log for the most recent
    /// <c>LOADING LEVEL</c> line, parse its embedded timestamp + area, and
    /// return a seed frame. Returns <c>null</c> when there's no config, no
    /// file, or no marker — those all reduce to "area unknown at startup,"
    /// which is a valid and self-healing state. Pure function: safe to call
    /// from any thread; performs file I/O but mutates no producer state.
    ///
    /// <para>Called synchronously by
    /// <c>PlayerAreaWorldRegistration.StartAsync</c>; result is applied
    /// directly to the folder so back-compat synchronous-read consumers
    /// (Gandalf's chest-area stamp, Legolas's
    /// <c>ApplyAreaIfChanged</c>, Palantir's debug refresh) see the correct
    /// area at THEIR own <c>StartAsync</c> later in registration order.</para>
    /// </summary>
    public Frame<AreaLoadingFrame>? TryBuildSeedFrame()
    {
        var path = _config?.PlayerLogPath;
        if (string.IsNullOrEmpty(path) || !File.Exists(path)) return null;

        var info = new FileInfo(path);
        if (info.Length == 0) return null;

        string? seedLine;
        try
        {
            seedLine = ScanForMostRecentMarker(path, info.Length);
        }
        catch (IOException ex)
        {
            _diag?.Warn(DiagCategory, $"Pre-drain seed scan failed: {ex.Message}");
            return null;
        }

        if (seedLine is null)
        {
            _diag?.Info(DiagCategory,
                "Pre-drain seed: no LOADING LEVEL marker in scanned region — folder starts area-unknown");
            return null;
        }

        var stamp = ResolveSeedTimestamp(seedLine, info.LastWriteTimeUtc);
        if (_parser.TryParse(seedLine, stamp.UtcDateTime)
            is not AreaTransitionEvent evt)
        {
            // Marker matched but the parser rejected the line shape. Surface
            // it so a future grammar drift is investigable; no seed frame.
            _diag?.Warn(DiagCategory,
                $"Pre-drain seed: marker found but parser rejected line: {seedLine}");
            return null;
        }

        return new Frame<AreaLoadingFrame>(stamp, new AreaLoadingFrame(evt.AreaKey));
    }

    /// <summary>
    /// Walk the file backward in chunks until the most recent
    /// <see cref="Marker"/> is found; return the surrounding full line (no
    /// trailing newline). Returns <c>null</c> when no marker is present.
    /// </summary>
    private static string? ScanForMostRecentMarker(string logPath, long size)
    {
        using var fs = new FileStream(
            logPath, FileMode.Open, FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete);

        var overlap = Marker.Length;
        var end = size;
        while (end > 0)
        {
            var chunkSize = (int)Math.Min(ScanChunkBytes, end);
            var scanFrom = end - chunkSize;
            fs.Seek(scanFrom, SeekOrigin.Begin);
            var buf = new byte[chunkSize];
            var read = fs.Read(buf, 0, buf.Length);
            var text = Encoding.UTF8.GetString(buf, 0, read);
            var idx = text.LastIndexOf(Marker, StringComparison.Ordinal);
            if (idx >= 0)
            {
                var lineStart = text.LastIndexOf('\n', idx);
                var startInChunk = lineStart < 0 ? 0 : lineStart + 1;
                var lineEnd = text.IndexOf('\n', idx);
                if (lineEnd < 0) lineEnd = text.Length;
                return text.Substring(startInChunk, lineEnd - startInChunk).TrimEnd('\r');
            }
            if (scanFrom == 0) break;
            end = scanFrom + overlap;
        }
        return null;
    }

    /// <summary>
    /// Recover a full <see cref="DateTimeOffset"/> from a seed line's
    /// <c>[HH:MM:SS]</c> prefix anchored against the file's UTC mtime. PG's
    /// Player.log carries time-of-day only; this fold mirrors
    /// <c>PlayerLogClock.EnsureAnchored</c>'s mtime-anchor fallback: if the
    /// parsed time-of-day is later than mtime's time-of-day, the line falls
    /// on the previous UTC day; otherwise the same day. Either way the
    /// resulting timestamp is bounded by the file write window, which is the
    /// most defensible anchor available without already consuming the L1
    /// session-banner stream (the producer runs before that).
    ///
    /// <para>When the prefix is missing or malformed, fall back to the file's
    /// mtime. The seed-line shape comes from PG itself — every gameplay line
    /// carries the prefix — so the fallback is a defence-in-depth path.</para>
    /// </summary>
    private static DateTimeOffset ResolveSeedTimestamp(string seedLine, DateTime mtimeUtc)
    {
        if (!TryParseTimestampPrefix(seedLine, out var tod))
        {
            return new DateTimeOffset(
                DateTime.SpecifyKind(mtimeUtc, DateTimeKind.Utc));
        }

        var anchorDate = DateOnly.FromDateTime(mtimeUtc);
        if (tod > mtimeUtc.TimeOfDay) anchorDate = anchorDate.AddDays(-1);

        var utc = new DateTime(
            anchorDate.Year, anchorDate.Month, anchorDate.Day,
            tod.Hours, tod.Minutes, tod.Seconds, DateTimeKind.Utc);
        return new DateTimeOffset(utc, TimeSpan.Zero);
    }

    /// <summary>
    /// Parse the <c>[HH:MM:SS] </c> prefix every PG gameplay line carries.
    /// Inlined from the internal <c>PlayerLogClock.TryParseTimestampPrefix</c>
    /// helper rather than crossing an assembly boundary for a fixed-width
    /// 11-byte parse — the format is bolted into PG's logging code and
    /// hasn't drifted in years, so the small duplication is bounded.
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
