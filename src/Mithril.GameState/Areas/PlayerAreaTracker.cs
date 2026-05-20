using System.IO;
using System.Text;
using Mithril.GameState.Areas.Parsing;
using Mithril.Shared.Diagnostics;
using Mithril.Shared.Game;
using Mithril.Shared.Logging;
using Microsoft.Extensions.Hosting;

namespace Mithril.GameState.Areas;

/// <summary>
/// Holds the player's current area code, parsed from
/// <c>LOADING LEVEL Area&lt;Name&gt;</c> log lines via
/// <see cref="AreaTransitionParser"/>. Shared live game-state: consumed by
/// Gandalf (chest commits stamp <c>LearnedChest.Area</c>) and Legolas
/// (per-area survey-projection calibration), among others.
///
/// <para><b>Self-feeding via the L1 driver (#556 Phase 2).</b> When an
/// <see cref="ILogStreamDriver"/> is supplied, the tracker is a
/// <see cref="BackgroundService"/> that subscribes to L0.5's
/// <see cref="ISystemSignalLogStream"/> typed pipe through the L1 driver,
/// filtered for <see cref="SystemSignalKind.AreaLoading"/>. The shared
/// state then updates without any other GameState producer feeding it via
/// <see cref="Observe(RawLogLine)"/>. Both feed paths update the same
/// state idempotently: the area key is string-compared, last-writer-wins,
/// so a double-feed during the Phase 3 migration window is safe. The
/// <see cref="Observe(RawLogLine)"/> overload retires in Phase 3's final
/// PR (once Pin/Weather/Position move to the unified pipe).</para>
///
/// <para><b>Startup seeding.</b> <see cref="PlayerLogTailReader.SeedToSessionStart"/>
/// rewinds the live replay window to the most recent <c>ProcessAddPlayer(</c>
/// line, which lands ~9 s <em>after</em> the <c>LOADING LEVEL</c> line for the
/// current area — so the area code is just upstream of where playback
/// begins. <see cref="SeedFromLog"/> closes that gap with its own one-shot
/// reverse-scan for the most recent <c>LOADING LEVEL</c> line, applied
/// before the live stream starts. Local change scope; no impact on other
/// consumers tuned against <see cref="PlayerLogStream"/>'s seed.</para>
///
/// <para><b>Threading.</b> <see cref="Observe(string,DateTime)"/> is called
/// from the log ingestion background thread; the L1 self-feed runs on the
/// driver's pump thread; <see cref="CurrentArea"/> is read from
/// chest-commit paths on whichever thread routes the bracket. A simple
/// lock suffices — the contention is low (one area transition per
/// minute-ish, vs. dozens of reads per chest interaction).</para>
/// </summary>
public sealed class PlayerAreaTracker : BackgroundService
{
    private readonly AreaTransitionParser _parser;
    private readonly IDiagnosticsSink? _diag;
    private readonly GameConfig? _config;
    private readonly ILogStreamDriver? _driver;
    private readonly object _lock = new();
    private readonly object _seedLock = new();
    private bool _seedAttempted;
    private string? _currentArea;
    private ILogSubscription? _subscription;

    private const string DiagCategory = "GameState.Area";

    /// <param name="config">Optional. When supplied, the tracker owns its own
    /// one-shot pre-login-preamble seed (lazily, on the first
    /// <see cref="CurrentArea"/> read or <see cref="Observe(string,DateTime)"/>)
    /// — consumers no longer trigger <see cref="SeedFromLog"/>. Null in tests
    /// that drive state directly.</param>
    /// <param name="driver">Optional L1 subscription driver. When supplied,
    /// <see cref="ExecuteAsync"/> subscribes to the L0.5 system-signal pipe
    /// and self-feeds <see cref="SystemSignalKind.AreaLoading"/> envelopes
    /// (#556 Phase 2). Null in tests that drive state directly via
    /// <see cref="Observe(string,DateTime)"/>.</param>
    public PlayerAreaTracker(
        AreaTransitionParser parser,
        IDiagnosticsSink? diag = null,
        GameConfig? config = null,
        ILogStreamDriver? driver = null)
    {
        _parser = parser;
        _diag = diag;
        _config = config;
        _driver = driver;
    }

