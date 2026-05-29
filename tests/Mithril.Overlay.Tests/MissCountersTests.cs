using System.Diagnostics.Metrics;
using FluentAssertions;
using Mithril.MapCalibration;
using Mithril.Overlay.Internal;
using Mithril.Shared.Diagnostics.Telemetry;
using Xunit;

namespace Mithril.Overlay.Tests;

/// <summary>
/// M3 + m1 telemetry guards. Verifies that the
/// <c>MithrilMeters.Overlay.DispatchMisses</c> counter fires when a marker's
/// style has no registered drawer. (The ProjectionMisses counter fires from
/// inside the production OnSurfaceRender path, which can't be unit-tested
/// without a D3D surface — covered manually + via the same MeterListener
/// idiom in a future migration-PR test once a real consumer exists.)
///
/// <para>The counter is a process-static, and other tests in the suite
/// (notably the contention test) also produce miss events. The listener
/// therefore filters by <c>style_type</c> tag matching this test's
/// uniquely-named TestStyle so cross-test pollution doesn't perturb the
/// assertion.</para>
/// </summary>
public sealed class MissCountersTests
{
    // Unique name keeps this test's misses identifiable in the global stream.
    private sealed record MissCountersTestStyle(string Name) : IMarkerStyle;

    [Fact]
    public void Dispatch_misses_counter_fires_per_unregistered_style_marker()
    {
        var renderer = new MarkerSceneRenderer();
        // Deliberately register NO drawer for MissCountersTestStyle — every
        // marker should miss.

        var targetTypeName = typeof(MissCountersTestStyle).FullName!;
        long observedDispatchMisses = 0;

        using var listener = new MeterListener
        {
            InstrumentPublished = (instr, l) =>
            {
                if (instr.Meter.Name == "Mithril.Overlay"
                    && instr.Name == "mithril.overlay.dispatch.misses")
                {
                    l.EnableMeasurementEvents(instr);
                }
            },
        };
        listener.SetMeasurementEventCallback<long>((_, measurement, tags, _) =>
        {
            // Filter on style_type to isolate this test's misses from any
            // parallel test's dispatch counter increments.
            foreach (var kv in tags)
            {
                if (kv.Key == "style_type" && (kv.Value as string) == targetTypeName)
                {
                    Interlocked.Add(ref observedDispatchMisses, measurement);
                    return;
                }
            }
        });
        listener.Start();

        var markers = new List<(PixelPoint, IMarkerStyle)>
        {
            (new PixelPoint(0, 0), new MissCountersTestStyle("a")),
            (new PixelPoint(1, 1), new MissCountersTestStyle("b")),
            (new PixelPoint(2, 2), new MissCountersTestStyle("a")),
        };

        renderer.Render(markers, null!, null!, null!);

        observedDispatchMisses.Should().Be(3,
            "every marker missed (no drawer registered) and the counter should tick once per miss");
    }
}
