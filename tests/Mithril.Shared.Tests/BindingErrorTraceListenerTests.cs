using System.Diagnostics;
using System.Reflection;
using FluentAssertions;
using Mithril.Shared.Diagnostics.Telemetry;
using Xunit;

namespace Mithril.Shared.Tests;

[Collection(TelemetryTestCollection.Name)]
public class BindingErrorTraceListenerTests
{
    // The listener is internal; use reflection rather than InternalsVisibleTo
    // so we don't widen the assembly surface for one test class.
    private static object CreateListener()
    {
        var t = typeof(MithrilActivitySources).Assembly.GetType(
            "Mithril.Shared.Diagnostics.Performance.BindingErrorTraceListener",
            throwOnError: true)!;
        return Activator.CreateInstance(t, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance, null, Array.Empty<object>(), null)!;
    }

    private static void Write(object listener, string message)
        => listener.GetType().GetMethod("WriteLine", new[] { typeof(string) })!.Invoke(listener, [message]);

    private static bool WouldEmit(object listener, string message)
        => (bool)listener.GetType().GetMethod("WouldEmit", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(listener, [message])!;

    private sealed record CapturedActivity(string SourceName, string OperationName, IReadOnlyList<KeyValuePair<string, object?>> Tags);

    private static (ActivityListener listener, List<CapturedActivity> log) CaptureActivities(string sourceName)
    {
        var log = new List<CapturedActivity>();
        var listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == sourceName,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllData,
            ActivityStopped = a => log.Add(new CapturedActivity(a.Source.Name, a.OperationName, a.TagObjects.ToList())),
        };
        ActivitySource.AddActivityListener(listener);
        return (listener, log);
    }

    [Fact]
    public void With_no_listener_attached_emit_does_not_throw_and_records_throttle_state()
    {
        // No ActivityListener — startActivity returns null and the listener
        // path short-circuits, but the throttle table still updates.
        var listener = CreateListener();
        Write(listener, "System.Windows.Data Error: 40 : BindingExpression path error");
        WouldEmit(listener, "System.Windows.Data Error: 40 : BindingExpression path error")
            .Should().BeFalse("first emit consumed the throttle window");
    }

    [Fact]
    public void Duplicate_message_within_window_is_throttled()
    {
        var (activityListener, log) = CaptureActivities(MithrilActivitySources.Wpf.Name);
        try
        {
            var listener = CreateListener();
            var msg = "System.Windows.Data Error: 40 : BindingExpression path error";

            Write(listener, msg);
            Write(listener, msg);
            Write(listener, msg);

            log.Should().ContainSingle("the first emit wins; subsequent duplicates inside the 1s window are dropped")
                .Which.OperationName.Should().Be("binding_error");
            WouldEmit(listener, msg).Should().BeFalse();
            WouldEmit(listener, "different message").Should().BeTrue("throttling is per-message, not global");
        }
        finally
        {
            activityListener.Dispose();
        }
    }
}
