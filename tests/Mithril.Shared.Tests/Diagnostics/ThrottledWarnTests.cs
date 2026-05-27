using System.IO;
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
    private sealed class FakeClock : TimeProvider
    {
        public DateTimeOffset Now = DateTimeOffset.UnixEpoch;
        public override DateTimeOffset GetUtcNow() => Now;
    }

    private static (ThrottledWarn warn, DiagnosticsLoggerProvider provider, FakeClock clock) Make(
        double windowSeconds = 5)
    {
        var provider = new DiagnosticsLoggerProvider(
            Path.Combine(Path.GetTempPath(), "mithril-throttled-warn-" + Guid.NewGuid()));
        var clock = new FakeClock();
        var warn = new ThrottledWarn(
            provider.CreateLogger("Cat"),
            "Cat",
            TimeSpan.FromSeconds(windowSeconds),
            clock);
        return (warn, provider, clock);
    }

    private static List<DiagnosticEntry> WarnEntries(DiagnosticsLoggerProvider provider) =>
        provider.Snapshot()
            .Where(e => e.Level == DiagnosticLevel.Warn && e.Category == "Cat")
            .ToList();

    [Fact]
    public void First_Warn_Emits_Immediately_As_Warn_Level()
    {
        var (warn, provider, _) = Make();

        warn.Warn("boom");

        WarnEntries(provider).Should().ContainSingle()
            .Which.Message.Should().Be("boom");
    }

    [Fact]
    public void Subsequent_Warns_Within_Window_Are_Suppressed()
    {
        var (warn, provider, clock) = Make();

        warn.Warn("first");
        clock.Now += TimeSpan.FromSeconds(1);
        warn.Warn("second");
        clock.Now += TimeSpan.FromSeconds(3);
        warn.Warn("third");

        WarnEntries(provider).Should().ContainSingle().Which.Message.Should().Be("first");
    }

    [Fact]
    public void After_Window_Next_Warn_Emits_With_Suppressed_Rollup()
    {
        var (warn, provider, clock) = Make(windowSeconds: 5);

        warn.Warn("a");                    // emits
        clock.Now += TimeSpan.FromSeconds(1);
        warn.Warn("b");                    // suppressed
        warn.Warn("c");                    // suppressed
        clock.Now += TimeSpan.FromSeconds(5);
        warn.Warn("d");                    // window elapsed → emits with rollup

        var entries = WarnEntries(provider);
        entries.Should().HaveCount(2);
        entries[0].Message.Should().Be("a");
        entries[1].Message.Should().Be("d (+2 similar suppressed in last 5s)");
    }

    [Fact]
    public void Emit_After_Window_With_No_Suppression_Has_No_Rollup_Suffix()
    {
        var (warn, provider, clock) = Make(windowSeconds: 5);

        warn.Warn("a");
        clock.Now += TimeSpan.FromSeconds(6);
        warn.Warn("b");

        WarnEntries(provider).Select(e => e.Message).Should().Equal("a", "b");
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
        var (warn, provider, clock) = Make(windowSeconds: 2);

        warn.Warn("e1");                       // emit
        warn.Warn("x");                        // suppressed (1)
        clock.Now += TimeSpan.FromSeconds(2);
        warn.Warn("e2");                       // emit, rollup +1
        warn.Warn("y");                        // suppressed (1, counter had reset)
        clock.Now += TimeSpan.FromSeconds(2);
        warn.Warn("e3");                       // emit, rollup +1 (not +2)

        WarnEntries(provider).Select(e => e.Message).Should().Equal(
            "e1",
            "e2 (+1 similar suppressed in last 2s)",
            "e3 (+1 similar suppressed in last 2s)");
    }
}