    /// <summary>
    /// Latest area key parsed from a <c>LOADING LEVEL Area*</c> line, or
    /// <c>null</c> if the player is at character-select / disconnected /
    /// before the first observed transition. Consumers should treat
    /// <c>null</c> as "current area is unknown" — chest commits during a
    /// null-area window persist with <c>Area = null</c> and self-heal on
    /// the next portal.
    /// </summary>
    public string? CurrentArea
    {
        get { EnsureSeeded(); lock (_lock) return _currentArea; }
    }

    /// <summary>
    /// Feed one log line through the area parser. Idempotent for unrelated
    /// lines (the parser's substring fast-path returns null without touching
    /// state).
    /// </summary>
    public void Observe(string line, DateTime timestamp)
    {
        EnsureSeeded();
        Apply(line, timestamp);
    }

    /// <summary>
    /// Hosted-service entry point (#556 Phase 2). When an
    /// <see cref="ILogStreamDriver"/> is configured, subscribes to L0.5's
    /// system-signal pipe and folds every
    /// <see cref="SystemSignalKind.AreaLoading"/> envelope into the shared
    /// area state. When no driver is supplied (tests that drive state
    /// directly), this method returns immediately — the
    /// <see cref="Observe(string,DateTime)"/> path remains the source of
    /// truth for those scenarios.
    ///
    /// <para>Per-envelope failures are contained by the L1 driver (the
    /// <c>InlineBridge</c> per-handler try/catch + degraded-state SM); the
    /// handler body itself is intentionally tiny and unlikely to throw —
    /// <see cref="Apply"/> swallows non-matching lines via the parser's
    /// substring fast-path.</para>
    /// </summary>
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (_driver is null)
        {
            // Test path / no L1 driver — nothing to subscribe to. Park so
            // the host's StartAsync await unblocks cleanly; the host will
            // cancel on shutdown and this method unwinds.
            try { await Task.Delay(Timeout.Infinite, stoppingToken).ConfigureAwait(false); }
            catch (OperationCanceledException) { }
            return;
        }

        _diag?.Info(DiagCategory,
            "Subscribing to L1 driver (SystemSignal pipe) for AreaLoading self-feed");

        // Lazy-seed before the live stream starts so the reverse-scan
        // catches a LOADING LEVEL line that sits upstream of the L1
        // replay window. Mirrors the pre-#556 CurrentArea-read self-seed,
        // just driven once at startup.
        EnsureSeeded();

        _subscription = _driver.Subscribe<SystemSignalLogLine>(
            envelope =>
            {
                var s = envelope.Payload;
                // The unified pipe (and the typed system pipe) carries the
                // full SystemSignalKind set; ignore everything except
                // AreaLoading. PG only emits one transition per actual
                // portal, so this filter is cheap.
                if (s.Kind == SystemSignalKind.AreaLoading)
                {
                    // SystemSignalLogLine.Data is the "LOADING LEVEL <area>"
                    // tail (with the [ts] prefix eaten by L0.5). The
                    // parser's substring guard handles unrelated content
                    // as no-op, so feeding raw Data is safe.
                    Apply(s.Data, s.Timestamp.UtcDateTime);
                }
                return ValueTask.CompletedTask;
            },
            new LogSubscriptionOptions
            {
                ReplayMode = ReplayMode.FromSessionStart,
                DeliveryContext = DeliveryContext.Inline,
                DiagnosticCategory = DiagCategory,
            });

