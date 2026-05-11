using System.Reflection;
using FluentAssertions;
using Mithril.Shared.Diagnostics.Performance;
using Xunit;

namespace Mithril.Shared.Tests;

public class BindingErrorTraceListenerTests
{
    // The listener is internal; use reflection rather than InternalsVisibleTo
    // so we don't widen the assembly surface for one test class.
    private static object CreateListener(IPerfTracer tracer)
    {
        var t = typeof(IPerfTracer).Assembly.GetType("Mithril.Shared.Diagnostics.Performance.BindingErrorTraceListener", throwOnError: true)!;
        return Activator.CreateInstance(t, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance, null, new object[] { tracer }, null)!;
    }

    private static void Write(object listener, string message)
        => listener.GetType().GetMethod("WriteLine", new[] { typeof(string) })!.Invoke(listener, [message]);

    private static bool WouldEmit(object listener, string message)
        => (bool)listener.GetType().GetMethod("WouldEmit", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(listener, [message])!;

    [Fact]
    public void Inactive_tracer_swallows_messages_without_recording()
    {
        var tracer = new FakePerfTracer { IsActive = false };
        var listener = CreateListener(tracer);
        Write(listener, "System.Windows.Data Error: 40 : BindingExpression path error");
        Write(listener, "System.Windows.Data Error: 40 : BindingExpression path error");
        tracer.BindingErrors.Should().BeEmpty();
    }

    [Fact]
    public void Duplicate_message_within_window_is_throttled()
    {
        var tracer = new FakePerfTracer { IsActive = true };
        var listener = CreateListener(tracer);
        var msg = "System.Windows.Data Error: 40 : BindingExpression path error";

        Write(listener, msg);
        Write(listener, msg);
        Write(listener, msg);

        tracer.BindingErrors.Should().ContainSingle("the first emit wins; subsequent duplicates inside the 1s window are dropped");
        WouldEmit(listener, msg).Should().BeFalse();
        WouldEmit(listener, "different message").Should().BeTrue(
            "throttling is per-message, not global");
    }

    private sealed class FakePerfTracer : IPerfTracer
    {
        public bool IsActive { get; set; }
        public string? CurrentSessionPath => null;
        public List<string> BindingErrors { get; } = new();

        public void StartSession(SessionHeader header) { }
        public void StopSession() { }
        public PerfScope Scope(string name, object? tags = null) => default;
        public void EmitFrameSummary(int count, double meanMs, double p50Ms, double p95Ms, double maxMs, int stallCount) { }
        public void EmitFrame(double intervalMs, bool stall, string? currentOp) { }
        public void EmitDispatcher(string priority, double waitMs, double runMs, int queueDepthAtStart) { }
        public void EmitCounter(long workingSetMB, int gen0, int gen1, int gen2, int threads, int handles, int dispatcherQueueDepth) { }
        public void EmitGc(int generation, double durationMs) { }
        public void EmitBindingError(string message) => BindingErrors.Add(message);
        public void EmitInputLatency(string kind, double latencyMs) { }
        public void EmitModuleActivated(string moduleId, double durationMs) { }
        public void EmitRefFetch(string file, bool cacheHit, double durationMs, long bytes) { }
        public void EmitScope(string name, double durationMs, object? tags) { }
    }
}
