namespace Mithril.Shared.Diagnostics.Performance;

/// <summary>
/// Opt-in perf-recording session lifecycle. While a session is active a
/// per-session JSON-lines file is written to <c>%LocalAppData%/Mithril/Shell/perf/</c>
/// from <see cref="System.Diagnostics.ActivitySource"/> + <see cref="System.Diagnostics.Metrics.Meter"/>
/// instruments produced across the solution.
///
/// Sessions are toggled externally — typically via <c>StartPerfTraceHotkey</c>
/// — so producers never see this interface; they emit unconditionally via
/// the BCL primitives in <see cref="Telemetry.MithrilActivitySources"/> /
/// <see cref="Telemetry.MithrilMeters"/>. Recording cost in the steady
/// state (no session attached) is one indirect <see cref="System.Diagnostics.ActivitySource"/>
/// volatile read per emit.
/// </summary>
public interface IPerfRecorder
{
    /// <summary>True while a recording session is in progress.</summary>
    bool IsActive { get; }

    /// <summary>Full path of the file the current session is writing to, or null when inactive.</summary>
    string? CurrentSessionPath { get; }

    /// <summary>Start a new session. Idempotent — calling while active is a no-op.</summary>
    void Start(SessionHeader header);

    /// <summary>Stop the current session and flush. Idempotent.</summary>
    void Stop();

    /// <summary>Fires whenever <see cref="IsActive"/> flips. Consumers should
    /// re-read <see cref="IsActive"/> rather than tracking a payload — keeps
    /// state-of-the-world unambiguous when start/stop fire close together.</summary>
    event EventHandler? IsActiveChanged;
}
