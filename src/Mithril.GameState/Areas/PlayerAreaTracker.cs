using System.IO;
using System.Text;
using Mithril.GameState.Areas.Parsing;
using Mithril.Shared.Diagnostics;
using Mithril.Shared.Logging;

namespace Mithril.GameState.Areas;

/// <summary>
/// Holds the player's current area code, parsed from
/// <c>LOADING LEVEL Area&lt;Name&gt;</c> log lines via
/// <see cref="AreaTransitionParser"/>. Shared live game-state: consumed by
/// Gandalf (chest commits stamp <c>LearnedChest.Area</c>) and Legolas
/// (per-area survey-projection calibration), among others.
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
/// <para><b>Threading.</b> <see cref="Observe"/> is called from the log
/// ingestion background thread; <see cref="CurrentArea"/> is read from
/// chest-commit paths on whichever thread routes the bracket. A simple
/// lock suffices — the contention is low (one area transition per
/// minute-ish, vs. dozens of reads per chest interaction).</para>
/// </summary>
public sealed class PlayerAreaTracker
{
    private readonly AreaTransitionParser _parser;
    private readonly IDiagnosticsSink? _diag;
    private readonly object _lock = new();
    private string? _currentArea;

    public PlayerAreaTracker(AreaTransitionParser parser, IDiagnosticsSink? diag = null)
    {
        _parser = parser;
        _diag = diag;
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
        get { lock (_lock) return _currentArea; }
    }

    /// <summary>
    /// Feed one log line through the area parser. Idempotent for unrelated
    /// lines (the parser's substring fast-path returns null without touching
    /// state).
    /// </summary>
    public void Observe(string line, DateTime timestamp)
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

    public void Observe(RawLogLine raw) => Observe(raw.Line, raw.Timestamp);

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
    public void SeedFromLog(string logPath)
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
                    Observe(line, DateTime.UtcNow);
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
