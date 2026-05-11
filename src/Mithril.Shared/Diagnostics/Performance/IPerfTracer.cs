namespace Mithril.Shared.Diagnostics.Performance;

/// <summary>
/// Opt-in performance trace recorder. When <see cref="IsActive"/> is false
/// every emit call short-circuits, so callers can sprinkle <see cref="Scope"/>
/// usages and emit calls without worrying about cost in the steady state.
///
/// A session is started/stopped externally — typically via
/// <c>StartPerfTraceHotkey</c> — and writes JSON-lines events to a
/// per-session file under <c>%LocalAppData%/Mithril/Shell/perf/</c>.
/// </summary>
public interface IPerfTracer
{
    /// <summary>True while a recording session is in progress.</summary>
    bool IsActive { get; }

    /// <summary>Full path of the file the current session is writing to, or null when inactive.</summary>
    string? CurrentSessionPath { get; }

    /// <summary>Start a new session. Idempotent — calling while active is a no-op.</summary>
    void StartSession(SessionHeader header);

    /// <summary>Stop the current session and flush. Idempotent.</summary>
    void StopSession();

    /// <summary>Fires whenever <see cref="IsActive"/> flips. Consumers should
    /// re-read <see cref="IsActive"/> rather than tracking a payload — keeps
    /// state-of-the-world unambiguous when start/stop fire close together.</summary>
    event EventHandler? IsActiveChanged;

    /// <summary>Begin a timing scope. Returns a default (no-op) struct when inactive.</summary>
    PerfScope Scope(string name, object? tags = null);

    void EmitFrameSummary(int count, double meanMs, double p50Ms, double p95Ms, double maxMs, int stallCount);

    /// <summary>
    /// Emit a single frame event. <paramref name="attribution"/> is only meaningful when
    /// <paramref name="stall"/> is true — for non-stall frames pass null and the property
    /// is omitted from the JSON so verbose-frame mode doesn't bloat every record.
    /// </summary>
    void EmitFrame(double intervalMs, bool stall, string? currentOp, string? attribution);
    void EmitDispatcher(string priority, double waitMs, double runMs, int queueDepthAtStart);
    void EmitCounter(long workingSetMB, int gen0, int gen1, int gen2, int threads, int handles, int dispatcherQueueDepth);
    void EmitGc(int generation, double durationMs);
    void EmitBindingError(string message);
    void EmitInputLatency(string kind, double latencyMs);
    void EmitModuleActivated(string moduleId, double durationMs);
    void EmitRefFetch(string file, bool cacheHit, double durationMs, long bytes);

    /// <summary>
    /// Emit a manual scope event. <see cref="PerfScope.Dispose"/> calls this;
    /// callers can also invoke it directly when they have a duration already
    /// in hand (e.g. forwarding a timing from a layer that wasn't structured
    /// around <c>using</c>).
    /// </summary>
    void EmitScope(string name, double durationMs, object? tags);
}
