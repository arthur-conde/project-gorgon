using System.ComponentModel;
using FluentAssertions;
using Xunit;

namespace Silmarillion.Tests;

public sealed class SilmarillionSettingsTests
{
    [Fact]
    public void Defaults_StampSchemaVersion1_AndCap12()
    {
        var s = new SilmarillionSettings();
        s.SchemaVersion.Should().Be(1);
        s.UsedInChipCap.Should().Be(SilmarillionSettings.DefaultUsedInChipCap);
        SilmarillionSettings.DefaultUsedInChipCap.Should().Be(12);
    }

    [Fact]
    public void UsedInChipCap_ClampsBelowZero_ToZero()
    {
        var s = new SilmarillionSettings { UsedInChipCap = -5 };
        s.UsedInChipCap.Should().Be(0);
    }

    [Fact]
    public void UsedInChipCap_ClampsAboveMax_ToMax()
    {
        var s = new SilmarillionSettings { UsedInChipCap = 9999 };
        s.UsedInChipCap.Should().Be(SilmarillionSettings.MaxUsedInChipCap);
    }

    [Fact]
    public void UsedInChipCap_AcceptsZero_AsAllCollapsed()
    {
        var s = new SilmarillionSettings { UsedInChipCap = 0 };
        s.UsedInChipCap.Should().Be(0);
    }

    [Fact]
    public void UsedInChipCap_RaisesPropertyChanged_OnChange()
    {
        var s = new SilmarillionSettings();
        var fired = new List<string?>();
        s.PropertyChanged += (_, e) => fired.Add(e.PropertyName);

        s.UsedInChipCap = 24;

        fired.Should().Contain(nameof(SilmarillionSettings.UsedInChipCap));
    }

    [Fact]
    public void UsedInChipCap_DoesNotRaise_WhenValueUnchanged()
    {
        var s = new SilmarillionSettings { UsedInChipCap = 24 };
        var fired = new List<string?>();
        s.PropertyChanged += (_, e) => fired.Add(e.PropertyName);

        s.UsedInChipCap = 24;

        fired.Should().BeEmpty();
    }
}
