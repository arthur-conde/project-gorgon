namespace Mithril.Shared.Diagnostics.Performance;

/// <summary>
/// Classifies a stall as <see cref="Dispatcher"/> (UI thread was busy
/// running ops during the bad frame interval) or <see cref="NonDispatcher"/>
/// (UI thread was idle — the cause lives elsewhere: GC pause, GPU/DWM hitch,
/// blocked native call, etc.). Pure function so the threshold has a unit
/// test and the hosted service stays a thin orchestrator.
///
/// The threshold (default 20 ms over a 200 ms window) is empirical: ops
/// that total less than ~20 ms can't realistically be the cause of a
/// >33 ms frame stall on their own — something else stole the time.
/// </summary>
public static class StallAttribution
{
    public const double DispatcherThresholdMs = 20.0;
    public const double WindowMs = 200.0;
    public const string Dispatcher = "dispatcher";
    public const string NonDispatcher = "non-dispatcher";

    public static string Classify(double dispatcherRunMsInWindow, double thresholdMs = DispatcherThresholdMs)
        => dispatcherRunMsInWindow >= thresholdMs ? Dispatcher : NonDispatcher;
}
