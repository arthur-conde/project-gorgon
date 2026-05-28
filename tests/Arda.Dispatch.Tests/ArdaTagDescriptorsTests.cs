using System.Linq;
using Arda.Abstractions.Diagnostics;
using FluentAssertions;
using Mithril.Shared.Telemetry.Abstractions;
using Xunit;

namespace Arda.Dispatch.Tests;

public class ArdaTagDescriptorsTests
{
    [Fact]
    public void All_descriptors_have_non_empty_key_and_subsystem()
    {
        var provider = new ArdaTagDescriptors();
        foreach (var d in provider.Describe())
        {
            d.Key.Should().NotBeNullOrEmpty();
            d.Subsystem.Should().NotBeNullOrEmpty();
        }
    }

    [Fact]
    public void Describe_is_non_empty()
    {
        new ArdaTagDescriptors().Describe().Should().NotBeEmpty();
    }

    [Fact]
    public void Keys_are_unique_within_provider()
    {
        var provider = new ArdaTagDescriptors();
        var keys = provider.Describe().Select(d => d.Key).ToArray();
        keys.Should().OnlyHaveUniqueItems();
    }

    [Fact]
    public void All_subsystems_use_Mithril_Arda_prefix()
    {
        var provider = new ArdaTagDescriptors();
        foreach (var d in provider.Describe())
        {
            d.Subsystem.Should().StartWith("Mithril.Arda");
        }
    }
}
