namespace Mithril.Shared.Diagnostics.Performance;

/// <summary>
/// String constants for the <c>kind</c> property on every perf event. Used by
/// the writer and by analysis tooling (jq queries, MCP filters) — keeping them
/// in one place avoids drift between producer and consumer.
/// </summary>
public static class PerfEventKinds
{
    public const string SessionHeader = "session_header";
    public const string Frame = "frame";
    public const string FrameSummary = "frame_summary";
    public const string Dispatcher = "dispatcher";
    public const string Stall = "stall";
    public const string Counter = "counter";
    public const string Gc = "gc";
    public const string BindingError = "binding_error";
    public const string InputLatency = "input_latency";
    public const string Scope = "scope";
    public const string ModuleActivated = "module_activated";
    public const string RefFetch = "ref_fetch";
}
