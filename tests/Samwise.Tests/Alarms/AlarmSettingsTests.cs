using System.Text.Json;
using FluentAssertions;
using Samwise.Alarms;
using Samwise.State;
using Xunit;

namespace Samwise.Tests.Alarms;

public class AlarmSettingsTests
{
    [Fact]
    public void NewRule_LoopDefaultsToFalse()
    {
        new StageAlarmRule().Loop.Should().BeFalse();
    }

    [Fact]
    public void NewRule_ChannelIdDefaultsToDefault()
    {
        new StageAlarmRule().ChannelId.Should().Be("default");
    }

    [Fact]
    public void StageAlarmRule_JsonRoundTrip_PreservesLoopAndChannel()
    {
        var settings = new SamwiseSettings();
        settings.Alarms.Rules[PlotStage.Ripe].Loop = true;
        settings.Alarms.Rules[PlotStage.Ripe].ChannelId = "custom-channel-id";

        var json = JsonSerializer.Serialize(settings, SamwiseSettingsJsonContext.Default.SamwiseSettings);
        var deserialized = JsonSerializer.Deserialize(json, SamwiseSettingsJsonContext.Default.SamwiseSettings)!;

        var ripe = deserialized.Alarms.Rules[PlotStage.Ripe];
        ripe.Loop.Should().BeTrue();
        ripe.ChannelId.Should().Be("custom-channel-id");
    }
}
