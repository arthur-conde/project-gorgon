using System.Linq;
using FluentAssertions;
using Mithril.Shared.Diagnostics.Telemetry;
using Mithril.Shared.Telemetry.Abstractions;
using Xunit;

namespace Mithril.Shared.Tests.Telemetry;

public class MithrilSharedTagDescriptorsTests
{
    [Fact]
    public void All_descriptors_have_non_empty_key_and_subsystem()
    {
        var provider = new MithrilSharedTagDescriptors();
        foreach (var d in provider.Describe())
        {
            d.Key.Should().NotBeNullOrEmpty();
            d.Subsystem.Should().NotBeNullOrEmpty();
        }
    }

    [Fact]
    public void Describe_is_non_empty()
    {
        new MithrilSharedTagDescriptors().Describe().Should().NotBeEmpty();
    }

    [Fact]
    public void Keys_are_unique_within_provider()
    {
        var provider = new MithrilSharedTagDescriptors();
        var keys = provider.Describe().Select(d => d.Key).ToArray();
        keys.Should().OnlyHaveUniqueItems();
    }

    [Fact]
    public void All_subsystems_use_Mithril_prefix()
    {
        var provider = new MithrilSharedTagDescriptors();
        foreach (var d in provider.Describe())
        {
            d.Subsystem.Should().StartWith("Mithril.");
        }
    }
}
