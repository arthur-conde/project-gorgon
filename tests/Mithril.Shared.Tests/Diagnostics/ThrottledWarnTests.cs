using FluentAssertions;
using Mithril.Shared.Diagnostics;
using Xunit;

namespace Mithril.Shared.Tests.Diagnostics;

/// <summary>
/// Pins <see cref="ThrottledWarn"/>: a log ingestion loop must stay alive on a
/// bad line (mithril#512) but a pathological run of bad lines must not flood
/// the unbuffered sink (mithril#507). One Warn per window, suppressed count
/// rolled into the next emission.
/// </summary>
public class ThrottledWarnTests
{
    private sealed class CapturingSink : IDiagnosticsSink
    {
        public List<(DiagnosticLevel Level, string Category, string Message)> Entries { get; } = new();
        public void Write(DiagnosticLevel level, string category, string message) =>
            Entries.Add((level, category, message));
        public IReadOnlyList<DiagnosticEntry> Snapshot() => Array.Empty<DiagnosticEntry>();
        public event EventHandler<DiagnosticEntry>? EntryAdded { add { } remove { } }
    }

    private sealed class FakeClock : TimeProvider
    {
        public DateTimeOffset Now = DateTimeOffset.UnixEpoch;
        public override DateTimeOffset GetUtcNow() => Now;
    }

    private static (ThrottledWarn warn, CapturingSink sink, FakeClock clock) Make(
        double windowSeconds = 5)
    {
        var sink = new CapturingSink();
        var clock = new FakeClock();
        var warn = new ThrottledWarn(sink, "Cat", TimeSpan.FromSeconds(windowSeconds), clock);
        return (warn, sink, clock);
    }

    [Fact]
    public void First_Warn_Emits_Immediately_As_Warn_Level()
    {
        var (warn, sink, _) = Make();

        warn.Warn("boom");

        sink.Entries.Should().ContainSingle()
            .Which.Should().Be((DiagnosticLevel.Warn, "Cat", "boom"));
    }

    [Fact]
    public void Subsequent_Warns_Within_Window_Are_Suppressed()
    {
        var (warn, sink, clock) = Make();

        warn.Warn("first");
        clock.Now += TimeSpan.FromSeconds(1);
        warn.Warn("second");
        clock.Now += TimeSpan.FromSeconds(3);
        warn.Warn("third");

        sink.Entries.Should().ContainSingle().Which.Message.Should().Be("first");
    }

    [Fact]
    public void After_Window_Next_Warn_Emits_With_Suppressed_Rollup()
    {
        var (warn, sink, clock) = Make(windowSeconds: 5);

        warn.Warn("a");                    // emits
        clock.Now += TimeSpan.FromSeconds(1);
        warn.Warn("b");                    // suppressed
        warn.Warn("c");                    // suppressed
        clock.Now += TimeSpan.FromSeconds(5);
        warn.Warn("d");                    // window elapsed → emits with rollup

        sink.Entries.Should().HaveCount(2);
        sink.Entries[0].Message.Should().Be("a");
        sink.Entries[1].Message.Should().Be("d (+2 similar suppressed in last 5s)");
    }

    [Fact]
    public void Emit_After_Window_With_No_Suppression_Has_No_Rollup_Suffix()
    {
        var (warn, sink, clock) = Make(windowSeconds: 5);

        warn.Warn("a");
        clock.Now += TimeSpan.FromSeconds(6);
        warn.Warn("b");

        sink.Entries.Select(e => e.Message).Should().Equal("a", "b");
    }

    [Fact]
    public void Null_Sink_Is_A_Silent_No_Op()
    {
        var warn = new ThrottledWarn(null, "Cat");

        var act = () => warn.Warn("nobody listening");

        act.Should().NotThrow();
    }

    [Fact]
    public void Suppressed_Counter_Resets_After_Each_Emission()
    {
        var (warn, sink, clock) = Make(windowSeconds: 2);

        warn.Warn("e1");                       // emit
        warn.Warn("x");                        // suppressed (1)
        clock.Now += TimeSpan.FromSeconds(2);
        warn.Warn("e2");                       // emit, rollup +1
        warn.Warn("y");                        // suppressed (1, counter had reset)
        clock.Now += TimeSpan.FromSeconds(2);
        warn.Warn("e3");                       // emit, rollup +1 (not +2)

        sink.Entries.Select(e => e.Message).Should().Equal(
            "e1",
            "e2 (+1 similar suppressed in last 2s)",
            "e3 (+1 similar suppressed in last 2s)");
    }
}