        // Park until host stop; the L1 subscription runs its own pump on
        // a Task.Run, so ExecuteAsync's only job after Subscribe is to
        // dispose the handle on shutdown.
        try { await Task.Delay(Timeout.Infinite, stoppingToken).ConfigureAwait(false); }
        catch (OperationCanceledException) { /* expected on host stop */ }
        finally
        {
            _subscription?.Dispose();
            _subscription = null;
        }
    }

    public override void Dispose()
    {
        _subscription?.Dispose();
        _subscription = null;
        base.Dispose();
    }

    private void Apply(string line, DateTime timestamp)
    {
        if (_parser.TryParse(line, timestamp) is AreaTransitionEvent evt)
        {
            lock (_lock)
            {
                if (_currentArea != evt.AreaKey)
                {
                    _currentArea = evt.AreaKey;
                    _diag?.Trace("GameState.Area",
                        $"Player area transition → {evt.AreaKey ?? "(none)"} at {timestamp:O}");
                }
            }
        }
    }

    /// <summary>
    /// Idempotent, owned, one-shot pre-login-preamble seed. Runs at most once
    /// per instance (lazily, on the first <see cref="CurrentArea"/> read or
    /// <see cref="Observe(string,DateTime)"/>), so consumers never trigger the
    /// scan and a new consumer can't forget to. With no log path / no
    /// <c>LOADING LEVEL</c> found, "area unknown" is surfaced once rather than
    /// being a silent null. See mithril#514.
    /// </summary>
    private void EnsureSeeded()
    {
        lock (_seedLock)
        {
            if (_seedAttempted) return;
            _seedAttempted = true;
        }

        var path = _config?.PlayerLogPath;
        if (!string.IsNullOrEmpty(path)) ScanForArea(path);

        bool unknown;
        lock (_lock) unknown = _currentArea is null;
        if (unknown)
            _diag?.Info("GameState.Area",
                "Area unknown after one-shot preamble seed (no LOADING LEVEL " +
                "found / no log path) — null until the first live transition");
    }

    /// <summary>
    /// One-shot startup seed. Reads <paramref name="logPath"/> backward in
    /// chunks looking for the most recent <c>LOADING LEVEL</c> line, parses
    /// it, and sets <see cref="CurrentArea"/>. No-op if the file is missing
    /// or no <c>LOADING LEVEL</c> line exists in the scanned region.
    /// </summary>
    /// <remarks>
    /// Scan bound: <see cref="ScanChunkBytes"/> (10 MB, mirrored from
    /// <see cref="PlayerLogTailReader.SessionScanChunkBytes"/>) per chunk.
    /// The full file is walked in chunks until the marker is found or the
    /// start of the file is reached, so the scan is bounded by file size,
    /// not the chunk constant.
    /// </remarks>
    /// <summary>
    /// Explicit one-shot seed. Retained for tests and any caller that already
    /// holds the log path; marks the seed attempted so the lazy
    /// <see cref="EnsureSeeded"/> path won't re-scan. Production consumers do
    /// <b>not</b> call this — the tracker self-seeds (mithril#514).
    /// </summary>
    public void SeedFromLog(string logPath)
    {
        lock (_seedLock) _seedAttempted = true;
        ScanForArea(logPath);
    }

    private void ScanForArea(string logPath)
    {
        if (!File.Exists(logPath)) return;

        var size = new FileInfo(logPath).Length;
        if (size == 0) return;

        try
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
                    var line = text.Substring(startInChunk, lineEnd - startInChunk).TrimEnd('\r');

                    // Best-effort timestamp. The line itself may carry an
                    // "[HH:MM:SS]" prefix but PlayerLogTailReader strips it
                    // before normal parsing; for the seed we only care about
                    // the AreaKey, not the timestamp, so wall-clock-now is fine.
                    Apply(line, DateTime.UtcNow);
                    return;
                }
                if (scanFrom == 0) break;
                end = scanFrom + overlap;
            }
        }
        catch (IOException ex)
        {
            _diag?.Warn("GameState.Area", $"SeedFromLog failed: {ex.Message}");
        }
    }

    private const string Marker = "LOADING LEVEL";
    private const int ScanChunkBytes = 10 * 1024 * 1024;
}
