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

    // PR B additions — Arda + module-discovery + refdata-outcome + GameState counters.
    public const string ArdaBatch = "arda_batch";
    public const string ArdaDispatch = "arda_dispatch";
    public const string ArdaWorldDriver = "arda_world_driver";
    public const string ArdaCompose = "arda_compose";
    public const string ModuleDiscover = "module_discover";
    public const string GateOpen = "gate_open";
    public const string ViewResolve = "view_resolve";
    public const string MeterCounter = "meter_counter";
}
