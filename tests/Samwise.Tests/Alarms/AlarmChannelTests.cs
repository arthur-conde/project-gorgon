using FluentAssertions;
using Samwise.Alarms;
using Xunit;

namespace Samwise.Tests.Alarms;

public class AlarmChannelTests
{
    [Fact]
    public void NewChannel_DefaultsToMix()
    {
        var c = new AlarmChannel();
        c.Collision.Should().Be(AlarmCollisionBehavior.Mix);
    }

    [Fact]
    public void NewChannel_HasNonEmptyGuidId()
    {
        var c1 = new AlarmChannel();
        var c2 = new AlarmChannel();
        c1.Id.Should().NotBeNullOrEmpty();
        c1.Id.Should().NotBe(c2.Id);
    }

    [Fact]
    public void SettingName_RaisesPropertyChanged()
    {
        var c = new AlarmChannel { Name = "Default" };
        var raised = new List<string>();
        c.PropertyChanged += (_, e) => raised.Add(e.PropertyName!);

        c.Name = "Renamed";

        raised.Should().Contain(nameof(AlarmChannel.Name));
    }

    [Fact]
    public void SettingCollision_RaisesPropertyChanged()
    {
        var c = new AlarmChannel();
        var raised = new List<string>();
        c.PropertyChanged += (_, e) => raised.Add(e.PropertyName!);

        c.Collision = AlarmCollisionBehavior.Replace;

        raised.Should().Contain(nameof(AlarmChannel.Collision));
    }
}
