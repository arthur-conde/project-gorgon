using System.Collections.Concurrent;
using FluentAssertions;
using Mithril.Shared.Telemetry.Settings;
using Xunit;

namespace Mithril.Shared.Telemetry.Tests.Settings;

public class NotifyPropertyChangedOptionsMonitorTests
{
    [Fact]
    public void CurrentValue_and_Get_return_the_wrapped_singleton()
    {
        var settings = new TelemetrySettings();
        using var monitor = new NotifyPropertyChangedOptionsMonitor<TelemetrySettings>(settings);

        monitor.CurrentValue.Should().BeSameAs(settings);
        monitor.Get(name: null).Should().BeSameAs(settings);
        monitor.Get("anyName").Should().BeSameAs(settings);
    }

    [Fact]
    public void OnChange_fires_with_singleton_and_property_name()
    {
        var settings = new TelemetrySettings();
        using var monitor = new NotifyPropertyChangedOptionsMonitor<TelemetrySettings>(settings);

        TelemetrySettings? value = null;
        string? changedName = null;
        using var sub = monitor.OnChange((v, n) => { value = v; changedName = n; });

        settings.Endpoint = "http://localhost:4318/";

        value.Should().BeSameAs(settings);
        changedName.Should().Be(nameof(TelemetrySettings.Endpoint));
    }

    [Fact]
    public void OnChange_fires_for_collection_Touch()
    {
        // The settings UI mutates the TagExports / Headers dictionaries in place
        // then calls Touch(propertyName) to raise PropertyChanged. The monitor
        // must surface that as an OnChange too.
        var settings = new TelemetrySettings();
        using var monitor = new NotifyPropertyChangedOptionsMonitor<TelemetrySettings>(settings);

        var fired = false;
        using var sub = monitor.OnChange((_, n) =>
        {
            if (n == nameof(TelemetrySettings.TagExports)) fired = true;
        });

        settings.TagExports["module.id"] = true;
        settings.Touch(nameof(TelemetrySettings.TagExports));

        fired.Should().BeTrue();
    }

    [Fact]
    public void Disposing_subscription_detaches_only_that_listener()
    {
        var settings = new TelemetrySettings();
        using var monitor = new NotifyPropertyChangedOptionsMonitor<TelemetrySettings>(settings);

        var aCount = 0;
        var bCount = 0;
        var subA = monitor.OnChange((_, _) => aCount++);
        using var subB = monitor.OnChange((_, _) => bCount++);

        settings.ServiceName = "x";
        aCount.Should().Be(1);
        bCount.Should().Be(1);

        subA.Dispose();
        settings.ServiceName = "y";

        aCount.Should().Be(1, "disposed listener must not fire");
        bCount.Should().Be(2, "the other listener must keep firing");
    }

    [Fact]
    public void Same_delegate_subscribed_twice_disposing_one_leaves_the_other()
    {
        // Standard C# multicast `-=` removes a single matching invocation entry,
        // so subscribing the same delegate twice then disposing one subscription
        // must leave one live registration. Lock in that behaviour.
        var settings = new TelemetrySettings();
        using var monitor = new NotifyPropertyChangedOptionsMonitor<TelemetrySettings>(settings);

        var count = 0;
        Action<TelemetrySettings, string?> handler = (_, _) => count++;
        var sub1 = monitor.OnChange(handler);
        using var sub2 = monitor.OnChange(handler);

        sub1.Dispose();
        settings.ServiceName = "x";

        count.Should().Be(1,
            "disposing one of two subscriptions of the same delegate must leave exactly one live registration.");
    }

    [Fact]
    public void Disposing_monitor_detaches_from_PropertyChanged()
    {
        var settings = new TelemetrySettings();
        var monitor = new NotifyPropertyChangedOptionsMonitor<TelemetrySettings>(settings);

        var count = 0;
        monitor.OnChange((_, _) => count++);

        monitor.Dispose();
        settings.ServiceName = "after-dispose";

        count.Should().Be(0,
            "after the monitor is disposed it must unhook from the singleton's " +
            "PropertyChanged so it neither fires stale listeners nor leaks the singleton.");
    }

    [Fact]
    public void TagExports_dictionary_identity_is_preserved_for_live_reads()
    {
        // The scrubber reads CurrentValue.TagExports per record; the monitor must
        // never swap the dictionary reference away from the singleton's.
        var settings = new TelemetrySettings();
        using var monitor = new NotifyPropertyChangedOptionsMonitor<TelemetrySettings>(settings);

        ConcurrentDictionary<string, bool> live = monitor.CurrentValue.TagExports;
        live.Should().BeSameAs(settings.TagExports);

        settings.TagExports["arda.verb"] = false;
        monitor.CurrentValue.TagExports.Should().ContainKey("arda.verb");
    }
}
