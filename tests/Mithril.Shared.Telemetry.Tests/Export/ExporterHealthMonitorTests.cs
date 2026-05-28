using FluentAssertions;
using Mithril.Shared.Telemetry.Export;
using Xunit;

namespace Mithril.Shared.Telemetry.Tests.Export;

public class ExporterHealthMonitorTests
{
    [Fact]
    public void Initial_state_has_nulls()
    {
        using var m = new ExporterHealthMonitor();
        m.Current.LastSuccessUtc.Should().BeNull();
        m.Current.LastFailureUtc.Should().BeNull();
        m.Current.LastError.Should().BeNull();
    }

    [Fact]
    public void RecordSuccess_updates_Current_and_pushes_to_observers()
    {
        using var m = new ExporterHealthMonitor();
        ExporterHealth? observed = null;
        using var sub = m.Subscribe(h => observed = h);
        m.RecordSuccess();
        m.Current.LastSuccessUtc.Should().NotBeNull();
        observed.Should().NotBeNull();
        observed!.LastSuccessUtc.Should().NotBeNull();
    }

    [Fact]
    public void RecordFailure_carries_error_message()
    {
        using var m = new ExporterHealthMonitor();
        m.RecordFailure("Connection refused");
        m.Current.LastFailureUtc.Should().NotBeNull();
        m.Current.LastError.Should().Be("Connection refused");
    }

    [Fact]
    public void RecordSuccess_clears_LastError_but_preserves_LastFailureUtc()
    {
        using var m = new ExporterHealthMonitor();
        m.RecordFailure("transient blip");
        m.RecordSuccess();
        m.Current.LastError.Should().BeNull();
        m.Current.LastFailureUtc.Should().NotBeNull(); // history preserved
        m.Current.LastSuccessUtc.Should().NotBeNull();
    }

    [Fact]
    public void Late_subscriber_sees_current_snapshot_immediately()
    {
        using var m = new ExporterHealthMonitor();
        m.RecordSuccess();
        ExporterHealth? observed = null;
        using var sub = m.Subscribe(h => observed = h);
        observed.Should().NotBeNull();
        observed!.LastSuccessUtc.Should().NotBeNull();
    }
}
